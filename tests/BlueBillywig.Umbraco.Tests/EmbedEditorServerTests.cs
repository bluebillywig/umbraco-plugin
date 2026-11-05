using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// The server half of the property editor additions: the live preview URL (the reference plugin's editor frames the player with
/// a session token so drafts render), the embargo fields the status banner needs, and the channel and shorts feature flags.
/// </summary>
public class EmbedEditorServerTests
{
    private static (VideoController Controller, FakeClient Client, FakeSettings Settings) Rig()
    {
        var client = new FakeClient();
        var settings = new FakeSettings();
        return (new VideoController(client, new LibraryService(client, settings)), client, settings);
    }

    // Preview --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_clip_preview_uses_the_editors_playout_and_the_read_only_token()
    {
        (VideoController controller, FakeClient client, FakeSettings settings) = Rig();
        client.ReadOnlyToken = "7-roHOTP";

        ClipPreviewResponseModel preview = Assert.IsType<ClipPreviewResponseModel>(Assert.IsType<OkObjectResult>(await controller.Preview(11, "clip", "sidebar", null, default)).Value);

        Assert.True(preview.Authenticated);
        Assert.Equal("https://demo.bbvms.com/p/sidebar/c/11.html?useSession=true&rpctoken=7-roHOTP", preview.Url);
        Assert.DoesNotContain(settings.Value.ApiSecret, preview.Url);
        Assert.DoesNotContain(client.Calls, c => c.Path.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("channel", "https://demo.bbvms.com/ch/11.html")]
    [InlineData("shorts", "https://demo.bbvms.com/sh/11.html")]
    public async Task Channel_and_shorts_previews_are_the_player_pages_with_the_token(string type, string page)
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.ReadOnlyToken = "7-roHOTP";

        ClipPreviewResponseModel preview = Assert.IsType<ClipPreviewResponseModel>(Assert.IsType<OkObjectResult>(await controller.Preview(11, type, null, null, default)).Value);

        Assert.Equal(page + "?useSession=true&rpctoken=7-roHOTP", preview.Url);
    }

    [Fact]
    public async Task Without_a_read_only_pair_the_preview_is_the_public_player_and_says_so()
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.ReadOnlyToken = null;

        ClipPreviewResponseModel preview = Assert.IsType<ClipPreviewResponseModel>(Assert.IsType<OkObjectResult>(await controller.Preview(11, "clip", null, null, default)).Value);

        Assert.False(preview.Authenticated);
        Assert.Equal("https://demo.bbvms.com/p/default/c/11.html", preview.Url);
        Assert.DoesNotContain("rpctoken", preview.Url);
    }

    [Fact]
    public async Task Another_publication_gets_its_public_player_and_never_the_token()
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.ReadOnlyToken = "7-roHOTP";

        ClipPreviewResponseModel preview = Assert.IsType<ClipPreviewResponseModel>(Assert.IsType<OkObjectResult>(await controller.Preview(11, "clip", null, "Other-Pub", default)).Value);

        Assert.False(preview.Authenticated);
        Assert.Equal("https://other-pub.bbvms.com/p/default/c/11.html", preview.Url);
    }

    [Theory]
    [InlineData(0L, "clip", null, null)]
    [InlineData(-4L, "clip", null, null)]
    [InlineData(1_000_000_000_000L, "clip", null, null)]
    [InlineData(11L, "playlist", null, null)]
    [InlineData(11L, "clip", "../x", null)]
    [InlineData(11L, "clip", null, "bad/host")]
    [InlineData(11L, "clip", null, "evil.com/x")]
    public async Task Invalid_input_is_a_400_and_nothing_is_minted(long id, string type, string? playout, string? publication)
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.ReadOnlyToken = "7-roHOTP";

        Assert.IsType<BadRequestObjectResult>(await controller.Preview(id, type, playout, publication, default));
    }

    // Banner data ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_clip_summary_carries_the_embargo_window_and_hides_the_value_sapi_leaves_after_a_removal()
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        client.Json("/sapi/mediaclip/7", """{"id":"7","title":"t","status":"published","embargoFrom":"2030-01-01T10:00:00Z","embargoTo":"2030-02-01T10:00:00Z"}""");
        client.Json("/sapi/mediaclip/8", """{"id":"8","title":"t","status":"draft","embargoFrom":"2001-12-02T23:00:00Z","embargoTo":"2001-12-02T23:00:00Z"}""");

        ClipSummaryResponseModel embargoed = Assert.IsType<ClipSummaryResponseModel>(Assert.IsType<OkObjectResult>(await controller.Clip(7, default)).Value);
        ClipSummaryResponseModel cleared = Assert.IsType<ClipSummaryResponseModel>(Assert.IsType<OkObjectResult>(await controller.Clip(8, default)).Value);

        Assert.Equal("2030-01-01T10:00:00Z", embargoed.EmbargoFrom);
        Assert.Equal("2030-02-01T10:00:00Z", embargoed.EmbargoTo);
        Assert.Equal("draft", cleared.Status);
        Assert.Null(cleared.EmbargoFrom);
        Assert.Null(cleared.EmbargoTo);
    }

    // Channel and shorts availability ---------------------------------------------------------------------

    private static EmbedFeaturesResponseModel Flags(IActionResult result) => Assert.IsType<EmbedFeaturesResponseModel>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Features_follow_the_counts_and_ask_for_no_items()
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.Json("/sapi/channel", """{"numfound":3,"items":[]}""");
        client.Json("/sapi/shorts", """{"numfound":0,"items":[]}""");

        EmbedFeaturesResponseModel flags = Flags(await controller.Features(default));

        Assert.True(flags.HasChannels);
        Assert.False(flags.HasShorts);
        Assert.All(client.Calls, c => Assert.Equal("0", c.Request!.Query!["limit"]));
    }

    [Fact]
    public async Task A_count_that_arrives_as_text_is_read()
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.Json("/sapi/channel", """{"numfound":"2"}""");
        client.Json("/sapi/shorts", """{"numfound":"0"}""");

        EmbedFeaturesResponseModel flags = Flags(await controller.Features(default));

        Assert.True(flags.HasChannels);
        Assert.False(flags.HasShorts);
    }

    [Fact]
    public async Task A_failing_or_unreadable_count_fails_open_so_a_type_is_never_hidden_by_an_error()
    {
        (VideoController controller, FakeClient client, _) = Rig();
        client.Json("/sapi/channel", """{"unexpected":true}""");
        // /sapi/shorts is not answered at all: a failed call.

        EmbedFeaturesResponseModel flags = Flags(await controller.Features(default));

        Assert.True(flags.HasChannels);
        Assert.True(flags.HasShorts);
    }

    [Fact]
    public void Preview_and_features_sit_under_the_content_policy_with_the_picker()
    {
        foreach (string name in new[] { nameof(VideoController.Preview), nameof(VideoController.Features) })
        {
            Assert.NotNull(typeof(VideoController).GetMethod(name)!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), true).SingleOrDefault());
        }

        Assert.Contains("SectionAccessContent", typeof(VideoController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(a => a.Policy));
    }
}

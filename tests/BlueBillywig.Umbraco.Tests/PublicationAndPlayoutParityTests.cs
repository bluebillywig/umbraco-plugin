using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>One publication validator for settings and rendering (a dotted slug is a real publication on a development OVP).</summary>
public class PublicationParityTests
{
    [Theory]
    [InlineData("demo", true)]
    [InlineData("bb.dev", true)]
    [InlineData("a-b.c-d", true)]
    [InlineData("abc", true)]
    [InlineData("ab", true)]
    [InlineData("", false)]
    [InlineData("Demo", false)]
    [InlineData("-demo", false)]
    [InlineData("demo-", false)]
    [InlineData(".demo", false)]
    [InlineData("bb..dev", false)]
    [InlineData("bb/dev", false)]
    [InlineData("bb.dev/../x", false)]
    [InlineData("bb.dev?x=1", false)]
    [InlineData("bb.dev#frag", false)]
    [InlineData("bb dev", false)]
    [InlineData("bb@evil.example", false)]
    [InlineData("bb:443", false)]
    [InlineData("a", false)]
    public void Settings_and_rendering_accept_the_same_publications(string value, bool valid)
    {
        Assert.Equal(valid, PublicationSlug.IsValid(value));
        Assert.Equal(valid, BlueBillywigVideo.IsValidPublication(value));
        // Typed into the dashboard the reference plugin's 3 characters apply on top of the shared shape.
        Assert.Equal(valid && value.Length >= 3, SettingsController.IsValidPublication(value));
    }

    [Fact]
    public void The_dev_publication_bb_renders_and_stays_connected()
    {
        Assert.True(BlueBillywigVideo.IsValidPublication("bb"));
        Assert.True(Store(new BlueBillywigOptions { Publication = "bb", ApiId = "1", ApiSecret = "s" }).Get().HasCredentials);
        Assert.False(SettingsController.IsValidPublication("bb")); // typing it into the dashboard is still refused, as in the reference plugin
    }

    [Fact]
    public void Too_long_is_refused_by_all_three()
    {
        string sixtyFour = new('a', 64);
        Assert.False(PublicationSlug.IsValid(sixtyFour));
        Assert.False(SettingsController.IsValidPublication(sixtyFour));
        Assert.False(BlueBillywigVideo.IsValidPublication(sixtyFour));
        Assert.True(BlueBillywigVideo.IsValidPublication(new string('a', 63)));
    }

    [Fact]
    public void A_stored_dotted_publication_survives_parsing_and_uppercase_is_lowercased()
    {
        Assert.Equal("bb.dev", BlueBillywigVideo.Parse("""{"id":1,"publication":"bb.dev"}""")?.Publication);
        Assert.Equal("bb.dev", BlueBillywigVideo.Parse("""{"id":1,"publication":"BB.Dev"}""")?.Publication);
        Assert.Null(BlueBillywigVideo.Parse("""{"id":1,"publication":"bb..dev"}""")?.Publication);
    }

    [Fact]
    public async Task The_renderer_renders_an_embed_of_a_dotted_publication()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, Publication = "bb.dev", LazyLoad = false });

        Assert.Contains("src=\"https://bb.dev.bbvms.com/p/default/c/42.js\"", html);
        Assert.Empty(client.Requests); // another publication than the configured one: no SAPI call
    }

    [Fact]
    public async Task The_renderer_renders_when_the_configured_publication_is_dotted()
    {
        (BlueBillywigEmbedRenderer renderer, _, FakeSettings settings) = Rig.Create();
        settings.Value = settings.Value with { Publication = "bb.dev" };

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, LazyLoad = false });

        Assert.Contains("https://bb.dev.bbvms.com/p/default/c/42.js", html);
    }

    [Theory]
    [InlineData("bb..dev")]
    [InlineData("bb/../x")]
    [InlineData("evil.example/x?")]
    [InlineData("Demo")]
    public async Task A_publication_that_is_not_a_slug_never_reaches_a_url(string publication)
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create();

        // Built directly, as a site's own code can: Parse would already have dropped it.
        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, Publication = publication });

        Assert.Equal("", html);
    }

    [Fact]
    public void The_player_host_of_a_valid_publication_has_nothing_that_could_change_the_host()
    {
        Uri uri = new(BlueBillywigUrls.PublicationBaseUrl("bb.dev"));

        Assert.Equal("bb.dev.bbvms.com", uri.Host);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("/", uri.AbsolutePath);
    }

    [Fact]
    public void The_delivery_api_converter_gives_urls_for_a_dotted_publication()
    {
        var converter = new BlueBillywigVideoPropertyValueConverter(new FakeSettings());
        object? inter = converter.ConvertSourceToIntermediate(null!, null!, """{"type":"clip","id":12,"publication":"bb.dev"}""", false);

        var api = converter.ConvertIntermediateToDeliveryApiObject(null!, null!, default, inter, false, false) as BlueBillywigVideoApiModel;

        Assert.NotNull(api);
        Assert.Equal("bb.dev", api.Publication);
        Assert.Equal("https://bb.dev.bbvms.com/p/default/c/12.html", api.PlayerUrl);
        Assert.Equal("https://bb.dev.bbvms.com/p/default/c/12.js", api.ScriptUrl);
    }

    [Fact]
    public void The_converter_gives_no_urls_for_an_invalid_publication_it_is_handed_directly()
    {
        var converter = new BlueBillywigVideoPropertyValueConverter(new FakeSettings());

        var api = converter.ConvertIntermediateToDeliveryApiObject(null!, null!, default, new BlueBillywigVideo { Id = 12, Publication = "bb..dev" }, false, false) as BlueBillywigVideoApiModel;

        Assert.NotNull(api);
        Assert.Null(api.PlayerUrl);
        Assert.Null(api.ScriptUrl);
    }

    [Fact]
    public async Task The_tag_helper_renders_a_property_value_of_a_dotted_publication()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(http: new DefaultHttpContext());
        BlueBillywigVideo stored = BlueBillywigVideo.Parse("""{"type":"clip","id":7,"publication":"bb.dev","lazyLoad":false}""")!;
        var helper = new BlueBillywigVideoTagHelper(renderer) { Video = stored };
        var output = new TagHelperOutput("bb-video", [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        await helper.ProcessAsync(new TagHelperContext([], new Dictionary<object, object>(), "1"), output);

        Assert.Contains("https://bb.dev.bbvms.com/p/default/c/7.js", output.Content.GetContent());
    }

    [Fact]
    public async Task A_rich_text_reference_to_a_dotted_publication_renders()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create();
        string reference = """<span class="bb-rte-video-ref" data-bb-video='{"type":"clip","id":9,"publication":"bb.dev","lazyLoad":false}'></span>""";

        string html = await RichTextEmbedRewriter.RewriteAsync(reference, v => Rig.Render(renderer, v));

        Assert.Contains("https://bb.dev.bbvms.com/p/default/c/9.js", html);
    }

    // L4: the settings store ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Demo")]
    [InlineData("bb..dev")]
    [InlineData("x")]
    [InlineData("demo/../evil")]
    public void A_configured_publication_that_is_not_a_slug_counts_as_not_connected(string configured)
    {
        BlueBillywigSettings settings = Store(new BlueBillywigOptions { Publication = configured, ApiId = "1", ApiSecret = "s" }).Get();

        Assert.True(settings.PublicationRejected);
        Assert.Equal("", settings.Publication);
        Assert.False(settings.HasCredentials);
    }

    [Fact]
    public void A_stored_publication_that_is_not_a_slug_counts_as_not_connected()
    {
        var keyValues = new FakeKeyValues();
        keyValues.Rows["BlueBillywig.publication"] = "Bad Publication";

        BlueBillywigSettings settings = Store(new BlueBillywigOptions { ApiId = "1", ApiSecret = "s" }, keyValues).Get();

        Assert.True(settings.PublicationRejected);
        Assert.False(settings.HasCredentials);
    }

    [Fact]
    public void A_valid_dotted_publication_stays_connected()
    {
        BlueBillywigSettings settings = Store(new BlueBillywigOptions { Publication = "bb.dev", ApiId = "1", ApiSecret = "s" }).Get();

        Assert.False(settings.PublicationRejected);
        Assert.Equal("bb.dev", settings.Publication);
        Assert.True(settings.HasCredentials);
    }

    private static BlueBillywigSettingsStore Store(BlueBillywigOptions options, FakeKeyValues? keyValues = null) =>
        new(keyValues ?? new FakeKeyValues(), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), new FixedOptions(options));
}

/// <summary>The clip's library playout applies when the embed chooses none: embed &gt; library &gt; site default.</summary>
public class ClipPlayoutRenderTests
{
    private static (BlueBillywigEmbedRenderer Renderer, FakeClient Client, FakeSettings Settings, FakePlayouts Playouts) Rig2(bool consent = true)
    {
        var playouts = new FakePlayouts();
        (BlueBillywigEmbedRenderer renderer, FakeClient client, FakeSettings settings) = Rig.Create(consent: consent, http: new DefaultHttpContext(), playouts: playouts);
        return (renderer, client, settings, playouts);
    }

    [Fact]
    public async Task The_library_playout_is_used_when_the_embed_names_none()
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = "wide";

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, LazyLoad = false });

        Assert.Contains("https://demo.bbvms.com/p/wide/c/42.js", html);
        Assert.DoesNotContain("/p/default/", html);
    }

    [Fact]
    public async Task The_embeds_own_playout_beats_the_library_playout()
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = "wide";

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, Playout = "square", LazyLoad = false });

        Assert.Contains("/p/square/c/42.js", html);
        Assert.DoesNotContain("/p/wide/", html);
    }

    [Fact]
    public async Task Without_either_the_site_default_applies()
    {
        (BlueBillywigEmbedRenderer renderer, _, FakeSettings settings, _) = Rig2();
        settings.Value = settings.Value with { Playout = "sitewide" };

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, LazyLoad = false });

        Assert.Contains("/p/sitewide/c/42.js", html);
    }

    [Fact]
    public async Task The_library_playout_beats_the_site_default()
    {
        (BlueBillywigEmbedRenderer renderer, _, FakeSettings settings, FakePlayouts playouts) = Rig2();
        settings.Value = settings.Value with { Playout = "sitewide" };
        playouts.Values[42] = "wide";

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, LazyLoad = false });

        Assert.Contains("/p/wide/c/42.js", html);
    }

    [Fact]
    public async Task The_library_playout_reaches_the_embed_code_request()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _, FakePlayouts playouts) = Rig2();
        playouts.Values[5] = "wide";
        client.Json("/sapi/embedcode/5/wide/javascript", """{"body":"<script type=\"text/javascript\" src=\"https://demo.bbvms.com/p/wide/c/5.js\"></script>"}""");

        await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.Contains("/sapi/embedcode/5/wide/javascript", client.Requests);
        Assert.DoesNotContain("/sapi/embedcode/5/default/javascript", client.Requests);
    }

    [Fact]
    public async Task The_library_playout_applies_to_the_denied_consent_state_and_channels_ignore_it()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _, FakePlayouts playouts) = Rig2(consent: false);
        playouts.Values[42] = "wide";

        string denied = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42 });
        string channel = await Rig.Render(renderer, new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 42, LazyLoad = false });

        Assert.DoesNotContain("bbvms", denied); // nothing from the player host before consent, whatever the playout
        Assert.Empty(client.Requests);
        Assert.DoesNotContain("/p/wide/", channel);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("../x")]
    [InlineData("a b")]
    public async Task A_stored_playout_that_is_not_a_playout_name_is_ignored(string stored)
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = stored;

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, LazyLoad = false });

        Assert.Contains("/p/default/c/42.js", html);
    }

    [Fact]
    public async Task Another_publications_clip_does_not_take_this_publications_library_playout()
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = "wide";

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, Publication = "other", LazyLoad = false });

        Assert.Contains("https://other.bbvms.com/p/default/c/42.js", html);
    }

    [Fact]
    public async Task A_store_that_throws_does_not_break_the_render()
    {
        var failing = new ThrowingPlayouts();
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(http: new DefaultHttpContext(), playouts: failing);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, LazyLoad = false });

        Assert.Contains("/p/default/c/42.js", html);
    }

    private sealed class ThrowingPlayouts : IClipPlayoutStore
    {
        public string? Get(long clipId) => throw new InvalidOperationException("database down");

        public void Set(long clipId, string? playout) => throw new NotSupportedException();
    }

    // Every way into the renderer ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_tag_helper_by_id_uses_the_library_playout_and_an_attribute_playout_beats_it()
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = "wide";

        Assert.Contains("/p/wide/c/42.js", await Tag(renderer, new BlueBillywigVideoTagHelper(renderer) { Id = 42, Lazy = false }));
        Assert.Contains("/p/square/c/42.js", await Tag(renderer, new BlueBillywigVideoTagHelper(renderer) { Id = 42, Lazy = false, Playout = "square" }));
    }

    [Fact]
    public async Task The_tag_helper_with_a_property_value_uses_the_library_playout()
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = "wide";
        var stored = new BlueBillywigVideo { Id = 42, LazyLoad = false };

        Assert.Contains("/p/wide/c/42.js", await Tag(renderer, new BlueBillywigVideoTagHelper(renderer) { Video = stored }));
    }

    private static async Task<string> Tag(BlueBillywigEmbedRenderer _, BlueBillywigVideoTagHelper helper)
    {
        var output = new TagHelperOutput("bb-video", [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        await helper.ProcessAsync(new TagHelperContext([], new Dictionary<object, object>(), "1"), output);
        return output.Content.GetContent();
    }

    [Fact]
    public void FromId_leaves_the_playout_open_so_the_renderer_can_apply_the_library_choice()
    {
        BlueBillywigVideo byId = EmbedDefaults.FromId(null, "clip", 42, null, false, null, null);
        BlueBillywigVideo own = EmbedDefaults.FromId(null, "clip", 42, "square", false, null, null);

        Assert.Null(byId.Playout);
        Assert.Equal("square", own.Playout);
    }

    [Fact]
    public async Task Rich_text_references_use_the_library_playout()
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = "wide";
        string noChoice = """<span class="bb-rte-video-ref" data-bb-video='{"type":"clip","id":42,"lazyLoad":false}'></span>""";
        string ownChoice = """<span class="bb-rte-video-ref" data-bb-video='{"type":"clip","id":42,"playout":"square","lazyLoad":false}'></span>""";

        Assert.Contains("/p/wide/c/42.js", await RichTextEmbedRewriter.RewriteAsync(noChoice, v => Rig.Render(renderer, v)));
        Assert.Contains("/p/square/c/42.js", await RichTextEmbedRewriter.RewriteAsync(ownChoice, v => Rig.Render(renderer, v)));
    }

    [Fact]
    public async Task A_pasted_url_keeps_the_playout_in_the_url()
    {
        (BlueBillywigEmbedRenderer renderer, _, _, FakePlayouts playouts) = Rig2();
        playouts.Values[42] = "wide";
        BlueBillywigVideo pasted = BlueBillywigUrls.TryParseOEmbedUrl("https://demo.bbvms.com/p/square/c/42.html")!;

        string html = await Rig.Render(renderer, pasted with { LazyLoad = false });

        Assert.Contains("/p/square/c/42.js", html);
    }

    [Fact]
    public void The_delivery_api_player_url_uses_the_library_playout()
    {
        var playouts = new FakePlayouts();
        playouts.Values[12] = "wide";
        var converter = new BlueBillywigVideoPropertyValueConverter(new FakeSettings(), playouts);

        BlueBillywigVideoApiModel? Api(string json) => converter.ConvertIntermediateToDeliveryApiObject(null!, null!, default, converter.ConvertSourceToIntermediate(null!, null!, json, false), false, false) as BlueBillywigVideoApiModel;

        Assert.Equal("https://demo.bbvms.com/p/wide/c/12.html", Api("""{"type":"clip","id":12}""")?.PlayerUrl);
        Assert.Equal("https://demo.bbvms.com/p/own/c/12.html", Api("""{"type":"clip","id":12,"playout":"own"}""")?.PlayerUrl);
    }

    [Fact]
    public void The_helper_applies_only_to_a_clip_without_an_own_playout_of_the_configured_publication()
    {
        var playouts = new FakePlayouts();
        playouts.Values[1] = "wide";

        Assert.Equal("wide", EmbedDefaults.WithClipPlayout(playouts, new BlueBillywigVideo { Id = 1 }, true).Playout);
        Assert.Equal("own", EmbedDefaults.WithClipPlayout(playouts, new BlueBillywigVideo { Id = 1, Playout = "own" }, true).Playout);
        Assert.Null(EmbedDefaults.WithClipPlayout(playouts, new BlueBillywigVideo { Id = 1 }, false).Playout);
        Assert.Null(EmbedDefaults.WithClipPlayout(playouts, new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 1 }, true).Playout);
        Assert.Null(EmbedDefaults.WithClipPlayout(null, new BlueBillywigVideo { Id = 1 }, true).Playout);
    }
}

using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// What an edit changes elsewhere, the plugin's 2.0.x fixes: the main thumbnail is the same everywhere (library, picker,
/// player poster) and a recent edit versions the player URL, in the rendered page and in the embed code the library copies.
/// </summary>
public class EditEffectsTests
{
    private const string Clip = """{"id":"42","title":"t","width":1920,"height":1080,"thumbnails":[{"src":"/old.jpg","main":true,"crops":{"landscape":{"main":true}}}]}""";

    private readonly ThumbnailState _state = new(new MemoryCache(new MemoryCacheOptions()), new FakeTime());

    private void RememberNewMain() =>
        _state.Remember(42, JsonNode.Parse("""[{"src":"/new.jpg","main":true,"crops":{"landscape":{"main":true}}},{"src":"/old.jpg","main":false,"crops":{"landscape":{"main":false}}}]""")!.AsArray());

    [Fact]
    public async Task The_library_list_and_details_show_the_main_thumbnail_the_last_write_confirmed()
    {
        var client = new FakeClient();
        client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        client.Json("/sapi/mediaclip", $$"""{"numfound":1,"items":[{{Clip}}]}""");
        client.Json("/sapi/mediaclip/42", Clip);
        var service = new LibraryService(client, new FakeSettings(), _state);
        LibraryQuery.TryParse(null, null, null, null, null, null, 0, out LibraryFilter filter, out _);

        Assert.Equal("https://cdn.example/old.jpg", (await service.ListClipsAsync(filter, 100, default)).Value!.Items[0].ThumbnailUrl);

        RememberNewMain(); // SAPI's read still says /old.jpg
        Assert.Equal("https://cdn.example/new.jpg", (await service.ListClipsAsync(filter, 100, default)).Value!.Items[0].ThumbnailUrl);
        Assert.Equal("https://cdn.example/new.jpg", (await service.GetClipAsync(42, default)).Value!.ThumbnailUrl);
    }

    [Fact]
    public async Task The_player_poster_follows_the_confirmed_main_thumbnail()
    {
        var client = new FakeClient();
        var settings = new FakeSettings();
        client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example","baseurl":"https://demo.bbvms.com"}""");
        client.Json("/sapi/mediaclip/42", Clip);
        client.Json("/sapi/embedcode/42/default/javascript", """{"body":"<script type=\"text/javascript\" src=\"https://demo.bbvms.com/p/default/c/42.js\"></script>"}""");
        var renderer = new BlueBillywigEmbedRenderer(settings, client, new FixedConsent(true), new FixedNonce(null), new HttpContextAccessor(), NullLogger<BlueBillywigEmbedRenderer>.Instance, _state);
        var video = new BlueBillywigVideo { Id = 42, LazyLoad = true };

        Assert.Contains("https://cdn.example/old.jpg", await Rig.Render(renderer, video));

        RememberNewMain();
        string html = await Rig.Render(renderer, video);
        Assert.Contains("https://cdn.example/new.jpg", html);
        Assert.DoesNotContain("old.jpg", html);
    }

    [Fact]
    public async Task A_recent_edit_versions_the_player_url_in_the_page_and_the_old_url_returns_afterwards()
    {
        var client = new FakeClient();
        var settings = new FakeSettings();
        var time = new FakeTime();
        var state = new ThumbnailState(new MemoryCache(new MemoryCacheOptions()), time);
        client.Json("/sapi/publication", """{"baseurl":"https://demo.bbvms.com"}""");
        client.Json("/sapi/mediaclip/42", Clip);
        client.Json("/sapi/embedcode/42/default/javascript", """{"body":"<script type=\"text/javascript\" src=\"https://demo.bbvms.com/p/default/c/42.js\"></script>"}""");
        var renderer = new BlueBillywigEmbedRenderer(settings, client, new FixedConsent(true), new FixedNonce(null), new HttpContextAccessor(), NullLogger<BlueBillywigEmbedRenderer>.Instance, state);
        var video = new BlueBillywigVideo { Id = 42, LazyLoad = false };

        Assert.DoesNotContain("c/42.js?v=", await Rig.Render(renderer, video));

        state.Touch(42); // what a title edit does
        Assert.Contains("c/42.js?v=", await Rig.Render(renderer, video));

        time.Advance(ThumbnailState.VersionTtl + TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("c/42.js?v=", await Rig.Render(renderer, video));
    }

    [Fact]
    public async Task The_library_embed_code_is_versioned_and_uses_the_playout_chosen_for_the_clip()
    {
        var client = new FakeClient();
        var playouts = new FakePlayouts();
        playouts.Set(42, "chosen");
        client.Json("/sapi/embedcode/42/chosen/javascript", """{"body":"<script src=\"https://demo.bbvms.com/p/chosen/c/42.js\"></script>"}""");
        client.Json("/sapi/embedcode/42/named/javascript", """{"body":"<script src=\"https://demo.bbvms.com/p/named/c/42.js\"></script>"}""");
        client.Json("/sapi/embedcode/42/default/javascript", """{"body":"<script src=\"https://demo.bbvms.com/p/default/c/42.js\"></script>"}""");
        var service = new LibraryService(client, new FakeSettings(), _state, playouts);
        _state.Touch(42);

        LibraryResult<EmbedCodeResponseModel> chosen = await service.GetEmbedCodeAsync(42, null, default);
        Assert.Contains("/p/chosen/c/42.js?v=", chosen.Value!.Code); // the editor's choice, versioned

        Assert.Contains("/p/named/", (await service.GetEmbedCodeAsync(42, "named", default)).Value!.Code); // a playout named in the call wins

        playouts.Set(42, null);
        Assert.Contains("/p/default/", (await service.GetEmbedCodeAsync(42, null, default)).Value!.Code);
    }

    [Fact]
    public async Task The_preview_uses_the_playout_chosen_for_the_clip()
    {
        var playouts = new FakePlayouts();
        playouts.Set(42, "chosen");
        var service = new LibraryService(new FakeClient(), new FakeSettings(), _state, playouts);

        Assert.Contains("/p/chosen/c/42.html", (await service.GetPreviewAsync(42, default)).Value!.Url);
    }
}

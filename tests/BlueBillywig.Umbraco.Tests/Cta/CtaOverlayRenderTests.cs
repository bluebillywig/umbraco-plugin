using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Tests.Cta;

/// <summary>C6: the form overlay assets ship with every embed, with the nonce, once per request, and the clip id the overlay needs.</summary>
public class CtaOverlayRenderTests
{
    private static async Task<string> Render(BlueBillywigVideo video, BlueBillywigOptions? options = null, string? nonce = null, bool consent = true, bool enableCta = true, DefaultHttpContext? http = null)
    {
        (BlueBillywigEmbedRenderer renderer, _, FakeSettings settings) = Rig.Create(consent: consent, nonce: nonce, http: http ?? new DefaultHttpContext(), options: options);
        settings.Value = settings.Value with { EnableCta = enableCta };
        return await Rig.Render(renderer, video);
    }

    [Fact]
    public async Task Every_render_path_ships_the_overlay_stylesheet_and_script()
    {
        foreach (BlueBillywigVideoType type in Enum.GetValues<BlueBillywigVideoType>())
        {
            string granted = await Render(new BlueBillywigVideo { Type = type, Id = 1 });
            Assert.Contains("bb-cta-form-overlay.css", granted);
            Assert.Contains("bb-cta-form-overlay.js", granted);
        }

        // The consent placeholder too: the script is what the visitor's "load video" ends up using.
        Assert.Contains("bb-cta-form-overlay.js", await Render(new BlueBillywigVideo { Id = 1 }, consent: false));
    }

    [Fact]
    public async Task The_overlay_is_not_removed_by_switching_the_lazy_loader_off()
    {
        string html = await Render(new BlueBillywigVideo { Id = 1, LazyLoad = false }, new BlueBillywigOptions { DisableLazyLoader = true });
        Assert.DoesNotContain("bb-lazy.js", html);
        Assert.Contains("bb-cta-form-overlay.js", html);
        Assert.Contains("bb-cta-form-overlay.css", html);
    }

    [Fact]
    public async Task The_overlay_script_carries_the_nonce()
    {
        string html = await Render(new BlueBillywigVideo { Id = 1 }, nonce: "abc123");
        Assert.Matches("bb-cta-form-overlay\\.js[^>]*nonce=\"abc123\"", html);
    }

    [Fact]
    public async Task The_overlay_is_emitted_once_per_request()
    {
        var http = new DefaultHttpContext();
        string first = await Render(new BlueBillywigVideo { Id = 1 }, http: http);
        string second = await Render(new BlueBillywigVideo { Id = 2 }, http: http);
        Assert.Contains("bb-cta-form-overlay.js", first);
        Assert.DoesNotContain("bb-cta-form-overlay", second);
    }

    [Fact]
    public async Task The_overlay_is_left_out_when_the_feature_is_off()
    {
        string html = await Render(new BlueBillywigVideo { Id = 1 }, enableCta: false);
        Assert.DoesNotContain("bb-cta-form-overlay", html);
        Assert.Contains("bb-lazy.js", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_clip_wrapper_carries_the_clip_id_the_overlay_stores_its_answer_under(bool lazy)
    {
        string html = await Render(new BlueBillywigVideo { Id = 4711, LazyLoad = lazy });
        Assert.Matches("class=\"bb-block-embed[^\"]*\"[^>]*data-bb-clip-id=\"4711\"", html);
    }

    [Fact]
    public void The_overlay_script_keeps_the_storage_key_and_posts_only_to_the_entered_endpoint()
    {
        string js = File.ReadAllText(FindAsset("bb-cta-form-overlay.js"));
        Assert.Contains("'bbtl_'", js);
        Assert.Contains("localStorage.setItem(PREFIX + clipId", js);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(js, @"fetch\("));
        Assert.Contains("fetch(cfg.submitEndpoint", js);
    }

    private static string FindAsset(string name)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BlueBillywig.Umbraco.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir!, "src", "BlueBillywig.Umbraco", "Client", "public", name);
    }
}

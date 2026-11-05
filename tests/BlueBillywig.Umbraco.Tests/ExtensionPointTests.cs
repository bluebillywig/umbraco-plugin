using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>X1 and X2: the consent and CSP nonce providers are the seams a site replaces.</summary>
public class ProviderExtensionPointTests
{
    private sealed class SiteConsent : IBlueBillywigConsentProvider
    {
        public bool IsGranted(HttpContext? httpContext) => httpContext?.Request.Headers.ContainsKey("X-Consented") == true;
    }

    private sealed class SiteNonce : IBlueBillywigCspNonceProvider
    {
        public string? GetNonce(HttpContext? httpContext) => "site-nonce_1=";
    }

    [Fact]
    public void A_sites_own_provider_wins_over_the_default_whichever_registers_first()
    {
        // The embed composer registers its defaults with TryAddSingleton exactly like this.
        foreach (bool siteFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            if (siteFirst)
            {
                services.AddSingleton<IBlueBillywigConsentProvider, SiteConsent>();
                services.AddSingleton<IBlueBillywigCspNonceProvider, SiteNonce>();
            }

            services.TryAddSingleton<IBlueBillywigConsentProvider, AlwaysGrantedConsentProvider>();
            services.TryAddSingleton<IBlueBillywigCspNonceProvider, NoCspNonceProvider>();
            if (!siteFirst)
            {
                // Registered after, a plain AddSingleton still takes over: the last registration is the one resolved.
                services.AddSingleton<IBlueBillywigConsentProvider, SiteConsent>();
                services.AddSingleton<IBlueBillywigCspNonceProvider, SiteNonce>();
            }

            using ServiceProvider provider = services.BuildServiceProvider();
            Assert.IsType<SiteConsent>(provider.GetRequiredService<IBlueBillywigConsentProvider>());
            Assert.IsType<SiteNonce>(provider.GetRequiredService<IBlueBillywigCspNonceProvider>());
        }
    }

    [Fact]
    public void The_defaults_grant_consent_and_use_no_nonce()
    {
        Assert.True(new AlwaysGrantedConsentProvider().IsGranted(null));
        Assert.Null(new NoCspNonceProvider().GetNonce(null));
    }

    [Fact]
    public async Task The_consent_provider_decides_per_request()
    {
        var granted = new DefaultHttpContext();
        granted.Request.Headers["X-Consented"] = "1";
        var denied = new DefaultHttpContext();
        var consent = new SiteConsent();

        Assert.True(consent.IsGranted(granted));
        Assert.False(consent.IsGranted(denied));

        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(consent: consent.IsGranted(denied), http: denied);
        Assert.Contains("bb-consent-placeholder", await Rig.Render(renderer, new BlueBillywigVideo { Id = 1 }));
    }

    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("a/b_c=-", "a/b_c=-")]
    [InlineData("bad\"nonce", null)]
    [InlineData("two words", null)]
    [InlineData("<script>", null)]
    [InlineData("", null)]
    public async Task The_nonce_goes_on_every_script_the_renderer_emits_and_only_if_it_is_well_formed(string nonce, string? expected)
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(nonce: nonce);

        string clip = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });
        string channel = await Rig.Render(renderer, new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 8, LazyLoad = false });

        if (expected is null)
        {
            Assert.DoesNotContain("nonce=", clip);
            Assert.DoesNotContain("nonce=", channel);
            return;
        }

        // The loader script (first render of the request is a fresh context, so each render carries its own copy).
        Assert.Matches($"bb-lazy\\.js[^>]*nonce=\"{System.Text.RegularExpressions.Regex.Escape(expected)}\"", clip);
        Assert.Contains($"<script type=\"text/javascript\" nonce=\"{expected}\" src=\"https://demo.bbvms.com/p/default/c/5.js\"></script>", clip);
        Assert.Contains($"nonce=\"{expected}\" src=\"https://demo.bbvms.com/ch/8.js\"", channel);
    }
}

/// <summary>X3: <c>DisableLazyLoader</c> and <c>ForceLazyLoader</c>, the plugin's two lazy-loader filters.</summary>
public class LazyLoaderOptionTests
{
    private static async Task<string> Render(BlueBillywigOptions? options, BlueBillywigVideo video, bool consent = true, DefaultHttpContext? http = null)
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(consent: consent, options: options, http: http ?? new DefaultHttpContext());
        return await Rig.Render(renderer, video);
    }

    [Fact]
    public async Task By_default_every_render_path_ships_the_loader()
    {
        Assert.Contains("bb-lazy.js", await Render(null, new BlueBillywigVideo { Id = 1 }));
        Assert.Contains("bb-lazy.js", await Render(null, new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 1 }));
        Assert.Contains("bb-lazy.js", await Render(null, new BlueBillywigVideo { Type = BlueBillywigVideoType.Shorts, Id = 1 }));
        Assert.Contains("bb-lazy.js", await Render(null, new BlueBillywigVideo { Id = 1 }, consent: false));
    }

    [Fact]
    public async Task Disabling_the_loader_removes_it_from_every_render_path_even_the_consent_placeholder()
    {
        var options = new BlueBillywigOptions { DisableLazyLoader = true };

        foreach (BlueBillywigVideoType type in Enum.GetValues<BlueBillywigVideoType>())
        {
            string granted = await Render(options, new BlueBillywigVideo { Type = type, Id = 1 });
            string denied = await Render(options, new BlueBillywigVideo { Type = type, Id = 1 }, consent: false);
            Assert.DoesNotContain("bb-lazy.js", granted);
            Assert.DoesNotContain("bb-lazy.css", granted);
            Assert.DoesNotContain("bb-lazy.js", denied);
            Assert.DoesNotContain("bb-lazy.css", denied);
        }

        // The embed itself is still there.
        Assert.Contains("c/1.js", await Render(options, new BlueBillywigVideo { Id = 1, LazyLoad = false }));
    }

    [Fact]
    public async Task Turning_force_off_never_removes_the_loader_from_an_embed()
    {
        // The plugin's test_enqueue_lazy_loader_filter_does_not_gate_the_render_safety_net: only disable can do that.
        string html = await Render(new BlueBillywigOptions { ForceLazyLoader = false }, new BlueBillywigVideo { Id = 1 });

        Assert.Contains("bb-lazy.js", html);
        Assert.Contains("bb-lazy.css", html);
    }

    private static IBlueBillywigAssets Assets(BlueBillywigOptions options, DefaultHttpContext http, string? nonce = null)
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(nonce: nonce, http: http, options: options);
        return renderer;
    }

    [Fact]
    public void The_assets_helper_emits_only_when_forced()
    {
        string off = Assets(new BlueBillywigOptions(), new DefaultHttpContext()).RenderAssets().ToHtmlString();
        string on = Assets(new BlueBillywigOptions { ForceLazyLoader = true }, new DefaultHttpContext(), "nonce1").RenderAssets().ToHtmlString();

        Assert.Equal("", off);
        Assert.Contains("bb-lazy.css", on);
        Assert.Matches("bb-lazy\\.js[^>]*nonce=\"nonce1\"", on);
    }

    [Fact]
    public void Disable_wins_over_force()
    {
        string html = Assets(new BlueBillywigOptions { ForceLazyLoader = true, DisableLazyLoader = true }, new DefaultHttpContext()).RenderAssets().ToHtmlString();

        Assert.Equal("", html);
    }

    [Fact]
    public async Task The_loader_is_emitted_once_per_request_whether_the_helper_or_the_embed_comes_first()
    {
        var options = new BlueBillywigOptions { ForceLazyLoader = true };
        var http = new DefaultHttpContext();
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(http: http, options: options);

        string head = renderer.RenderAssets().ToHtmlString();
        string embed = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1 });
        string headAgain = renderer.RenderAssets().ToHtmlString();

        Assert.Contains("bb-lazy.js", head);
        Assert.DoesNotContain("bb-lazy.js", embed);
        Assert.Equal("", headAgain);
    }

    [Fact]
    public async Task The_tag_helper_writes_what_the_service_returns()
    {
        var http = new DefaultHttpContext();
        var helper = new BlueBillywigAssetsTagHelper(Assets(new BlueBillywigOptions { ForceLazyLoader = true }, http));
        var output = new TagHelperOutput("bb-assets", [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        helper.Process(new TagHelperContext([], new Dictionary<object, object>(), "1"), output);

        Assert.Null(output.TagName);
        Assert.Contains("bb-lazy.js", output.Content.GetContent());
        await Task.CompletedTask;
    }
}

/// <summary>X4: the lazy-load switches for oEmbed, channel oEmbed and the Razor surface. They apply only where nothing else was chosen.</summary>
public class LazyLoadSwitchTests
{
    private const string ClipRef = """<p><span class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/p/default/c/42.html"></span></p>""";
    private const string ChannelRef = """<p><span class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/ch/7.html"></span></p>""";

    private static async Task<string> Rewrite(string stored, BlueBillywigOptions? options)
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(options: options, http: new DefaultHttpContext());
        return await RichTextEmbedRewriter.RewriteAsync(
            stored,
            async v => (await renderer.RenderAsync(v)).ToHtmlString(),
            oembedDefaults: v => EmbedDefaults.ForOEmbed(options, v));
    }

    [Fact]
    public async Task A_pasted_clip_is_lazy_by_default_and_the_switch_turns_it_off()
    {
        Assert.Contains("bb-lazy-embed", await Rewrite(ClipRef, null));
        Assert.Contains("bb-lazy-embed", await Rewrite(ClipRef, new BlueBillywigOptions { LazyLoadOEmbed = true }));

        string eager = await Rewrite(ClipRef, new BlueBillywigOptions { LazyLoadOEmbed = false });

        Assert.DoesNotContain("data-bb-embed", eager);
        Assert.Contains("demo.bbvms.com/p/default/c/42.js", eager);
    }

    [Fact]
    public async Task A_pasted_channel_is_eager_by_default_and_the_switch_turns_lazy_on()
    {
        Assert.DoesNotContain("data-bb-embed", await Rewrite(ChannelRef, null));
        Assert.DoesNotContain("data-bb-embed", await Rewrite(ChannelRef, new BlueBillywigOptions { LazyLoadOEmbed = true }));

        string lazy = await Rewrite(ChannelRef, new BlueBillywigOptions { LazyLoadOEmbedChannel = true });

        Assert.Contains("data-bb-embed", lazy);
    }

    [Fact]
    public async Task The_oembed_switches_do_not_touch_a_block_reference_that_chose_for_itself()
    {
        string eagerRef = """<span class="bb-rte-video-ref" data-bb-video='{"type":"clip","id":42,"lazyLoad":false}'></span>""";
        string lazyRef = """<span class="bb-rte-video-ref" data-bb-video='{"type":"clip","id":42,"lazyLoad":true}'></span>""";

        Assert.DoesNotContain("data-bb-embed", await Rewrite(eagerRef, new BlueBillywigOptions { LazyLoadOEmbed = true }));
        Assert.Contains("data-bb-embed", await Rewrite(lazyRef, new BlueBillywigOptions { LazyLoadOEmbed = false }));
    }

    private static async Task<string> TagHelper(BlueBillywigOptions? options, bool? lazy, BlueBillywigVideo? video = null, long? id = 42)
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(options: options, http: new DefaultHttpContext());
        var helper = new BlueBillywigVideoTagHelper(renderer, options is null ? null : new FixedOptions(options)) { Id = video is null ? id : null, Video = video, Lazy = lazy };
        var output = new TagHelperOutput("bb-video", [], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        await helper.ProcessAsync(new TagHelperContext([], new Dictionary<object, object>(), "1"), output);
        return output.Content.GetContent();
    }

    [Fact]
    public async Task A_clip_by_id_follows_the_component_switch_unless_the_attribute_says_otherwise()
    {
        Assert.Contains("data-bb-embed", await TagHelper(null, null));
        Assert.DoesNotContain("data-bb-embed", await TagHelper(new BlueBillywigOptions { LazyLoadComponent = false }, null));
        Assert.Contains("data-bb-embed", await TagHelper(new BlueBillywigOptions { LazyLoadComponent = false }, lazy: true));
        Assert.DoesNotContain("data-bb-embed", await TagHelper(new BlueBillywigOptions { LazyLoadComponent = true }, lazy: false));
    }

    [Fact]
    public async Task A_stored_value_keeps_its_own_choice_whatever_the_switch_says()
    {
        var stored = new BlueBillywigVideo { Id = 42, LazyLoad = true };

        Assert.Contains("data-bb-embed", await TagHelper(new BlueBillywigOptions { LazyLoadComponent = false }, null, stored));
    }

    [Fact]
    public async Task The_view_component_builds_a_video_from_an_id_with_the_same_rule()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(options: new BlueBillywigOptions { LazyLoadComponent = false }, http: new DefaultHttpContext());
        var component = new BlueBillywigVideoViewComponent(renderer, new FixedOptions(new BlueBillywigOptions { LazyLoadComponent = false }))
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext { HttpContext = new DefaultHttpContext() } },
        };

        var result = (HtmlContentViewComponentResult)await component.InvokeAsync(null, id: 42);
        string html = result.EncodedContent.ToHtmlString();

        Assert.Contains("demo.bbvms.com/p/default/c/42.js", html);
        Assert.DoesNotContain("data-bb-embed", html);
        Assert.Equal("", ((HtmlContentViewComponentResult)await component.InvokeAsync(null)).EncodedContent.ToHtmlString());
    }
}

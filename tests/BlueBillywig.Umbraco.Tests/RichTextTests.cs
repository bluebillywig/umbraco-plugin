using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Strings;

namespace BlueBillywig.Umbraco.Tests;

public class RichTextTests
{
    // What the editor stored before the fix: the player, the poster and an escaped noscript baked in.
    private const string OldStored = """
        <p><span class="umb-embed-holder" data-embed-constrain="true" data-embed-height="281" data-embed-url="https://bb.bbvms.com/p/default/c/11224.html" data-embed-width="500"><link rel="stylesheet" href="/App_Plugins/BlueBillywig/bb-lazy.css?v=1"><div class="bb-block-embed"><div class="bb-lazy-embed" data-bb-embed="AAAA" style="background-image:url('https://cdn.example/x.webp');"></div><noscript>&lt;div&gt;&lt;iframe src="https://bb.dev.example/p/default/c/11224.html"&gt;&lt;/iframe&gt;&lt;/div&gt;</noscript></div></span></p><p>after</p>
        """;

    private const string NewStored = """<p><span class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/p/default/c/42.html"><span class="bb-oembed-ref">Blue Billywig video 42.</span></span></p>""";

    private sealed class Html(string value) : IHtmlEncodedString
    {
        public string ToHtmlString() => value;
    }

    private static DeferredRichText Value(string stored, bool consent, string? nonce, DefaultHttpContext? http = null)
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create(consent: consent, nonce: nonce, http: http ?? new DefaultHttpContext());
        client.Json("/sapi/mediaclip/42", """{"id":42,"title":"T"}""");
        return new DeferredRichText(new Html(stored), renderer);
    }

    [Fact]
    public async Task The_provider_stores_no_player_markup()
    {
        string? markup = await new BlueBillywigOEmbedProvider(null!).GetMarkupAsync("https://demo.bbvms.com/p/default/c/42.html", null, null, default);

        Assert.NotNull(markup);
        Assert.DoesNotContain("bbvms", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<noscript", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", markup);
        Assert.Contains("42", markup);
    }

    [Fact]
    public async Task The_provider_still_ignores_urls_it_does_not_own() =>
        Assert.Null(await new BlueBillywigOEmbedProvider(null!).GetMarkupAsync("https://example.com/p/default/c/42.html", null, null, default));

    [Fact]
    public void Same_cached_value_gives_different_output_for_consent_granted_and_denied()
    {
        string granted = Value(NewStored, consent: true, nonce: null).ToHtmlString();
        string denied = Value(NewStored, consent: false, nonce: null).ToHtmlString();

        Assert.Contains("demo.bbvms.com/p/default/c/42.js", granted);
        Assert.DoesNotContain("bb-consent-placeholder", granted);
        Assert.Contains("bb-consent-placeholder", denied);
        Assert.DoesNotContain("bbvms", denied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-bb-embed", denied);
        Assert.DoesNotContain("bb-oembed-ref", granted);
    }

    [Fact]
    public void The_nonce_is_applied_per_request_not_remembered()
    {
        string first = Value(NewStored, consent: true, nonce: "nonceAAA").ToHtmlString();
        string second = Value(NewStored, consent: true, nonce: "nonceBBB").ToHtmlString();

        Assert.Contains("nonce=\"nonceAAA\"", first);
        Assert.DoesNotContain("nonceBBB", first);
        Assert.Contains("nonce=\"nonceBBB\"", second);
        Assert.DoesNotContain("nonceAAA", second);
    }

    [Fact]
    public void One_value_can_be_written_again_and_follows_consent_each_time()
    {
        bool consent = false;
        var client = new FakeClient();
        var renderer = new BlueBillywigEmbedRenderer(new FakeSettings(), client, new SwitchConsent(() => consent), new FixedNonce(null), new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueBillywigEmbedRenderer>.Instance);
        var value = new DeferredRichText(new Html(NewStored), renderer);

        string before = value.ToHtmlString();
        consent = true;
        string after = value.ToHtmlString();

        Assert.Contains("bb-consent-placeholder", before);
        Assert.DoesNotContain("bb-consent-placeholder", after);
    }

    [Fact]
    public void Old_stored_markup_is_replaced_so_the_baked_player_poster_and_escaped_noscript_are_gone()
    {
        string denied = Value(OldStored, consent: false, nonce: null).ToHtmlString();

        Assert.DoesNotContain("data-bb-embed=\"AAAA\"", denied);
        Assert.DoesNotContain("cdn.example", denied);
        Assert.DoesNotContain("&lt;iframe", denied);
        Assert.DoesNotContain("noscript", denied);
        Assert.Contains("bb-consent-placeholder", denied);
        Assert.Contains("<p>after</p>", denied);
    }

    [Fact]
    public void A_granted_render_has_a_real_noscript_fallback_not_escaped_text()
    {
        string granted = Value(NewStored.Replace("c/42.html", "c/42.html"), consent: true, nonce: null).ToHtmlString();

        Assert.DoesNotContain("&lt;", granted);
        Assert.Contains("<noscript>", granted);
    }

    [Fact]
    public void Rich_text_without_embeds_is_returned_untouched()
    {
        const string plain = "<p>hello <span class=\"umb-embed-holder\" data-embed-url=\"https://youtube.com/watch?v=1\">yt</span></p>";

        Assert.Equal(plain, Value(plain, consent: true, nonce: null).ToHtmlString());
    }

    [Fact]
    public void Headless_output_keeps_the_reference_and_drops_baked_player_markup()
    {
        string api = RichTextEmbedRewriter.ToReferences(OldStored);

        Assert.Contains("data-embed-url=\"https://bb.bbvms.com/p/default/c/11224.html\"", api);
        Assert.DoesNotContain("data-bb-embed", api);
        Assert.DoesNotContain("<script", api);
        Assert.DoesNotContain("cdn.example", api);
        Assert.DoesNotContain("noscript", api);
        Assert.Contains("<p>after</p>", api);
    }

    [Fact]
    public async Task Only_urls_the_provider_accepts_are_rewritten()
    {
        string html = await RichTextEmbedRewriter.RewriteAsync(
            """<div class="umb-embed-holder" data-embed-url="https://evil.example/p/default/c/1.html">keep</div>""",
            _ => Task.FromResult("REPLACED"));

        Assert.Contains("keep", html);
        Assert.DoesNotContain("REPLACED", html);
    }

    [Fact]
    public async Task Nested_elements_of_the_same_tag_inside_a_holder_are_replaced_as_a_whole()
    {
        string html = await RichTextEmbedRewriter.RewriteAsync(
            """<p>a</p><div class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/ch/7.html"><div><div>x</div></div><div>y</div></div><p>b</p>""",
            video => Task.FromResult($"[{video.Type}:{video.Id}]"));

        // The holder is replaced as a whole: its attributes (data-embed-url among them) were only for the editor.
        Assert.Equal("""<p>a</p>[Channel:7]<p>b</p>""", html);
    }

    [Fact]
    public async Task Two_holders_are_both_rewritten_and_an_unclosed_one_is_left_alone()
    {
        string html = await RichTextEmbedRewriter.RewriteAsync(
            """<span class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/p/default/c/1.html">a</span> <span class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/p/default/c/2.html">b</span> <span class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/p/default/c/3.html">oops""",
            video => Task.FromResult($"[{video.Id}]"));

        Assert.StartsWith("[1] [2] ", html);

        // The third holder is left as it was, its reference intact (the parser closes the element when it writes it out).
        Assert.EndsWith("data-embed-url=\"https://demo.bbvms.com/p/default/c/3.html\">oops</span>", html);
    }

    // The editor's own HTML parser pulls a block out of the holder (a div inside a span inside a p) when old content is
    // opened and saved again. This is that result, found on bb.dev: the holder is empty and the baked embed sits outside it.
    private const string Detached = """
        <p><span class="umb-embed-holder" data-embed-url="https://bb.bbvms.com/p/default/c/11224.html"></span></p><div class="bb-block-embed bb-embed-responsive bb-embed-lazy"><div class="bb-lazy-embed bb-lazy-ratio-16-9" data-bb-embed="AAAA" style="background-image: url(&quot;https://cdn.example/x.webp&quot;);"></div></div><p></p>
        """;

    [Fact]
    public void Baked_markup_the_editor_pulled_out_of_the_holder_is_removed_so_it_cannot_outlive_consent()
    {
        string denied = Value(Detached, consent: false, nonce: null).ToHtmlString();

        Assert.DoesNotContain("AAAA", denied);
        Assert.DoesNotContain("cdn.example", denied);
        Assert.DoesNotContain("bb-lazy-embed", denied);
        Assert.Contains("bb-consent-placeholder", denied);
        Assert.DoesNotContain("bbvms", denied, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_providers_own_reference_text_does_not_survive_when_the_editor_pulls_it_out_of_the_holder()
    {
        // An earlier provider version stored a div; the editor's parser pulled it out of the paragraph. It must not show as text.
        string denied = Value("""<p><span class="umb-embed-holder" data-embed-url="https://demo.bbvms.com/p/default/c/42.html"></span></p><div class="bb-oembed-ref">Blue Billywig video 42. The player is added when the page is shown.</div>""", consent: false, nonce: null).ToHtmlString();

        Assert.DoesNotContain("The player is added", denied);
        Assert.Contains("bb-consent-placeholder", denied);
    }

    [Fact]
    public void Headless_output_also_drops_the_detached_markup()
    {
        string api = RichTextEmbedRewriter.ToReferences(Detached);

        Assert.DoesNotContain("AAAA", api);
        Assert.DoesNotContain("bb-block-embed", api);
        Assert.Contains("data-embed-url", api);
    }

    [Fact]
    public async Task A_rich_text_block_reference_is_rendered_from_its_json()
    {
        string json = new BlueBillywigVideo { Type = BlueBillywigVideoType.Clip, Id = 9, LazyLoad = false }.ToJson();
        string html = await RichTextEmbedRewriter.RewriteAsync(
            $"<div class=\"bb-rte-video-ref\" data-bb-video=\"{System.Net.WebUtility.HtmlEncode(json)}\"></div>",
            video => Task.FromResult($"[{video.Id}:{video.LazyLoad}]"));

        Assert.Contains("[9:False]", html);
    }

    [Fact]
    public async Task A_hostile_class_or_attribute_cannot_inject_through_the_rewriter()
    {
        string html = await RichTextEmbedRewriter.RewriteAsync(
            """<div class="x umb-embed-holderish" data-embed-url="https://demo.bbvms.com/p/default/c/1.html">keep</div>""",
            _ => Task.FromResult("REPLACED"));

        Assert.Contains("keep", html);
    }

    private sealed class SwitchConsent(Func<bool> granted) : IBlueBillywigConsentProvider
    {
        public bool IsGranted(HttpContext? httpContext) => granted();
    }
}

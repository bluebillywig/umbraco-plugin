using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Strings;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>
/// Finding 1d: rich text is parsed, not pattern-matched. Any tag, any case, quoted or unquoted attributes and entity-encoded
/// class names lose the package vocabulary, on render and in the Delivery API form.
/// </summary>
public class RichTextHostileTests
{
    public static TheoryData<string> Hostile => new()
    {
        "<p>x</p><p class=bb-lazy-embed data-bb-embed=\"AAAA\">y</p>",
        "<p>x</p><DIV CLASS='bb-lazy-embed' DATA-BB-EMBED='AAAA'>y</DIV>",
        "<p>x</p><section class=\"intro bb-lazy-embed\" data-bb-embed=AAAA>y</section>",
        "<p>x</p><span class=\"a\tbb-cta-form bb-cta-form-config-e30\">y</span>",
        "<p>x</p><div class=\"bb&#45;lazy&#45;embed\" data-bb-embed=\"AAAA\">y</div>",
        "<p>x</p><div class=\"&#98;b-cta-form\">y</div>",
        "<p>x</p><div class=\"&#x62;b-lazy-embed\">y</div>",
        "<p>x</p><button type=button data-bb-consent-grant=\"1\">y</button>",
        "<p>x</p><a class=bb-chapter-link data-time=1 href=\"#\">y</a>",
        "<p>x</p><div class=\"bb-cta-form\"><div class=\"bb-cta-form\">y</div></div>",
        "<p>x</p><div class=\"bb-lazy-embed\" data-bb-embed=\"AAAA\">y",
        "<p>x</p><div data-bb-key=\"abc\" data-bb-hosts=\"*.evil.example\">y</div>",
    };

    private const string Forbidden = "bb-lazy|bb-cta|bb-consent|bb-chapter|data-bb-|&#45;|&#98;|&#x62;";

    private static void AssertClean(string html)
    {
        Assert.DoesNotMatch("(?i)" + Forbidden, html);
        Assert.Contains("x", html);
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task Hostile_markup_loses_the_package_vocabulary_when_rendered(string stored)
    {
        Assert.True(RichTextEmbedRewriter.MayContainEmbeds(stored), "the cheap test must not wave it through");

        string html = await RichTextEmbedRewriter.RewriteAsync(stored, _ => Task.FromResult("<b>unused</b>"));

        AssertClean(html);
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_markup_loses_the_package_vocabulary_in_the_delivery_api_form(string stored)
    {
        AssertClean(RichTextEmbedRewriter.ToReferences(stored));
    }

    [Fact]
    public void The_same_goes_through_the_deferred_value_the_page_prints()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(http: new DefaultHttpContext());
        var value = new DeferredRichText(new Html("<p>x</p><p class=bb-lazy-embed data-bb-embed=\"AAAA\">y</p><DIV CLASS='bb-cta-form'>z</DIV>"), renderer);

        AssertClean(value.ToHtmlString());
    }

    [Fact]
    public async Task Text_and_other_markup_survive_and_unrelated_rich_text_is_returned_as_it_was()
    {
        const string plain = "<p class=\"intro\">Hello <em>there</em> &amp; welcome</p>";
        Assert.Equal(plain, await RichTextEmbedRewriter.RewriteAsync(plain, _ => Task.FromResult("")));

        string cleaned = await RichTextEmbedRewriter.RewriteAsync("<h2 class=\"title bb-cta-form big\">Head</h2><p>Body</p>", _ => Task.FromResult(""));
        Assert.Contains("<h2 class=\"title big\">Head</h2>", cleaned);
        Assert.Contains("<p>Body</p>", cleaned);
    }

    [Fact]
    public async Task The_embed_the_renderer_makes_is_put_in_after_the_cleaning_and_keeps_its_own_vocabulary()
    {
        string html = await RichTextEmbedRewriter.RewriteAsync(
            "<p>a</p><span class=\"umb-embed-holder\" data-embed-url=\"https://demo.bbvms.com/p/default/c/1.html\"></span><p class=bb-cta-form>b</p>",
            _ => Task.FromResult("<div class=\"bb-lazy-embed\" data-bb-embed=\"REAL\" data-bb-key=\"K\"></div>"));

        Assert.Contains("<div class=\"bb-lazy-embed\" data-bb-embed=\"REAL\" data-bb-key=\"K\"></div>", html);
        Assert.Contains("<p>b</p>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "bb-lazy-embed"));
    }

    [Fact]
    public void A_block_reference_stays_a_reference_in_the_delivery_api_form_but_nothing_else_of_the_vocabulary_does()
    {
        string json = new BlueBillywigVideo { Type = BlueBillywigVideoType.Clip, Id = 9, LazyLoad = false }.ToJson();
        string api = RichTextEmbedRewriter.ToReferences($"<div class=\"bb-rte-video-ref\" data-bb-video=\"{System.Net.WebUtility.HtmlEncode(json)}\">inner</div><p class=bb-cta-form>x</p>");

        Assert.Contains("bb-rte-video-ref", api);
        Assert.Contains("data-bb-video", api);
        Assert.DoesNotContain("inner", api);
        Assert.DoesNotContain("bb-cta-form", api);
    }

    private sealed class Html(string value) : IHtmlEncodedString
    {
        public string ToHtmlString() => value;
    }
}

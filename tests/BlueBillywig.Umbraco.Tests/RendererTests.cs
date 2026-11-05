using System.Text;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Tests;

public class RendererTests
{
    [Theory]
    [InlineData(BlueBillywigVideoType.Clip)]
    [InlineData(BlueBillywigVideoType.Channel)]
    [InlineData(BlueBillywigVideoType.Shorts)]
    public async Task Consent_placeholder_references_nothing_from_bbvms(BlueBillywigVideoType type)
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create(consent: false);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Type = type, Id = 42, LazyLoad = true });

        Assert.Contains("bb-consent-placeholder", html);
        Assert.Contains("data-bb-consent-grant", html);
        Assert.DoesNotContain("bbvms", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-bb-embed", html);
        Assert.DoesNotContain("<noscript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.Requests);
        // The only script is the package's own loader.
        foreach (Match m in Regex.Matches(html, "<script[^>]*src=\"([^\"]+)\""))
        {
            Assert.StartsWith("/App_Plugins/BlueBillywig/", m.Groups[1].Value);
        }
    }

    [Fact]
    public async Task Consent_cookie_set_by_the_placeholder_button_grants_consent()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = "bb_consent=1";
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(consent: false, http: http);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42, LazyLoad = false });

        Assert.DoesNotContain("bb-consent-placeholder", html);
        Assert.Contains("demo.bbvms.com/p/default/c/42.js", html);
    }

    [Fact]
    public async Task Assets_ship_with_every_render_path_once_per_request()
    {
        var http = new DefaultHttpContext();
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(http: http);

        string first = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1 });
        string second = await Rig.Render(renderer, new BlueBillywigVideo { Id = 2 });

        Assert.Contains("bb-lazy.css", first);
        Assert.Contains("bb-lazy.js", first);
        Assert.DoesNotContain("bb-lazy.css", second);
    }

    [Fact]
    public async Task Assets_ship_even_when_consent_is_denied()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(consent: false);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1 });

        Assert.Contains("bb-lazy.css", html);
        Assert.Contains("bb-lazy.js", html);
    }

    [Fact]
    public async Task Invalid_video_renders_nothing()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();

        Assert.Equal("", (await renderer.RenderAsync(null)).ToHtmlString());
        Assert.Equal("", await Rig.Render(renderer, new BlueBillywigVideo { Id = 0 }));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Iframe_embed_fills_its_responsive_box()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/embedcode/5/default/javascript", """{"body":"<iframe src=\"https://demo.bbvms.com/p/default/c/5.html\" width=\"720\" height=\"405\"></iframe>"}""");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        // The bare 720x405 iframe sits inside an absolutely positioned fill-the-box wrapper in a responsive box...
        Assert.Contains("bb-embed-responsive", html);
        Assert.Contains("padding-bottom:56.25%", html);
        Assert.Contains("<div style=\"position:absolute;top:0;left:0;width:100%;height:100%;\"><iframe", html);

        // ...and the shipped stylesheet sizes the iframe itself to 100% of that wrapper (2.0.5).
        string css = File.ReadAllText(FindShippedAsset("bb-lazy.css"));
        Match rule = Regex.Match(css, @"\.bb-embed-responsive iframe\s*\{([^}]*)\}");
        Assert.True(rule.Success, "bb-lazy.css has no .bb-embed-responsive iframe rule");
        Assert.Matches(@"position:\s*absolute", rule.Groups[1].Value);
        Assert.Matches(@"width:\s*100%", rule.Groups[1].Value);
        Assert.Matches(@"height:\s*100%", rule.Groups[1].Value);
    }

    [Fact]
    public async Task Lazy_embed_carries_the_iframe_in_its_payload_and_a_noscript_copy()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/embedcode/5/default/javascript", """{"body":"<iframe src=\"https://demo.bbvms.com/p/default/c/5.html\"></iframe>"}""");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5 });

        Match payload = Regex.Match(html, "data-bb-embed=\"([^\"]+)\"");
        Assert.True(payload.Success);
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Groups[1].Value));
        Assert.Contains("<iframe src=\"https://demo.bbvms.com/p/default/c/5.html\">", decoded);
        Assert.Contains("<noscript><div", html);
        Assert.Contains("bb-lazy-ratio-16-9", html);
    }

    [Fact]
    public async Task Falls_back_to_the_script_embed_when_sapi_is_down()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create();

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.Contains("src=\"https://demo.bbvms.com/p/default/c/5.js\"", html);
    }

    [Fact]
    public async Task Embed_code_from_a_trusted_blue_billywig_host_is_kept()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/embedcode/5/default/javascript", """{"body":"<script type=\"text/javascript\" src=\"https://demo.dev.ovp.bluebillywig.eu/p/default/c/5.js\"  async=\"true\"></script>"}""");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.Contains("src=\"https://demo.dev.ovp.bluebillywig.eu/p/default/c/5.js\" async=\"true\"", html);
        Assert.DoesNotContain("demo.bbvms.com/p/default/c/5.js", html);
    }

    [Fact]
    public async Task Look_alike_hosts_are_not_trusted()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/embedcode/5/default/javascript", """{"body":"<script src=\"https://bluebillywig.eu.evil.example/p.js\"></script>"}""");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public async Task Our_nonce_replaces_any_upstream_nonce_on_script_tags()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create(nonce: "abc123");
        client.Json("/sapi/embedcode/5/default/javascript", """{"body":"<script nonce=\"smuggled\" src=\"https://demo.bbvms.com/p/default/c/5.js\"></script>"}""");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.DoesNotContain("smuggled", html);
        Assert.Contains("src=\"https://demo.bbvms.com/p/default/c/5.js\" nonce=\"abc123\"", html);
        Assert.Contains("bb-lazy.js?v=", html);
        Assert.Matches("bb-lazy\\.js[^>]*nonce=\"abc123\"", html);
    }

    [Theory]
    [InlineData("<script src=\"https://evil.example/x.js\"></script>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>")]
    [InlineData("<iframe srcdoc=\"<b>x</b>\" src=\"https://demo.bbvms.com/x\"></iframe><style>*{}</style>")]
    [InlineData("<div><script src=\"https://demo.bbvms.com/a.js\"></script")]
    public async Task Unexpected_embed_bodies_are_replaced_by_the_script_embed(string body)
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/embedcode/5/default/javascript", new System.Text.Json.Nodes.JsonObject { ["body"] = body }.ToJsonString());

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.DoesNotContain("evil.example", html);
        Assert.DoesNotContain("alert(1)", html);
        Assert.DoesNotContain("onerror", html);
        Assert.Contains("https://demo.bbvms.com/p/default/c/5.js", html);
    }

    [Fact]
    public async Task Clip_text_from_sapi_is_encoded()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/mediaclip/5", """
            {"title":"</script><img src=x onerror=alert(1)>","transcript":"line one <script>alert(2)</script>\nline two","isocode":"ar\"><x","chapters":[{"time":75,"title":"<b>Intro</b>"},{"time":3,"title":""}]}
            """);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, ShowChapters = true });

        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<b>Intro", html);
        Assert.Contains("&lt;script&gt;alert(2)", html);
        Assert.Contains("1:15", html);
        Assert.Contains("data-time=\"75\"", html);
        Assert.Contains("dir=\"ltr\"", html);
        Assert.DoesNotContain("data-time=\"3\"", html);
    }

    [Fact]
    public async Task Arabic_transcripts_are_rtl_and_transcript_can_be_switched_off()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/mediaclip/5", """{"title":"t","transcript":"hello","isocode":"ar-SA"}""");

        string on = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5 });
        string off = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, ShowTranscript = false });

        Assert.Contains("dir=\"rtl\"", on);
        Assert.Contains("lang=\"ar-SA\"", on);
        Assert.DoesNotContain("bb-transcript", off);
    }

    [Fact]
    public async Task Subtitles_are_the_transcript_fallback()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/mediaclip/5", """{"title":"t","subtitles":[{"id":"9","status":"draft"},{"id":"10","status":"published","isocode":"en","languagename":"English"}]}""");
        client.Raw("/sapi/subtitle/10/contents", "1\n00:00:01,000 --> 00:00:02,000\nHello <b>there</b>\n\n2\n00:00:03,000 --> 00:00:04,000\nBye");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5 });

        Assert.Contains("Transcript (English)", html);
        Assert.Contains("Hello &lt;b&gt;there&lt;/b&gt;", html);
        Assert.DoesNotContain("-->", html);
        Assert.DoesNotContain("/sapi/subtitle/9/", string.Join(' ', client.Requests));
    }

    [Fact]
    public async Task Channel_and_shorts_follow_the_reference_plugin_markup()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create();

        string channel = await Rig.Render(renderer, new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 8, Height = "500px", LazyLoad = false });
        string shorts = await Rig.Render(renderer, new BlueBillywigVideo { Type = BlueBillywigVideoType.Shorts, Id = 3, Height = "700px" });

        Assert.Contains("height:500px", channel);
        Assert.Contains("https://demo.bbvms.com/ch/8.js", channel);
        Assert.Contains("<noscript><a href=\"https://demo.bbvms.com/ch/8.html\">", channel);
        Assert.Contains("src=\"https://demo.bbvms.com/sh/3.html\"", shorts);
        Assert.Contains("max-width:400px", shorts);
        Assert.Contains("allow=\"autoplay; fullscreen\"", shorts);
    }

    [Fact]
    public async Task Another_publication_uses_bbvms_and_skips_sapi()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, Publication = "other", Playout = "wide", LazyLoad = false });

        Assert.Contains("https://other.bbvms.com/p/wide/c/5.js", html);
        Assert.Empty(client.Requests);
    }

    // Reference render_block: a clip with timelines gets overflow:visible and the bb-interactive class so overlay widgets
    // can leave the 16:9 box; any other clip stays overflow:hidden.
    [Fact]
    public async Task Clip_with_a_timeline_gets_visible_overflow_and_the_interactive_class()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/mediaclip/5", """{"title":"t","timelines":[{"id":"9"}]}""");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.Matches("class=\"bb-block-embed bb-embed-responsive bb-interactive\"", html);
        Assert.Contains("overflow:visible;", html);
        Assert.DoesNotContain("overflow:hidden;", html);
    }

    [Theory]
    [InlineData("""{"title":"t"}""")]
    [InlineData("""{"title":"t","timelines":[]}""")]
    public async Task Clip_without_a_timeline_keeps_hidden_overflow_and_no_interactive_class(string clip)
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/mediaclip/5", clip);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.DoesNotContain("bb-interactive", html);
        Assert.Contains("overflow:hidden;", html);
        Assert.DoesNotContain("overflow:visible;", html);
    }

    [Fact]
    public async Task Lazy_embed_of_a_clip_with_a_timeline_carries_the_interactive_class()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/mediaclip/5", """{"title":"t","timelines":[{"id":"9"}]}""");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = true });

        Assert.Contains("bb-embed-lazy bb-interactive\"", html);
    }

    // A failed clip read must not flip the wrapper: the plugin treats an error as "not interactive".
    [Fact]
    public async Task Failed_clip_read_is_not_treated_as_interactive()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create();

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5, LazyLoad = false });

        Assert.DoesNotContain("bb-interactive", html);
        Assert.Contains("overflow:hidden;", html);
    }

    [Fact]
    public void Double_encoded_utf8_is_repaired_but_correct_text_is_untouched()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string mojibake = Encoding.GetEncoding(1252).GetString(Encoding.UTF8.GetBytes("café €"));

        Assert.Equal("café €", BlueBillywigEmbedRenderer.FixDoubleEncodedUtf8(mojibake));
        Assert.Equal("café", BlueBillywigEmbedRenderer.FixDoubleEncodedUtf8("café"));
        Assert.Equal("وكان", BlueBillywigEmbedRenderer.FixDoubleEncodedUtf8("وكان"));
    }

    private static string FindShippedAsset(string name)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "BlueBillywig.Umbraco", "Client", "public", name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(name);
    }
}

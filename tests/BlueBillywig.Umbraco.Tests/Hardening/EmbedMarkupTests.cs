using System.Text;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>
/// Finding 1: markup that came from SAPI must not be able to speak the vocabulary of the lazy loader and the form overlay,
/// and the browser scripts act only on what the renderer itself made (a per-request key).
/// </summary>
public class EmbedMarkupTests
{
    private static bool Host(string host) => host.EndsWith(".bbvms.com", StringComparison.OrdinalIgnoreCase);

    private static string Prepare(string html)
    {
        Assert.True(EmbedHtml.TryPrepare(html, null, Host, out string result), "the body was rejected: " + html);
        return result;
    }

    private const string Payload = "PHNjcmlwdCBzcmM9aHR0cHM6Ly9ldmlsL3guanM+PC9zY3JpcHQ+"; // <script src=https://evil/x.js></script>

    [Theory]
    [InlineData("<div class=\"bb-lazy-embed\" data-bb-embed=\"" + Payload + "\"></div>")]
    [InlineData("<div class=bb-lazy-embed data-bb-embed=" + Payload + "></div>")]
    [InlineData("<DIV CLASS='BB-Lazy-Embed' DATA-BB-EMBED='" + Payload + "'></DIV>")]
    [InlineData("<span class=\"x bb-lazy-embed y\" data-bb-embed=\"" + Payload + "\"></span>")]
    [InlineData("<div class=\"bb&#45;lazy&#45;embed\" data-bb-embed=\"" + Payload + "\"></div>")]
    [InlineData("<div class=\"&#98;b-lazy-embed\" data-bb-embed=\"" + Payload + "\"></div>")]
    [InlineData("<div class=\"x\tbb-lazy-embed\" data-bb-embed=\"" + Payload + "\"></div>")]
    [InlineData("<div class=\"x\nbb-cta-form bb-cta-form-config-e30\"></div>")]
    [InlineData("<div class=\"bb-cta-form\"></div>")]
    [InlineData("<span data-bb-key=\"abc\" data-bb-hosts=\"*.evil.example\" data-bb-consent-grant=\"1\"></span>")]
    public void The_package_vocabulary_is_stripped_from_upstream_markup(string body)
    {
        string result = Prepare(body);

        Assert.DoesNotContain("data-bb-", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bb-lazy", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bb-cta", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Payload, result);
    }

    [Fact]
    public void Other_classes_and_attributes_are_kept_and_an_emptied_class_attribute_is_dropped()
    {
        string result = Prepare("<div class=\"player bb-x wide\" id=\"p1\"></div><span class=\"bb-only\"></span>");

        Assert.Contains("class=\"player wide\"", result);
        Assert.Contains("id=\"p1\"", result);
        Assert.Contains("<span></span>", result);
    }

    [Theory]
    [InlineData("<script src=\"http://demo.bbvms.com/p/default/c/1.js\"></script>")]
    [InlineData("<iframe src=\"http://demo.bbvms.com/p/default/c/1.html\"></iframe>")]
    public void Scripts_and_iframes_must_be_https_so_a_token_cannot_travel_in_cleartext(string body)
    {
        Assert.False(EmbedHtml.TryPrepare(body, null, Host, out _));
    }

    [Fact]
    public void Https_scripts_and_plain_http_links_are_still_accepted()
    {
        string result = Prepare("<script src=\"https://demo.bbvms.com/p/default/c/1.js\"></script><a href=\"http://demo.bbvms.com/x\">x</a>");

        Assert.Contains("https://demo.bbvms.com/p/default/c/1.js", result);
        Assert.Contains("href=\"http://demo.bbvms.com/x\"", result);
    }

    // The whole renderer ----------------------------------------------------------------------------------

    private static readonly Regex Key = new("data-bb-key=\"([A-Za-z0-9_-]+)\"");

    private static (BlueBillywigEmbedRenderer Renderer, FakeClient Client) Create(DefaultHttpContext http, string body, bool consent = true)
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create(consent: consent, http: http);
        client.Json("/sapi/embedcode/1/default/javascript", System.Text.Json.JsonSerializer.Serialize(new { body }));
        client.Json("/sapi/mediaclip/1", """{"id":1,"title":"T"}""");
        return (renderer, client);
    }

    private static string Decode(string html) =>
        string.Concat(Regex.Matches(html, "data-bb-embed=\"([A-Za-z0-9+/=]+)\"").Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value))));

    [Fact]
    public async Task A_forged_wrapper_in_a_sapi_embed_body_does_not_reach_the_page_in_any_form()
    {
        string body = $"<div class=\"bb-lazy-embed\" data-bb-embed=\"{Payload}\" data-bb-key=\"guess\"></div><div class=\"bb-cta-form\"></div><script src=\"https://demo.bbvms.com/p/default/c/1.js\"></script>";
        var (renderer, _) = Create(new DefaultHttpContext(), body);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1, LazyLoad = true });
        string payload = Decode(html);

        // One wrapper (the renderer's own) and nothing forged, in the page or in its payload.
        Assert.Single(Regex.Matches(html, "data-bb-embed="));
        Assert.DoesNotContain(Payload, html);
        Assert.DoesNotContain("bb-cta-form", html.Replace("bb-cta-form-overlay", ""));
        Assert.DoesNotContain("bb-lazy-embed", payload);
        Assert.DoesNotContain("data-bb-", payload);
        Assert.DoesNotContain("guess", html);
        Assert.Contains("demo.bbvms.com/p/default/c/1.js", payload);
    }

    [Fact]
    public async Task The_loader_overlay_container_and_wrapper_of_one_request_share_one_key_that_differs_per_request()
    {
        var first = new DefaultHttpContext();
        var (renderer, _) = Create(first, "<script src=\"https://demo.bbvms.com/p/default/c/1.js\"></script>");

        string one = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1, LazyLoad = true });
        string two = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1, LazyLoad = true });

        string[] keys = Key.Matches(one + two).Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Single(keys);
        Assert.True(keys[0].Length >= 20);
        // The loader script, the overlay script, the container and the wrapper all carry it.
        Assert.Matches("<script src=\"[^\"]*bb-lazy\\.js[^\"]*\"[^>]*data-bb-key=\"" + keys[0], one);
        Assert.Matches("<script src=\"[^\"]*bb-cta-form-overlay\\.js[^\"]*\"[^>]*data-bb-key=\"" + keys[0], one);
        Assert.Matches("class=\"bb-block-embed[^\"]*\"[^>]*data-bb-clip-id=\"1\" data-bb-key=\"" + keys[0], one);
        Assert.Matches("class=\"bb-lazy-embed[^\"]*\"[^>]*data-bb-embed=\"[^\"]+\" data-bb-key=\"" + keys[0] + "\" data-bb-hosts=\"", one);
        Assert.True(Regex.Matches(one + two, "data-bb-key=\"" + keys[0] + "\"").Count >= 6);

        var (other, _) = Create(new DefaultHttpContext(), "<script src=\"https://demo.bbvms.com/p/default/c/1.js\"></script>");
        string elsewhere = await Rig.Render(other, new BlueBillywigVideo { Id = 1, LazyLoad = true });
        Assert.NotEqual(keys[0], Key.Match(elsewhere).Groups[1].Value);
    }

    [Fact]
    public async Task The_wrapper_tells_the_loader_which_hosts_a_script_may_come_from()
    {
        var (renderer, _) = Create(new DefaultHttpContext(), "<script src=\"https://demo.bbvms.com/p/default/c/1.js\"></script>");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1, LazyLoad = true });

        string hosts = Regex.Match(html, "data-bb-hosts=\"([^\"]*)\"").Groups[1].Value;
        string[] list = hosts.Split(' ');
        Assert.Contains("demo.bbvms.com", list);
        Assert.Contains("*.bbvms.com", list);
        Assert.DoesNotContain(list, h => h.Contains("evil"));
    }

    [Fact]
    public async Task The_consent_placeholder_carries_the_key_so_only_its_own_button_can_record_consent()
    {
        var (renderer, _) = Create(new DefaultHttpContext(), "<script src=\"https://demo.bbvms.com/p/default/c/1.js\"></script>", consent: false);

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1 });

        string key = Key.Match(html).Groups[1].Value;
        Assert.NotEmpty(key);
        Assert.Matches("class=\"bb-consent-placeholder[^\"]*\"[^>]*data-bb-key=\"" + key + "\"", html);
    }

    [Fact]
    public async Task A_http_script_in_the_embed_code_falls_back_to_the_plain_script_embed()
    {
        var (renderer, _) = Create(new DefaultHttpContext(), "<script src=\"http://demo.bbvms.com/p/default/c/1.js\"></script>");

        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 1, LazyLoad = false });

        Assert.DoesNotContain("http://demo.bbvms.com", html);
        Assert.Contains("https://demo.bbvms.com/p/default/c/1.js", html);
    }
}

/// <summary>The shipped browser scripts, checked as files: what they must not do, and the pure host rules.</summary>
public class ShippedScriptTests
{
    private static string Script(string name)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BlueBillywig.Umbraco.slnx")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        return File.ReadAllText(Path.Combine(dir!, "src", "BlueBillywig.Umbraco", "Client", "public", name));
    }

    [Fact]
    public void The_loader_only_acts_on_wrappers_that_carry_the_requests_key()
    {
        string js = Script("bb-lazy.js");

        Assert.Contains("document.currentScript", js);
        Assert.Contains("data-bb-key", js);
        Assert.Contains("if ( ! trusted( el ) )", js);
        // Every way of finding wrappers is narrowed by the key.
        MatchCollection finders = Regex.Matches(js, @"querySelectorAll\( '\.bb-lazy-embed'(.*)");
        Assert.Equal(2, finders.Count);
        Assert.All(finders, m => Assert.Contains("KEY_SELECTOR", m.Groups[1].Value));
    }

    [Fact]
    public void The_loader_never_rebuilds_an_inline_script_or_copies_script_text()
    {
        string js = Script("bb-lazy.js");

        Assert.DoesNotContain("textContent = oldScript", js);
        Assert.DoesNotContain("oldScript.textContent", js);
        Assert.Contains("urlAllowed( src, hosts )", js);
        Assert.Contains("protocol === 'https:'", js);
    }

    [Fact]
    public void The_overlay_keeps_the_references_storage_shape_but_forgets_after_a_year_and_posts_only_to_https_or_local_http_from_a_local_page()
    {
        string js = Script("bb-cta-form-overlay.js");

        Assert.Contains("function remember(clipId, email) {", js);
        Assert.Contains("email: email,", js);
        Assert.Contains("REMEMBER_MS", js);
        Assert.Contains("localStorage.removeItem(PREFIX + clipId)", js);
        Assert.Contains("isLocalHost(parsed.hostname) && isLocalHost(page)", js);
        Assert.Contains("findContainer(el)", js);
    }
}

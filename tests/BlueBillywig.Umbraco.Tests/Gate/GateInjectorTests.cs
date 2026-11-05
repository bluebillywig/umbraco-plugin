using System.Text;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Gate;

namespace BlueBillywig.Umbraco.Tests.Gate;

/// <summary>G4. Ports ContentGateInjectJwtTest case by case, then adds the host, case and renderer-shape checks.</summary>
public class GateInjectorTests
{
    private const string Jwt = "fake.jwt.token";

    private static GateInjector Injector(Func<string, bool>? host = null, ListLogger? log = null) =>
        new(host ?? GateInjector.IsBbvmsHost, () => GateMessage.Render(false, null, null), log);

    private static string Run(string content, string jwt = Jwt, int depth = 0, ListLogger? log = null) => Injector(log: log).Inject(content, jwt, depth).Html;

    private static string Wrap(string inner, string extra = "") => $"<div class=\"bb-lazy-embed\"{extra} data-bb-embed=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(inner))}\"></div>";

    private static string Decode(string b64) => Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    // L2: the token is a credential and goes over https only -----------------------------------------------------------

    [Theory]
    [InlineData("http://pub.bbvms.com/p/default/c/123.js")]
    [InlineData("HTTP://pub.bbvms.com/p/default/c/123.js")]
    [InlineData("//pub.bbvms.com/p/default/c/123.js")]
    public void A_url_that_is_not_https_never_gets_the_token(string url)
    {
        InjectResult result = Injector().Inject($"<script src=\"{url}\"></script>", Jwt, 0);

        Assert.DoesNotContain("jwt", result.Html);
        Assert.Equal(0, result.Substitutions);
    }

    [Fact]
    public void An_https_url_next_to_an_http_one_is_the_only_one_that_gets_the_token()
    {
        string html = "<script src=\"http://pub.bbvms.com/p/default/c/1.js\"></script><script src=\"https://pub.bbvms.com/p/default/c/2.js\"></script>";

        string output = Run(html);

        Assert.Contains("c/2.js?jwt=fake.jwt.token", output);
        Assert.DoesNotContain("c/1.js?jwt", output);
        Assert.Single(Regex.Matches(output, "jwt="));
    }

    [Fact]
    public void A_scheme_in_capitals_is_still_https()
    {
        Assert.Contains("c/3.js?jwt=fake.jwt.token", Run("<script src=\"HTTPS://pub.bbvms.com/p/default/c/3.js\"></script>"));
    }

    [Fact]
    public void An_http_url_inside_a_lazy_wrapper_is_not_given_the_token_either()
    {
        string output = Run(Wrap("<script src=\"http://pub.bbvms.com/p/default/c/9.js\"></script>"));
        string payload = Regex.Match(output, "data-bb-embed=\"([^\"]+)\"").Groups[1].Value;

        Assert.DoesNotContain("jwt", Decode(payload));
    }

    // Port ------------------------------------------------------------------------------------------------

    [Fact]
    public void Appends_the_jwt_to_a_bare_bbvms_url()
    {
        string html = "<script src=\"https://pub.bbvms.com/p/default/c/123.js\"></script>";
        Assert.Contains("c/123.js?jwt=fake.jwt.token", Run(html));
    }

    [Fact]
    public void Preserves_an_existing_query_string()
    {
        string html = "<script src=\"https://pub.bbvms.com/p/default/c/123.js?placementOption=default\"></script>";
        string output = Run(html);

        // The round-3 bug: a greedy path class produced ...js?placementOption=default?jwt=..., which browsers parse as a broken URL.
        Assert.DoesNotContain("?placementOption=default?jwt=", output);
        Assert.Contains("?placementOption=default&jwt=fake.jwt.token", output);
    }

    [Fact]
    public void Is_idempotent_on_an_already_injected_url()
    {
        string output = Run("<script src=\"https://pub.bbvms.com/p/default/c/123.js?jwt=prior.value\"></script>");
        Assert.Single(Regex.Matches(output, "jwt="));
        Assert.Contains("jwt=prior.value", output);
    }

    [Fact]
    public void Handles_a_url_without_an_extension()
    {
        Assert.Contains("ch/42?jwt=fake.jwt.token", Run("<a href=\"https://pub.bbvms.com/ch/42\">channel</a>"));
    }

    [Fact]
    public void Swaps_the_base64_data_bb_embed_on_decode_success()
    {
        string wrapper = Wrap("<script src=\"https://pub.bbvms.com/p/default/c/999.js\"></script>");
        string output = Run(wrapper);

        Assert.Contains("data-bb-embed=\"", output);
        Assert.Contains("class=\"bb-lazy-embed\"", output);
        Match m = Regex.Match(output, "data-bb-embed=\"([^\"]+)\"");
        Assert.True(m.Success);
        Assert.Contains("c/999.js?jwt=fake.jwt.token", Decode(m.Groups[1].Value));
    }

    // 'AAAA=AA' matches the wrapper's character class but fails strict base64 (a '=' in the middle).
    private const string MalformedB64 = "AAAA=AA";

    [Fact]
    public void Replaces_the_placeholder_with_the_gate_message_on_decode_failure()
    {
        Assert.False(Convert.TryFromBase64String(MalformedB64, new byte[16], out _), "precondition: the payload must fail strict base64 decode");
        var log = new ListLogger();
        string output = Run($"<div class=\"bb-lazy-embed\" data-bb-embed=\"{MalformedB64}\"></div>", log: log);

        Assert.DoesNotContain("data-bb-embed=", output);
        Assert.Contains("bb-content-gate", output);
        Assert.True(log.Logged("decode failed"));
    }

    [Fact]
    public void Strips_the_sibling_noscript_on_decode_failure()
    {
        // Round 5: with only the div swapped, pass 2 would inject a JWT into the noscript URL and leak a working gated URL to visitors without JavaScript.
        string html = $"<div class=\"bb-lazy-embed\" data-bb-embed=\"{MalformedB64}\"></div><noscript><script src=\"https://pub.bbvms.com/p/default/c/1.js\"></script></noscript>";
        string output = Run(html);

        Assert.DoesNotContain("<noscript>", output);
        Assert.DoesNotContain("c/1.js?jwt=", output);
        Assert.DoesNotContain("c/1.js", output);
        Assert.Contains("bb-content-gate", output);
    }

    [Fact]
    public void Depth_cap_fails_closed()
    {
        // Past depth 2 the result must be the gate message, never the original content: returning it unchanged would let a
        // crafted nested-base64 payload bypass the gate once the outer wrapper is re-encoded.
        string html = "<script src=\"https://pub.bbvms.com/p/default/c/123.js\"></script>";
        InjectResult result = Injector().Inject(html, "jwt.value", 3);
        Assert.True(result.FailedClosed);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.DoesNotContain("c/123.js", result.Html);
    }

    [Fact]
    public void Multiple_adjacent_placeholders_each_get_the_token()
    {
        string a = Convert.ToBase64String(Encoding.UTF8.GetBytes("<script src=\"https://pub.bbvms.com/p/default/c/1.js\"></script>"));
        string b = Convert.ToBase64String(Encoding.UTF8.GetBytes("<script src=\"https://pub.bbvms.com/p/default/c/2.js\"></script>"));
        string html = $"<div class=\"bb-lazy-embed\" data-bb-embed=\"{a}\"></div><p>spacer</p><div class=\"bb-lazy-embed\" data-bb-embed=\"{b}\"></div>";

        MatchCollection matches = Regex.Matches(Run(html), "data-bb-embed=\"([^\"]+)\"");
        Assert.Equal(2, matches.Count);
        Assert.Contains("c/1.js?jwt=fake.jwt.token", Decode(matches[0].Groups[1].Value));
        Assert.Contains("c/2.js?jwt=fake.jwt.token", Decode(matches[1].Groups[1].Value));
    }

    // Nested payloads -------------------------------------------------------------------------------------

    [Fact]
    public void One_and_two_levels_of_nesting_are_injected_at_every_level()
    {
        string inner = "<script src=\"https://pub.bbvms.com/p/default/c/5.js\"></script>";
        string level1 = Wrap(inner);
        string level2 = Wrap(level1);

        string output = Run(level2);
        string firstLayer = Decode(Regex.Match(output, "data-bb-embed=\"([^\"]+)\"").Groups[1].Value);
        string secondLayer = Decode(Regex.Match(firstLayer, "data-bb-embed=\"([^\"]+)\"").Groups[1].Value);
        Assert.Contains("c/5.js?jwt=fake.jwt.token", secondLayer);
    }

    [Fact]
    public void A_payload_nested_past_the_cap_leaves_nothing_of_the_embed_behind()
    {
        string html = "<script src=\"https://pub.bbvms.com/p/default/c/777.js\"></script>";
        for (int i = 0; i < 5; i++)
        {
            html = Wrap(html);
        }

        var log = new ListLogger();
        InjectResult result = Injector(log: log).Inject(html, Jwt);
        Assert.True(result.FailedClosed);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.DoesNotContain("data-bb-embed", result.Html);
        Assert.DoesNotContain("777", result.Html);
        Assert.True(log.Logged("depth cap"));

        // Two wrappers inside one another (content at depth 2) still work; a third is past the cap.
        string atCap = "<script src=\"https://pub.bbvms.com/p/default/c/777.js\"></script>";
        for (int i = 0; i < 2; i++)
        {
            atCap = Wrap(atCap);
        }

        Assert.False(Injector().Inject(atCap, Jwt).FailedClosed);
    }

    [Fact]
    public void An_inner_failure_replaces_the_whole_wrapper_including_the_noscript()
    {
        string badInner = Wrap("x").Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes("x")), MalformedB64);
        string outer = Wrap(badInner) + "<noscript><script src=\"https://pub.bbvms.com/p/default/c/9.js\"></script></noscript>";
        InjectResult result = Injector().Inject(outer, Jwt);
        Assert.True(result.FailedClosed);
        Assert.DoesNotContain("data-bb-embed", result.Html);
        Assert.DoesNotContain("noscript", result.Html);
        Assert.DoesNotContain("c/9.js", result.Html);
    }

    [Fact]
    public void Invalid_utf8_inside_a_payload_fails_closed()
    {
        string payload = Convert.ToBase64String([0x3C, 0xFF, 0xFE, 0x3E]);
        InjectResult result = Injector().Inject($"<div data-bb-embed=\"{payload}\"></div>", Jwt);
        Assert.True(result.FailedClosed);
        Assert.Contains("bb-content-gate", result.Html);
    }

    // Variants the plugin's pattern did not match (fail-open bypasses) ---------------------------------------

    [Theory]
    [InlineData("<DIV class=\"x\" DATA-BB-EMBED=\"{0}\"></DIV>")]
    [InlineData("<div class=\"x\" data-bb-embed='{0}'></div>")]
    [InlineData("<div class=\"x\" data-bb-embed={0}></div>")]
    [InlineData("<div class=\"x\" data-bb-embed = \"{0}\"></div>")]
    [InlineData("<span data-bb-embed=\"{0}\"></span>")]
    public void A_lazy_attribute_the_pattern_cannot_rewrite_fails_closed(string template)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("<script src=\"https://pub.bbvms.com/p/default/c/3.js\"></script>"));
        InjectResult result = Injector().Inject(string.Format(template, payload), Jwt);
        if (template.StartsWith("<DIV", StringComparison.Ordinal))
        {
            // Tag and attribute names are case-insensitive in HTML, and so is the pattern.
            Assert.False(result.FailedClosed);
            Assert.Contains("c/3.js?jwt=", Decode(Regex.Match(result.Html, "data-bb-embed=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value));
            return;
        }

        Assert.True(result.FailedClosed, template);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.DoesNotContain("data-bb-embed", result.Html);
    }

    [Fact]
    public void A_mention_of_the_attribute_inside_a_wrapper_body_also_fails_closed()
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("<script src=\"https://pub.bbvms.com/p/default/c/3.js\"></script>"));
        InjectResult result = Injector().Inject($"<div data-bb-embed=\"{payload}\"><i data-bb-embed=\"{payload}\"></i></div>", Jwt);
        Assert.True(result.FailedClosed);
    }

    // Hosts: the token is a credential ----------------------------------------------------------------------

    [Theory]
    [InlineData("<script src=\"https://evil.example/p/default/c/1.js\"></script>")]
    [InlineData("<script src=\"https://evil.example/x.bbvms.com/p/default/c/1.js\"></script>")]
    [InlineData("<script src=\"https://bbvms.com.evil.example/p/default/c/1.js\"></script>")]
    [InlineData("<script src=\"https://pub.bbvms.com.evil.example/p/default/c/1.js\"></script>")]
    [InlineData("<script src=\"https://notbbvms.com/p/default/c/1.js\"></script>")]
    [InlineData("<a href=\"https://evil.example/?u=https://pub.bbvms.com/p/default/c/1.js\">x</a>")]
    [InlineData("<a href=\"https://evil.example/redirect/https://pub.bbvms.com/p/default/c/1.js\">x</a>")]
    [InlineData("<a href=\"https://evil.example/x?y=//pub.bbvms.com/p/default/c/1.js\">x</a>")]
    [InlineData("<img src=https://pub.bbvms.com/p/default/c/1.js>")]
    public void The_token_never_goes_to_another_host(string html)
    {
        string output = Run(html);
        Assert.DoesNotContain("jwt=", output);
        Assert.Equal(html, output);
    }

    [Fact]
    public void Subdomains_and_ports_of_a_gated_host_get_the_token_over_https_but_a_protocol_relative_url_does_not()
    {
        Assert.DoesNotContain("jwt", Run("<script src=\"//pub.bbvms.com/p/default/c/1.js\"></script>"));
        Assert.Contains("c/1.js?jwt=fake.jwt.token", Run("<script src=\"https://PUB.BBVMS.COM:443/p/default/c/1.js\"></script>"));
        Assert.Contains("view/default/1.html?jwt=fake.jwt.token", Run("<iframe src=\"https://a.b.bbvms.com/view/default/1.html\"></iframe>"));
        Assert.Contains("ch/1.html?jwt=", Run("<iframe src=\"https://pub.bbvms.com/ch/1.html\"></iframe>"));
        Assert.Contains("amp/default/1.html?jwt=", Run("<amp-iframe src=\"https://pub.bbvms.com/amp/default/1.html\"></amp-iframe>"));
        Assert.Contains("cpp/abc/1.js?jwt=", Run("<script src='https://pub.bbvms.com/cpp/abc/1.js'></script>"));
    }

    [Fact]
    public void The_configured_player_host_can_be_added_but_nothing_else_widens()
    {
        Func<string, bool> hosts = h => GateInjector.IsBbvmsHost(h) || h.Equals("player.bb.dev.example", StringComparison.OrdinalIgnoreCase);
        string html = "<script src=\"https://player.bb.dev.example/p/default/c/1.js\"></script><script src=\"https://other.example/p/default/c/1.js\"></script>";
        string output = Injector(hosts).Inject(html, Jwt).Html;
        Assert.Contains("player.bb.dev.example/p/default/c/1.js?jwt=", output);
        Assert.Contains("other.example/p/default/c/1.js\"", output);
        Assert.DoesNotContain("other.example/p/default/c/1.js?jwt", output);
        Assert.False(GateInjector.IsBbvmsHost("evilbbvms.com"));
        Assert.True(GateInjector.IsBbvmsHost("bbvms.com"));
    }

    [Fact]
    public void Entities_in_the_query_keep_their_separator_and_urls_ending_in_a_question_mark_work()
    {
        Assert.Contains("a=1&amp;jwt=fake.jwt.token", Run("<iframe src=\"https://pub.bbvms.com/p/default/c/1.html?a=1&amp;b=2\"></iframe>").Replace("b=2&amp;", ""));
        Assert.Contains("?&jwt=fake.jwt.token", Run("<a href=\"https://pub.bbvms.com/ch/1?\">x</a>"));
        // A parameter whose name merely ends in jwt is not a token.
        Assert.Contains("&jwt=fake.jwt.token", Run("<a href=\"https://pub.bbvms.com/ch/1?notjwt=1\">x</a>"));
    }

    [Fact]
    public void A_token_with_url_special_characters_is_url_encoded()
    {
        string output = Run("<script src=\"https://pub.bbvms.com/p/default/c/1.js\"></script>", "a+b/c=d e");
        Assert.Contains("?jwt=a%2Bb%2Fc%3Dd+e", output);
    }

    [Fact]
    public void Content_without_any_player_url_reports_zero_substitutions()
    {
        InjectResult result = Injector().Inject("<p>hello</p>", Jwt);
        Assert.Equal(0, result.Substitutions);
        Assert.False(result.FailedClosed);
        Assert.Equal("<p>hello</p>", result.Html);
    }

    [Fact]
    public void Pathological_input_does_not_hang()
    {
        string html = string.Concat(Enumerable.Repeat("<div data-bb-embed=\"AAAA\"", 3000)) + new string('x', 200_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        InjectResult result = Injector().Inject(html, Jwt);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.True(result.FailedClosed || result.Substitutions == 0);
    }

    // The exact wrapper the renderer emits ------------------------------------------------------------------

    [Fact]
    public async Task The_real_renderer_output_gets_the_token_in_the_payload_and_in_the_noscript_copy()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create();
        client.Json("/sapi/embedcode/5/default/javascript", """{"body":"<script type=\"text/javascript\" src=\"https://demo.bbvms.com/p/default/c/5.js\"></script>"}""");
        string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 5 });
        Assert.Contains("data-bb-embed=\"", html);
        Assert.Contains("<noscript>", html);

        // The page's own host serves the player here, as on a development publication.
        Func<string, bool> hosts = h => GateInjector.IsBbvmsHost(h) || h == "demo.bbvms.com";
        InjectResult result = Injector(hosts).Inject(html, Jwt);
        Assert.False(result.FailedClosed);

        string payload = Regex.Match(result.Html, "data-bb-embed=\"([^\"]+)\"").Groups[1].Value;
        Assert.Contains("c/5.js?jwt=fake.jwt.token", Decode(payload));
        string noscript = Regex.Match(result.Html, "<noscript>([\\s\\S]*?)</noscript>").Groups[1].Value;
        Assert.Contains("c/5.js?jwt=fake.jwt.token", noscript);
        // The package's own assets are not touched.
        Assert.DoesNotMatch("bb-lazy\\.(js|css)[^\"]*jwt=", result.Html);

        // The same HTML with the payload broken drops both the div and the noscript.
        string broken = Regex.Replace(html, "data-bb-embed=\"[^\"]+\"", $"data-bb-embed=\"{MalformedB64}\"");
        InjectResult closed = Injector(hosts).Inject(broken, Jwt);
        Assert.True(closed.FailedClosed);
        Assert.DoesNotContain("c/5.js", closed.Html);
    }
}

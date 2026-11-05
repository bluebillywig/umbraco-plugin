using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests.Gate;

/// <summary>A renderer wired to the real gate, the real SAPI-backed policy lookup (over the fake client) and fakes for the rest.</summary>
internal sealed class GateRig
{
    public const string Secret = "cpp-shared-secret-0123456789abcdef";

    public BlueBillywigEmbedRenderer Renderer { get; }
    public FakeClient Client { get; } = new();
    public FakeSettings Settings { get; } = new();
    public FakeGateSettings GateSettings { get; } = new();
    public FakeGateSecrets Secrets { get; } = new() { SharedSecret = Secret };
    public FakeViewers Viewers { get; } = new();
    public GateHealthTally Health { get; } = new();
    public SapiCppLookup Cpp { get; }
    public DefaultHttpContext Http { get; }

    public GateRig(bool consent = true, string? nonce = null, string embed = "javascript")
    {
        Http = new DefaultHttpContext();
        Settings.Value = Settings.Value with { Embed = embed };
        Cpp = new SapiCppLookup(Client, new FakeTime());
        var factory = new ContentGateFactory(GateSettings, Secrets, Viewers, Cpp, Health, new FakeTime());
        Renderer = new BlueBillywigEmbedRenderer(Settings, Client, new FixedConsent(consent), new FixedNonce(nonce), new HttpContextAccessor { HttpContext = Http }, NullLogger<BlueBillywigEmbedRenderer>.Instance, gates: factory);
    }

    public void Clip(long id, string? cpp, string extra = "")
    {
        string cppPart = cpp is null ? "" : $",\"cpp\":\"{cpp}\"";
        Client.Json($"/sapi/mediaclip/{id}", $$"""{"id":"{{id}}","title":"Film {{id}}","description":"d","status":"published","createddate":"2026-11-01T10:00:00Z","length":65{{cppPart}}{{extra}}}""");
    }

    public void Channel(long id, string? cpp)
    {
        string cppPart = cpp is null ? "" : $",\"cpp\":\"{cpp}\"";
        Client.Json($"/sapi/channel/{id}", $$"""{"id":"{{id}}","name":"Kanaal"{{cppPart}}}""");
    }

    public async Task<string> RenderAsync(BlueBillywigVideo video) => (await Renderer.RenderAsync(video)).ToHtmlString();

    public static BlueBillywigVideo ClipVideo(long id, bool lazy = true, bool transcript = false) =>
        new() { Type = BlueBillywigVideoType.Clip, Id = id, LazyLoad = lazy, ShowTranscript = transcript };

    /// <summary>Everything a browser could load: the markup itself, the decoded lazy payloads, and the noscript copy.</summary>
    public static string Unwrapped(string html)
    {
        var all = new StringBuilder(html);
        foreach (Match m in Regex.Matches(html, "data-bb-embed=\"([A-Za-z0-9+/=]+)\""))
        {
            all.Append('\n').Append(Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value)));
        }

        return all.ToString();
    }

    public static string TokenIn(string html)
    {
        Match m = Regex.Match(Unwrapped(html), "jwt=([A-Za-z0-9_.%-]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : throw new Xunit.Sdk.XunitException("no jwt= in: " + html);
    }
}

/// <summary>G1 to G6 through the real render chokepoint: nothing renders a protected embed without passing the gate.</summary>
public class GateRenderingTests
{
    private const string Cpp = "545179f0-1503-46d6-9cc7-041fab63fc48";

    private static GateRig Enabled(bool consent = true, string embed = "javascript")
    {
        var rig = new GateRig(consent, embed: embed);
        rig.GateSettings.Enabled = true;
        return rig;
    }

    [Fact]
    public async Task With_the_gate_off_a_protected_clip_renders_as_before_and_no_policy_is_looked_up()
    {
        var rig = new GateRig();
        rig.GateSettings.Enabled = false;
        rig.Clip(42, Cpp);

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("demo.bbvms.com/p/default/c/42.js", GateRig.Unwrapped(html));
        Assert.DoesNotContain("jwt=", GateRig.Unwrapped(html));
        Assert.False(rig.Http.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task An_unprotected_clip_passes_through_without_a_token_or_a_private_header()
    {
        GateRig rig = Enabled();
        rig.Clip(42, null);
        rig.Viewers.Viewer = _ => FakeViewers.Member();

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("demo.bbvms.com/p/default/c/42.js", GateRig.Unwrapped(html));
        Assert.DoesNotContain("jwt=", GateRig.Unwrapped(html));
        Assert.False(rig.Http.Response.Headers.ContainsKey("Cache-Control"));
        Assert.True(rig.Health.Snapshot().IsHealthy);
    }

    [Fact]
    public async Task An_anonymous_visitor_gets_the_gate_message_and_nothing_of_the_clip_G5()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp, ",\"transcript\":\"secret words spoken in the film\"");
        var video = GateRig.ClipVideo(42, transcript: true);

        string html = await rig.RenderAsync(video);
        string everything = GateRig.Unwrapped(html);

        Assert.Contains("bb-content-gate", html);
        Assert.DoesNotContain("/c/42", everything);
        Assert.DoesNotContain("secret words", everything);
        Assert.DoesNotContain("jwt=", everything);
        Assert.Equal("private, no-store", rig.Http.Response.Headers.CacheControl.ToString());
        Assert.True(rig.Health.Snapshot().IsHealthy); // a legitimate denial is not an error
    }

    [Fact]
    public async Task A_member_of_an_allowed_group_gets_a_verifiable_token_in_the_lazy_payload_and_the_noscript_G1_G4()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => FakeViewers.Member("member-guid-1");

        string html = await rig.RenderAsync(GateRig.ClipVideo(42, lazy: true));

        // Both places a browser could load the player from carry the token.
        string payload = Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Match(html, "data-bb-embed=\"([A-Za-z0-9+/=]+)\"").Groups[1].Value));
        Assert.Contains("/c/42.js?jwt=", payload);
        Assert.Matches(@"<noscript>.*/c/42\.js\?jwt=", html.Replace("\n", " "));
        string token = GateRig.TokenIn(html);
        Assert.True(GateJwt.TryVerify(token, GateRig.Secret, new FakeTime().GetUtcNow(), out JsonObject? claims));
        Assert.Equal(["42"], claims!["entity_access"]!["mediaclip"]!.AsArray().Select(n => (string)n!));
        Assert.StartsWith("u_", (string)claims["sub"]!);
        Assert.DoesNotContain("member-guid-1", (string)claims["sub"]!);
        Assert.DoesNotContain("member-guid-1", html);
        Assert.Equal("private, no-store", rig.Http.Response.Headers.CacheControl.ToString());
        Assert.True(rig.Health.Snapshot().IsHealthy);
    }

    [Fact]
    public async Task An_eagerly_rendered_embed_carries_the_token_too()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => FakeViewers.Member();

        string html = await rig.RenderAsync(GateRig.ClipVideo(42, lazy: false));

        Assert.Contains("/p/default/c/42.js?jwt=", html);
        Assert.DoesNotContain("data-bb-embed", html);
    }

    [Fact]
    public async Task A_member_who_may_not_watch_gets_the_logged_in_message()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => FakeViewers.Member(allowed: false);

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("does not have permission", html);
        Assert.DoesNotContain("/c/42", GateRig.Unwrapped(html));
        Assert.Equal("private, no-store", rig.Http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task A_channel_is_gated_the_same_way()
    {
        GateRig rig = Enabled();
        rig.Channel(7, Cpp);
        var channel = new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 7, LazyLoad = true };

        string denied = await rig.RenderAsync(channel);
        Assert.Contains("bb-content-gate", denied);
        Assert.DoesNotContain("/ch/7", GateRig.Unwrapped(denied));

        rig.Viewers.Viewer = _ => FakeViewers.Member();
        rig.Http.Response.Headers.Clear();
        string allowed = await rig.RenderAsync(channel);
        JsonObject claims = Gates.Claims(GateRig.TokenIn(allowed));
        Assert.Equal(["7"], claims["entity_access"]!["channel"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("/ch/7.js?jwt=", GateRig.Unwrapped(allowed));
    }

    [Fact]
    public async Task Shorts_are_not_gated_because_they_have_no_policy()
    {
        GateRig rig = Enabled();
        var shorts = new BlueBillywigVideo { Type = BlueBillywigVideoType.Shorts, Id = 3 };

        string html = await rig.RenderAsync(shorts);

        Assert.Contains("/sh/3.html", html);
        Assert.Empty(rig.Client.Requests.Where(r => r.Contains("mediaclip") || r.Contains("channel")));
    }

    [Fact]
    public async Task When_sapi_is_unreachable_the_gate_message_shows_and_the_error_is_tallied_G5_G7()
    {
        GateRig rig = Enabled();
        rig.Client.Responses["/sapi/mediaclip/42"] = SapiResult.Failure(0, "Could not reach Blue Billywig");
        rig.Viewers.Viewer = _ => FakeViewers.Member();

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-content-gate", html);
        Assert.DoesNotContain("/c/42", GateRig.Unwrapped(html));
        Assert.Equal(1, rig.Health.Snapshot().ErrorCounts["sapi_error"]);
        Assert.Equal("private, no-store", rig.Http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Without_credentials_the_gate_closes_instead_of_showing_the_embed()
    {
        GateRig rig = Enabled();
        rig.Client.IsConnected = false;

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-content-gate", html);
        Assert.DoesNotContain("/c/42", GateRig.Unwrapped(html));
        Assert.Equal(1, rig.Health.Snapshot().ErrorCounts["sapi_disconnected"]);
    }

    [Fact]
    public async Task A_missing_secret_closes_the_gate_even_for_an_allowed_member()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Secrets.SharedSecret = null;
        rig.Viewers.Viewer = _ => FakeViewers.Member();

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-content-gate", html);
        Assert.DoesNotContain("/c/42", GateRig.Unwrapped(html));
        Assert.Equal(1, rig.Health.Snapshot().ErrorCounts["secret_missing"]);
    }

    [Fact]
    public async Task A_viewer_provider_that_throws_closes_the_gate()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => throw new InvalidOperationException("boom");

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-content-gate", html);
        Assert.DoesNotContain("/c/42", GateRig.Unwrapped(html));
        Assert.Equal(1, rig.Health.Snapshot().ErrorCounts["internal_error"]);
    }

    [Fact]
    public async Task A_token_is_never_sent_when_the_response_can_no_longer_be_marked_private()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => FakeViewers.Member();
        rig.Http.Features.Set<IHttpResponseFeature>(new StartedResponse());

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-content-gate", html);
        Assert.DoesNotContain("jwt=", GateRig.Unwrapped(html));
        Assert.Equal(1, rig.Health.Snapshot().ErrorCounts["cache_unsafe"]);
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    [Fact]
    public async Task A_token_response_is_kept_out_of_the_output_cache_G6()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => FakeViewers.Member();
        var feature = new FakeOutputCacheFeature();
        rig.Http.Features.Set<IOutputCacheFeature>(feature);

        await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.False(feature.Context.AllowCacheStorage);
        Assert.Equal("no-cache", rig.Http.Response.Headers.Pragma.ToString());
    }

    private sealed class FakeOutputCacheFeature : IOutputCacheFeature
    {
        public OutputCacheContext Context { get; } = new() { HttpContext = new DefaultHttpContext(), AllowCacheStorage = true };
    }

    [Fact]
    public async Task Several_embeds_on_a_page_resolve_the_member_once_and_all_carry_tokens()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Clip(43, Cpp);
        int asked = 0;
        rig.Viewers.Viewer = _ => { asked++; return FakeViewers.Member(); };

        string a = await rig.RenderAsync(GateRig.ClipVideo(42));
        string b = await rig.RenderAsync(GateRig.ClipVideo(43, lazy: false));

        Assert.Contains("jwt=", GateRig.Unwrapped(a));
        Assert.Contains("jwt=", b);
        Assert.Equal(2, asked); // once per gated embed; the member lookup behind it is cached per request (see MemberViewerProviderTests)
    }

    [Fact]
    public async Task A_protected_clip_in_rich_text_goes_through_the_gate_too()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        string rte = "<p>Before</p><div class=\"umb-embed-holder\" data-embed-url=\"https://demo.bbvms.com/p/default/c/42.html\"><span class=\"bb-oembed-ref\">x</span></div><p>After</p>";

        string denied = await RichTextEmbedRewriter.RewriteAsync(rte, async v => (await rig.Renderer.RenderAsync(v)).ToHtmlString());
        Assert.Contains("bb-content-gate", denied);
        Assert.DoesNotContain("/c/42", GateRig.Unwrapped(denied));

        rig.Viewers.Viewer = _ => FakeViewers.Member();
        string allowed = await RichTextEmbedRewriter.RewriteAsync(rte, async v => (await rig.Renderer.RenderAsync(v)).ToHtmlString());
        Assert.Contains("jwt=", GateRig.Unwrapped(allowed));
    }

    // Consent ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_consent_an_allowed_member_gets_the_placeholder_and_nothing_from_the_player_host()
    {
        GateRig rig = Enabled(consent: false);
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => FakeViewers.Member();

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-consent-placeholder", html);
        Assert.DoesNotContain("bbvms", html);
        Assert.DoesNotContain("jwt=", html);
    }

    [Fact]
    public async Task Without_consent_a_visitor_who_may_not_watch_is_told_so_instead_of_asked_to_consent()
    {
        GateRig rig = Enabled(consent: false);
        rig.Clip(42, Cpp);

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-content-gate", html);
        Assert.DoesNotContain("bb-consent-placeholder", html);
        Assert.DoesNotContain("bbvms", html);
    }

    [Fact]
    public async Task Without_consent_an_unprotected_clip_shows_the_ordinary_placeholder()
    {
        GateRig rig = Enabled(consent: false);
        rig.Clip(42, null);

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("bb-consent-placeholder", html);
        Assert.False(rig.Http.Response.Headers.ContainsKey("Cache-Control"));
    }

    // The host rule ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_token_goes_to_the_publications_own_host_even_when_it_is_not_under_bbvms_com()
    {
        GateRig rig = Enabled();
        rig.Settings.Value = rig.Settings.Value with { BaseUrl = "https://bb.dev.example-ovp.eu" };
        rig.Clip(42, Cpp);
        rig.Viewers.Viewer = _ => FakeViewers.Member();

        string html = await rig.RenderAsync(GateRig.ClipVideo(42, lazy: false));

        Assert.Contains("https://bb.dev.example-ovp.eu/p/default/c/42.js?jwt=", html);
    }

    [Fact]
    public async Task The_token_is_not_added_to_a_player_url_on_a_foreign_host()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Client.Raw("/sapi/embedcode/42/default/javascript", "<script src=\"https://evil.example/p/default/c/42.js\"></script>");
        rig.Viewers.Viewer = _ => FakeViewers.Member();

        string html = await rig.RenderAsync(GateRig.ClipVideo(42, lazy: false));

        // The foreign embed body is refused by the embed scanner and replaced by the script embed on our host; either way no foreign URL has a token.
        Assert.DoesNotContain("evil.example/p/default/c/42.js?jwt=", html);
    }

    // JSON-LD (Q1, Q2) -------------------------------------------------------------------------------------

    private static JsonObject LdFrom(string html)
    {
        Match m = Regex.Match(html, "<script type=\"application/ld\\+json\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(m.Success, "no JSON-LD in: " + html);
        return JsonNode.Parse(m.Groups[1].Value)!.AsObject();
    }

    [Theory]
    [InlineData("iframe")]
    [InlineData("oembed")]
    [InlineData("url")]
    [InlineData("amp")]
    public async Task Json_ld_is_emitted_for_the_embed_types_the_plugin_emits_it_for(string embed)
    {
        var rig = new GateRig(embed: embed);
        rig.Clip(42, null, ",\"transcript\":\"hello <b>world</b>\",\"thumbnails\":[{\"src\":\"/t/42.jpg\",\"main\":true}]");
        rig.Client.Json("/sapi/publication", "{\"defaultMediaAssetPath\":\"https://cdn.example\"}");

        JsonObject ld = LdFrom(await rig.RenderAsync(GateRig.ClipVideo(42)));

        Assert.Equal("VideoObject", (string)ld["@type"]!);
        Assert.Equal("Film 42", (string)ld["name"]!);
        Assert.Equal("PT65S", (string)ld["duration"]!);
        Assert.Equal("https://cdn.example/t/42.jpg", (string)ld["thumbnailUrl"]!);
        Assert.Equal("2026-11-01T10:00:00Z", (string)ld["uploadDate"]!);
        Assert.Equal("hello world", (string)ld["transcript"]!);
    }

    [Fact]
    public async Task The_javascript_embed_brings_its_own_json_ld_so_none_is_emitted()
    {
        var rig = new GateRig(embed: "javascript");
        rig.Clip(42, null);

        Assert.DoesNotContain("ld+json", await rig.RenderAsync(GateRig.ClipVideo(42)));
    }

    [Fact]
    public async Task Json_ld_carries_the_csp_nonce_when_there_is_one()
    {
        var rig = new GateRig(embed: "iframe", nonce: "abc123==");
        rig.Clip(42, null);

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.Contains("<script type=\"application/ld+json\" nonce=\"abc123==\">", html);
    }

    [Fact]
    public async Task A_hostile_title_cannot_break_out_of_the_json_ld_script()
    {
        var rig = new GateRig(embed: "iframe");
        rig.Client.Json("/sapi/mediaclip/42", """{"id":"42","title":"</script><script>alert(1)</script><!-- ' & \"","description":"</SCRIPT >x","status":"published"}""");

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));
        string block = Regex.Match(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;

        Assert.DoesNotContain("<", block);
        Assert.DoesNotContain(">", block);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("</script><script>alert(1)</script><!-- ' & \"", (string)JsonNode.Parse(block)!["name"]!);
    }

    [Fact]
    public async Task Nothing_is_emitted_when_the_clip_data_cannot_be_encoded()
    {
        var rig = new GateRig(embed: "iframe");
        // A body parsed from JSON cannot hold a lone surrogate, but a value that was built in code can: the encoder must still refuse it.
        var clip = new JsonObject { ["id"] = "42", ["title"] = "Film", ["description"] = "lone \ud800 surrogate", ["status"] = "published" };
        rig.Client.Responses["/sapi/mediaclip/42"] = SapiResult.Success(200, clip, "{}");

        Assert.DoesNotContain("ld+json", await rig.RenderAsync(GateRig.ClipVideo(42)));
    }

    [Fact]
    public async Task A_denied_visitor_gets_no_json_ld_of_a_protected_clip()
    {
        GateRig rig = Enabled(embed: "iframe");
        rig.Clip(42, Cpp);

        string html = await rig.RenderAsync(GateRig.ClipVideo(42));

        Assert.DoesNotContain("ld+json", html);
        Assert.DoesNotContain("Film 42", html);
    }
}

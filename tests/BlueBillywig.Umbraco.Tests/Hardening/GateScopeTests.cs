using System.Text;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Tests.Gate;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>Finding 3: a protected clip shows the player only while the gate is off. Finding 8: the gate's reach.</summary>
public class ProtectedClipRenderTests
{
    private const string Cpp = "545179f0-1503-46d6-9cc7-041fab63fc48";
    private const string Extra = ",\"transcript\":\"secret words spoken in the film\",\"chapters\":[{\"time\":3,\"title\":\"Secret chapter\"}],\"thumbnails\":[{\"src\":\"/t/secret-thumb.jpg\",\"main\":true}]";

    private static GateRig Rig(bool gate, string embed = "iframe")
    {
        var rig = new GateRig(embed: embed);
        rig.GateSettings.Enabled = gate;
        rig.Client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        rig.Client.Json("/sapi/embedcode/42/default/" + embed, """{"body":"<iframe src=\"https://demo.bbvms.com/p/default/c/42.html\"></iframe>"}""");
        return rig;
    }

    private static BlueBillywigVideo Video(bool lazy = true) =>
        new() { Type = BlueBillywigVideoType.Clip, Id = 42, LazyLoad = lazy, ShowTranscript = true, ShowChapters = true };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_the_gate_off_a_protected_clip_renders_the_player_and_nothing_about_the_clip(bool lazy)
    {
        GateRig rig = Rig(gate: false);
        rig.Clip(42, Cpp, Extra);

        string html = await rig.RenderAsync(Video(lazy));
        string everything = GateRig.Unwrapped(html);

        Assert.Contains("demo.bbvms.com/p/default/c/42.html", everything);
        Assert.DoesNotContain("application/ld+json", everything);
        Assert.DoesNotContain("secret", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Film 42", everything);
        Assert.DoesNotContain("bb-transcript", everything);
        Assert.DoesNotContain("bb-chapters", everything);
        Assert.DoesNotContain("cdn.example", everything);
        Assert.DoesNotContain("background-image", everything);
    }

    [Fact]
    public async Task With_the_gate_off_an_unprotected_clip_keeps_its_json_ld_transcript_chapters_and_poster()
    {
        GateRig rig = Rig(gate: false);
        rig.Clip(42, null, Extra);

        string html = await rig.RenderAsync(Video());

        Assert.Contains("application/ld+json", html);
        Assert.Contains("secret words spoken in the film", html);
        Assert.Contains("Secret chapter", html);
        Assert.Contains("cdn.example/t/secret-thumb.jpg", html);
        Assert.Contains("Play: Film 42", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    public async Task An_empty_policy_value_counts_as_no_policy(string cpp)
    {
        GateRig rig = Rig(gate: false);
        rig.Client.Json("/sapi/mediaclip/42", $$"""{"id":"42","title":"Film 42","status":"published","cpp":"{{cpp}}","transcript":"public words"}""");

        string html = await rig.RenderAsync(Video());

        Assert.Contains("public words", html);
    }

    [Fact]
    public async Task With_the_gate_on_an_allowed_member_still_gets_the_transcript_the_gate_covers_it()
    {
        GateRig rig = Rig(gate: true);
        rig.Clip(42, Cpp, Extra);
        rig.Viewers.Viewer = _ => FakeViewers.Member("member-1");

        string html = await rig.RenderAsync(Video(lazy: false));

        Assert.Contains("secret words spoken in the film", html);
        Assert.Contains("jwt=", html);
    }
}

public class GateScopeTests
{
    private const string Cpp = "545179f0-1503-46d6-9cc7-041fab63fc48";

    private static GateRig Enabled()
    {
        var rig = new GateRig();
        rig.GateSettings.Enabled = true;
        rig.Viewers.Viewer = _ => FakeViewers.Member("member-1");
        return rig;
    }

    [Fact]
    public async Task An_embed_of_another_publication_is_never_looked_up_and_never_gets_a_token()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp); // the configured publication's clip 42 is protected; the embed below is another publication's clip 42

        string html = await rig.RenderAsync(new BlueBillywigVideo { Type = BlueBillywigVideoType.Clip, Id = 42, Publication = "other-pub", LazyLoad = false });

        Assert.Contains("https://other-pub.bbvms.com/p/default/c/42.js", html);
        Assert.DoesNotContain("jwt=", html);
        Assert.DoesNotContain("bb-content-gate", html);
        Assert.DoesNotContain(rig.Client.Requests, r => r.StartsWith("/sapi/mediaclip/42"));
        Assert.False(rig.Http.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task A_channel_of_another_publication_is_not_looked_up_either()
    {
        GateRig rig = Enabled();
        rig.Channel(7, Cpp);

        string html = await rig.RenderAsync(new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 7, Publication = "other-pub" });

        Assert.Contains("other-pub.bbvms.com/ch/7.js", html);
        Assert.DoesNotContain("jwt=", html);
        Assert.DoesNotContain(rig.Client.Requests, r => r.StartsWith("/sapi/channel"));
    }

    [Fact]
    public async Task A_token_is_appended_only_to_urls_on_the_configured_publications_own_host()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Client.Json("/sapi/embedcode/42/default/javascript",
            """{"body":"<script src=\"https://demo.bbvms.com/p/default/c/42.js\"></script><script src=\"https://other-tenant.bbvms.com/p/default/c/42.js\"></script><script src=\"https://demo-extra.bbvms.com/p/default/c/42.js\"></script><iframe src=\"https://www.bluebillywig.com/p/default/c/42.html\"></iframe>"}""");

        string html = await rig.RenderAsync(GateRig.ClipVideo(42, lazy: false));

        Assert.Contains("https://demo.bbvms.com/p/default/c/42.js?jwt=", html);
        Assert.Contains("\"https://other-tenant.bbvms.com/p/default/c/42.js\"", html);
        Assert.Contains("\"https://www.bluebillywig.com/p/default/c/42.html\"", html);
        Assert.Contains("\"https://demo-extra.bbvms.com/p/default/c/42.js\"", html);
        Assert.Single(Regex.Matches(html, "jwt="));
    }

    [Fact]
    public async Task A_development_ovp_host_that_names_the_publication_gets_the_token_too()
    {
        // The development publication's embed code points at {publication}.dev.ovp.bluebillywig.eu, not at its player base.
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Client.Json("/sapi/embedcode/42/default/javascript",
            """{"body":"<script src=\"https://demo.dev.ovp.bluebillywig.eu/p/default/c/42.js\"></script><script src=\"https://other.dev.ovp.bluebillywig.eu/p/default/c/42.js\"></script>"}""");

        string html = await rig.RenderAsync(GateRig.ClipVideo(42, lazy: false));

        Assert.Contains("demo.dev.ovp.bluebillywig.eu/p/default/c/42.js?jwt=", html);
        Assert.Contains("\"https://other.dev.ovp.bluebillywig.eu/p/default/c/42.js\"", html);
    }

    [Fact]
    public async Task The_publication_host_reported_by_sapi_also_counts_as_the_publications_own()
    {
        GateRig rig = Enabled();
        rig.Clip(42, Cpp);
        rig.Client.Json("/sapi/publication", """{"baseurl":"https://player.demo-tenant.bbvms.com"}""");
        rig.Client.Json("/sapi/embedcode/42/default/javascript",
            """{"body":"<script src=\"https://player.demo-tenant.bbvms.com/p/default/c/42.js\"></script>"}""");

        string html = await rig.RenderAsync(GateRig.ClipVideo(42, lazy: false));

        Assert.Contains("player.demo-tenant.bbvms.com/p/default/c/42.js?jwt=", html);
    }
}

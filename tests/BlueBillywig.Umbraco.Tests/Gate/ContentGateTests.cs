using BlueBillywig.Umbraco.Configuration;
using System.Text;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Sapi;

namespace BlueBillywig.Umbraco.Tests.Gate;

/// <summary>G1, G3, G5, G6. Ports ContentGateFailsClosedTest and GateMessagePrivateTest, then covers the whole decision.</summary>
public class ContentGateTests
{
    private static readonly GateEntity Clip42 = GateEntity.Clip(42);

    // ContentGateFailsClosedTest ----------------------------------------------------------------------------

    [Fact]
    public async Task Fails_closed_when_sapi_is_disconnected()
    {
        var client = new FakeClient { IsConnected = false };
        var health = new RecordingHealth();
        var log = new ListLogger();
        var viewers = new FakeViewers { Viewer = _ => FakeViewers.Member() };
        var gate = new ContentGate(new FakeGateSettings(), new FakeGateSecrets(), viewers, new SapiCppLookup(client, new FakeTime(), log), health, GateInjector.IsBbvmsHost, new FakeTime(), log);

        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);

        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.DoesNotContain("pub.bbvms.com", result.Html);
        Assert.Equal(["sapi_disconnected"], health.Errors);
        Assert.Equal("sapi_disconnected", result.Reason);
        Assert.Empty(client.Requests);
        Assert.True(log.Logged("not connected"));
    }

    [Fact]
    public async Task Fails_closed_when_the_policy_is_known_but_the_secret_is_missing()
    {
        ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out FakeViewers viewers, out _, out RecordingHealth health, out ListLogger log);
        secrets.SharedSecret = "";
        viewers.Viewer = _ => FakeViewers.Member();

        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);

        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.DoesNotContain("bbvms", result.Html);
        Assert.Equal(["secret_missing"], health.Errors);
        Assert.True(log.Logged("no shared secret"));
        Assert.DoesNotContain(secrets.Salt!, log.Entries.Select(e => e.Message).Aggregate("", (a, b) => a + b));
    }

    [Fact]
    public async Task A_pinned_secret_that_fails_validation_closes_the_gate_with_secret_missing()
    {
        ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out FakeViewers viewers, out _, out RecordingHealth health, out _);
        var store = new GateSettingsStore(new LockedKeyValues(), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions { GateSharedSecret = "pinned-but-only-26-chars" }), new ProcessWriteLock());
        secrets.SharedSecret = store.GetSharedSecret(); // what the real store hands the gate
        viewers.Viewer = _ => FakeViewers.Member();

        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);

        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.Equal(["secret_missing"], health.Errors);
    }

    [Fact]
    public async Task Passes_through_when_the_entity_has_no_policy_and_does_not_pollute_the_tally()
    {
        ContentGate gate = Gates.Create(out _, out _, out _, out FakeCpp cpp, out RecordingHealth health, out _);
        cpp.Status = CppStatus.NoCpp;

        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);

        Assert.Equal(GateOutcome.PassThrough, result.Outcome);
        Assert.Equal(Gates.ClipEmbed, result.Html);
        Assert.False(result.MustBePrivate);
        Assert.Empty(health.Errors);
    }

    // Every SAPI failure shape closes ---------------------------------------------------------------------

    [Theory]
    [InlineData(500, "sapi_error")]
    [InlineData(502, "sapi_error")]
    [InlineData(401, "sapi_error")]
    [InlineData(0, "sapi_error")]
    [InlineData(404, "clip_not_found")]
    public async Task Sapi_failures_give_the_gate_message_and_the_matching_reason(int status, string reason)
    {
        var client = new FakeClient();
        client.Responses["/sapi/mediaclip/42"] = SapiResult.Failure(status, "nope");
        (GateResult result, RecordingHealth health) = await Gated(client, GateEntity.Clip(42));
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.DoesNotContain("bbvms", result.Html);
        Assert.Equal([reason], health.Errors);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"id":0}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":null,"cpp":"abc"}""")]
    public async Task An_answer_without_an_id_is_not_found_not_unprotected(string body)
    {
        var client = new FakeClient();
        client.Json("/sapi/mediaclip/42", body);
        (GateResult result, RecordingHealth health) = await Gated(client, GateEntity.Clip(42));
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Equal(["clip_not_found"], health.Errors);
    }

    [Fact]
    public async Task An_unreadable_body_closes_as_a_sapi_error()
    {
        var client = new FakeClient();
        client.Raw("/sapi/mediaclip/42", "<html>gateway</html>");
        (GateResult result, RecordingHealth health) = await Gated(client, GateEntity.Clip(42));
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Equal(["sapi_error"], health.Errors);
    }

    [Fact]
    public async Task A_missing_channel_reports_channel_not_found_and_reads_the_channel_path()
    {
        var client = new FakeClient();
        (GateResult result, RecordingHealth health) = await Gated(client, GateEntity.ChannelOf(7));
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Equal(["channel_not_found"], health.Errors);
        Assert.Equal(["/sapi/channel/7"], client.Requests);
    }

    [Theory]
    [InlineData("""{"id":42}""")]
    [InlineData("""{"id":42,"cpp":""}""")]
    [InlineData("""{"id":42,"cpp":null}""")]
    [InlineData("""{"id":42,"cpp":[]}""")]
    [InlineData("""{"id":42,"cpp":0}""")]
    [InlineData("""{"id":42,"cpp":false}""")]
    public async Task Only_an_answer_that_says_no_policy_passes_the_embed_through(string body)
    {
        var client = new FakeClient();
        client.Json("/sapi/mediaclip/42", body);
        (GateResult result, RecordingHealth health) = await Gated(client, GateEntity.Clip(42));
        Assert.Equal(GateOutcome.PassThrough, result.Outcome);
        Assert.Empty(health.Errors);
    }

    private static async Task<(GateResult Result, RecordingHealth Health)> Gated(FakeClient client, GateEntity entity)
    {
        var health = new RecordingHealth();
        var viewers = new FakeViewers { Viewer = _ => FakeViewers.Member() };
        var gate = new ContentGate(new FakeGateSettings(), new FakeGateSecrets(), viewers, new SapiCppLookup(client, new FakeTime()), health, GateInjector.IsBbvmsHost, new FakeTime());
        return (await gate.ApplyAsync(entity, Gates.ClipEmbed), health);
    }

    // The positive path ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_allowed_member_gets_a_signed_token_for_exactly_this_clip()
    {
        var time = new FakeTime();
        ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out FakeViewers viewers, out _, out RecordingHealth health, out _, time);
        viewers.Viewer = _ => FakeViewers.Member("member-guid-1");

        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);

        Assert.Equal(GateOutcome.TokenInjected, result.Outcome);
        Assert.Empty(health.Errors);
        string token = Gates.TokenIn(result.Html);
        Assert.True(GateJwt.TryVerify(token, secrets.SharedSecret!, time.GetUtcNow(), out JsonObject? claims));

        long now = time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal(now, (long)claims!["iat"]!);
        Assert.Equal(now + 3600, (long)claims["exp"]!);
        Assert.Equal("{\"mediaclip\":[\"42\"]}", claims["entity_access"]!.ToJsonString());
        string sub = (string)claims["sub"]!;
        Assert.Matches("^u_[0-9a-f]{16}$", sub);
        Assert.Equal(GateSubject.For("member-guid-1", secrets.Salt!, secrets.Pepper!), sub);
        Assert.DoesNotContain("member-guid-1", token);
        Assert.False(GateJwt.TryVerify(token, secrets.SharedSecret + "x", time.GetUtcNow(), out _));
        Assert.True(secrets.Pepper!.Length >= 32, "the pepper is created and stored on first use");
    }

    [Fact]
    public async Task The_pepper_changes_the_sub_in_the_issued_token()
    {
        async Task<string> SubWith(string pepper)
        {
            ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out FakeViewers viewers, out _, out _, out _);
            secrets.Pepper = pepper;
            viewers.Viewer = _ => FakeViewers.Member("member-guid-1");
            GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
            return (string)Gates.Claims(Gates.TokenIn(result.Html))["sub"]!;
        }

        string a = await SubWith(new string('a', 40));
        string b = await SubWith(new string('b', 40));
        Assert.NotEqual(a, b);
        Assert.Equal(a, await SubWith(new string('a', 40)));
    }

    [Fact]
    public async Task A_channel_gets_a_channel_claim()
    {
        ContentGate gate = Gates.Create(out _, out _, out FakeViewers viewers, out _, out _, out _);
        viewers.Viewer = _ => FakeViewers.Member();
        GateResult result = await gate.ApplyAsync(GateEntity.ChannelOf(9), "<iframe src=\"https://pub.bbvms.com/ch/9.html\"></iframe>");
        Assert.Equal(GateOutcome.TokenInjected, result.Outcome);
        Assert.Equal("{\"channel\":[\"9\"]}", Gates.Claims(Gates.TokenIn(result.Html))["entity_access"]!.ToJsonString());
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(-100, 300)]
    [InlineData(299, 300)]
    [InlineData(300, 300)]
    [InlineData(7200, 7200)]
    [InlineData(7201, 7200)]
    [InlineData(31_536_000, 7200)]
    public async Task The_issued_token_lives_300_to_7200_seconds_whatever_is_configured(long configured, long lifetime)
    {
        ContentGate gate = Gates.Create(out FakeGateSettings settings, out _, out FakeViewers viewers, out _, out _, out _);
        settings.ExpirySeconds = configured;
        viewers.Viewer = _ => FakeViewers.Member();
        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
        JsonObject claims = Gates.Claims(Gates.TokenIn(result.Html));
        Assert.Equal(lifetime, (long)claims["exp"]! - (long)claims["iat"]!);
    }

    // Denials ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_viewers_see_the_login_gate_and_no_secret_is_read()
    {
        ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out _, out _, out RecordingHealth health, out _);
        secrets.SharedSecret = null; // would be a closed-on-error if the denial did not come first

        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);

        Assert.Equal(GateOutcome.Denied, result.Outcome);
        Assert.Contains("Please log in", result.Html);
        Assert.Contains("href=\"/login\"", result.Html);
        Assert.DoesNotContain("bbvms", result.Html);
        Assert.Empty(health.Errors);
        Assert.Null(secrets.Pepper);
    }

    [Fact]
    public async Task Members_without_access_see_the_no_permission_gate()
    {
        ContentGate gate = Gates.Create(out _, out _, out FakeViewers viewers, out _, out RecordingHealth health, out _);
        viewers.Viewer = _ => FakeViewers.Member(allowed: false);
        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
        Assert.Equal(GateOutcome.Denied, result.Outcome);
        Assert.Contains("does not have permission", result.Html);
        Assert.DoesNotContain("Log in", result.Html);
        Assert.Empty(health.Errors);
    }

    [Fact]
    public async Task An_unauthenticated_viewer_is_denied_even_if_the_provider_says_allowed()
    {
        ContentGate gate = Gates.Create(out _, out _, out FakeViewers viewers, out _, out _, out _);
        viewers.Viewer = _ => new GateViewer(false, null, true);
        Assert.Equal(GateOutcome.Denied, (await gate.ApplyAsync(Clip42, Gates.ClipEmbed)).Outcome);
    }

    [Fact]
    public async Task Disabled_means_nothing_is_gated_and_nothing_is_asked()
    {
        ContentGate gate = Gates.Create(out FakeGateSettings settings, out _, out _, out FakeCpp cpp, out RecordingHealth health, out _);
        settings.Enabled = false;
        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
        Assert.Equal(GateOutcome.PassThrough, result.Outcome);
        Assert.Empty(cpp.Asked);
        Assert.Empty(health.Errors);
    }

    // GateMessagePrivateTest: every exit but the pass-through is private -------------------------------------------

    [Fact]
    public async Task Every_outcome_except_pass_through_must_be_private()
    {
        foreach ((CppStatus status, GateViewer viewer, string? secret, string? salt, GateOutcome expected) in new (CppStatus, GateViewer, string?, string?, GateOutcome)[]
        {
            (CppStatus.NoCpp, FakeViewers.Member(), "s", "salt", GateOutcome.PassThrough),
            (CppStatus.HasCpp, FakeViewers.Member(), "s", "salt", GateOutcome.TokenInjected),
            (CppStatus.HasCpp, GateViewer.Anonymous(), "s", "salt", GateOutcome.Denied),
            (CppStatus.HasCpp, FakeViewers.Member(allowed: false), "s", "salt", GateOutcome.Denied),
            (CppStatus.HasCpp, FakeViewers.Member(), "", "salt", GateOutcome.ClosedOnError),
            (CppStatus.HasCpp, FakeViewers.Member(), "s", null, GateOutcome.ClosedOnError),
            (CppStatus.Disconnected, FakeViewers.Member(), "s", "salt", GateOutcome.ClosedOnError),
            (CppStatus.Error, GateViewer.Anonymous(), "s", "salt", GateOutcome.ClosedOnError),
            (CppStatus.NotFound, FakeViewers.Member(), "s", "salt", GateOutcome.ClosedOnError),
        })
        {
            ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out FakeViewers viewers, out FakeCpp cpp, out _, out _);
            cpp.Status = status;
            secrets.SharedSecret = secret;
            secrets.Salt = salt;
            viewers.Viewer = _ => viewer;

            GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
            Assert.Equal(expected, result.Outcome);
            Assert.Equal(expected != GateOutcome.PassThrough, result.MustBePrivate);
        }
    }

    [Fact]
    public async Task Unexpected_failures_close_the_gate_instead_of_throwing()
    {
        ContentGate gate = Gates.Create(out _, out _, out FakeViewers viewers, out _, out RecordingHealth health, out ListLogger log);
        viewers.Viewer = _ => throw new InvalidOperationException("member service down");
        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.True(result.MustBePrivate);
        Assert.Equal(["internal_error"], health.Errors);
        Assert.Contains("bb-content-gate", result.Html);
        Assert.DoesNotContain("bbvms", result.Html);
        Assert.True(log.Logged("member service down") || log.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error));
    }

    [Fact]
    public async Task A_missing_salt_closes_the_gate()
    {
        ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out FakeViewers viewers, out _, out RecordingHealth health, out _);
        secrets.Salt = null;
        viewers.Viewer = _ => FakeViewers.Member();
        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Equal(["salt_missing"], health.Errors);
        Assert.DoesNotContain("jwt=", result.Html);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task An_embed_without_a_valid_id_closes(long id)
    {
        ContentGate gate = Gates.Create(out _, out _, out FakeViewers viewers, out FakeCpp cpp, out RecordingHealth health, out _);
        viewers.Viewer = _ => FakeViewers.Member();
        cpp.Status = CppStatus.NoCpp; // even a lookup that would say "public" is not consulted
        GateResult result = await gate.ApplyAsync(GateEntity.Clip(id), Gates.ClipEmbed);
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Equal(["invalid_entity"], health.Errors);
        Assert.Empty(cpp.Asked);
    }

    [Fact]
    public async Task A_nested_payload_past_the_cap_ends_in_the_gate_message_and_a_tally_entry()
    {
        string html = Gates.ClipEmbed;
        for (int i = 0; i < 4; i++)
        {
            html = $"<div class=\"bb-lazy-embed\" data-bb-embed=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(html))}\"></div>";
        }

        ContentGate gate = Gates.Create(out _, out _, out FakeViewers viewers, out _, out RecordingHealth health, out _);
        viewers.Viewer = _ => FakeViewers.Member();
        GateResult result = await gate.ApplyAsync(Clip42, html);
        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.True(result.MustBePrivate);
        Assert.DoesNotContain("data-bb-embed", result.Html);
        Assert.DoesNotContain("c/42.js", result.Html);
        Assert.Equal(["inject_failed"], health.Errors);
    }

    [Fact]
    public async Task An_embed_with_no_recognisable_player_url_is_not_a_silent_success()
    {
        ContentGate gate = Gates.Create(out _, out _, out FakeViewers viewers, out _, out RecordingHealth health, out ListLogger log);
        viewers.Viewer = _ => FakeViewers.Member();
        GateResult result = await gate.ApplyAsync(Clip42, "<video src=\"https://cdn.example/video.mp4\"></video>");
        Assert.Equal(GateOutcome.TokenInjected, result.Outcome);
        Assert.DoesNotContain("jwt=", result.Html);
        Assert.Equal(["inject_failed"], health.Errors);
        Assert.True(log.Logged("no player URL"));
    }

    [Fact]
    public async Task The_gate_message_for_one_viewer_never_leaks_into_the_next_concurrent_call()
    {
        ContentGate gate = Gates.Create(out _, out FakeGateSecrets secrets, out FakeViewers viewers, out _, out _, out _);
        secrets.SharedSecret = "";
        viewers.Viewer = e => e.Id % 2 == 0 ? FakeViewers.Member() : GateViewer.Anonymous("/login");

        GateResult[] results = await Task.WhenAll(Enumerable.Range(1, 200).Select(i => gate.ApplyAsync(GateEntity.Clip(i), Gates.ClipEmbed)));

        for (int i = 0; i < results.Length; i++)
        {
            bool member = (i + 1) % 2 == 0;
            Assert.Equal(member, results[i].Html.Contains("does not have permission"));
        }
    }

    [Fact]
    public async Task A_custom_message_is_encoded_not_trusted()
    {
        ContentGate gate = Gates.Create(out FakeGateSettings settings, out _, out _, out _, out _, out _);
        settings.CustomMessage = "Members only <script>alert(1)</script> & more\nsecond line";
        GateResult result = await gate.ApplyAsync(Clip42, Gates.ClipEmbed);
        Assert.Equal(GateOutcome.Denied, result.Outcome);
        Assert.DoesNotContain("<script>", result.Html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; more<br />", result.Html);
        Assert.Contains("class=\"bb-content-gate\"", result.Html);
    }
}

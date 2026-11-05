using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Sapi;

namespace BlueBillywig.Umbraco.Tests.Gate;

/// <summary>The five-minute CPP cache (G1, G5), the one-hour error tally (G7) and the gate message.</summary>
public class CppLookupAndHealthTests
{
    private static readonly GateEntity Clip = GateEntity.Clip(42);

    [Fact]
    public async Task A_definite_answer_is_cached_for_five_minutes_and_then_asked_again()
    {
        var time = new FakeTime();
        var client = new FakeClient();
        client.Json("/sapi/mediaclip/42", """{"id":42,"cpp":"abc"}""");
        var lookup = new SapiCppLookup(client, time);

        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(Clip));
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(Clip));
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(Clip));
        Assert.Single(client.Requests);

        time.Advance(TimeSpan.FromSeconds(2));
        client.Json("/sapi/mediaclip/42", """{"id":42}""");
        Assert.Equal(CppStatus.NoCpp, await lookup.GetStatusAsync(Clip));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Invalidate_drops_one_entity_only()
    {
        var client = new FakeClient();
        client.Json("/sapi/mediaclip/42", """{"id":42}""");
        client.Json("/sapi/mediaclip/43", """{"id":43}""");
        var lookup = new SapiCppLookup(client, new FakeTime());
        await lookup.GetStatusAsync(Clip);
        await lookup.GetStatusAsync(GateEntity.Clip(43));

        client.Json("/sapi/mediaclip/42", """{"id":42,"cpp":"abc"}""");
        lookup.Invalidate(Clip);
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(Clip));
        Assert.Equal(CppStatus.NoCpp, await lookup.GetStatusAsync(GateEntity.Clip(43)));
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task Clips_and_channels_with_the_same_id_are_cached_apart()
    {
        var client = new FakeClient();
        client.Json("/sapi/mediaclip/5", """{"id":5,"cpp":"abc"}""");
        client.Json("/sapi/channel/5", """{"id":5}""");
        var lookup = new SapiCppLookup(client, new FakeTime());
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(GateEntity.Clip(5)));
        Assert.Equal(CppStatus.NoCpp, await lookup.GetStatusAsync(GateEntity.ChannelOf(5)));
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(GateEntity.Clip(5)));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Errors_are_never_cached_so_recovery_is_immediate()
    {
        var client = new FakeClient();
        client.Responses["/sapi/mediaclip/42"] = SapiResult.Failure(503, "down");
        var lookup = new SapiCppLookup(client, new FakeTime());
        Assert.Equal(CppStatus.Error, await lookup.GetStatusAsync(Clip));
        client.Json("/sapi/mediaclip/42", """{"id":42,"cpp":"abc"}""");
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(Clip));

        client.Responses.Remove("/sapi/mediaclip/42");
        lookup.Invalidate(Clip);
        Assert.Equal(CppStatus.NotFound, await lookup.GetStatusAsync(Clip));
        client.IsConnected = false;
        Assert.Equal(CppStatus.Disconnected, await lookup.GetStatusAsync(Clip));
    }

    [Fact]
    public async Task A_cached_answer_is_still_served_if_sapi_goes_away_within_the_window()
    {
        // The plugin's behaviour: the transient wins. A positive CPP answer stays protective; a "no" can lag a policy change by up
        // to five minutes, which is why the wiring must call Invalidate on every policy write.
        var client = new FakeClient();
        client.Json("/sapi/mediaclip/42", """{"id":42,"cpp":"abc"}""");
        var lookup = new SapiCppLookup(client, new FakeTime());
        await lookup.GetStatusAsync(Clip);
        client.IsConnected = false;
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(Clip));
    }

    [Fact]
    public async Task An_invalid_id_is_not_found_without_a_request()
    {
        var client = new FakeClient();
        var lookup = new SapiCppLookup(client, new FakeTime());
        Assert.Equal(CppStatus.NotFound, await lookup.GetStatusAsync(GateEntity.Clip(0)));
        Assert.Empty(client.Requests);
    }

    // Health tally ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_tally_counts_per_reason_and_clears_an_hour_after_the_last_error()
    {
        var time = new FakeTime();
        var tally = new GateHealthTally(time);
        Assert.True(tally.Snapshot().IsHealthy);

        tally.RecordGateError("sapi_error");
        tally.RecordGateError("sapi_error");
        tally.RecordGateError("secret_missing");
        GateHealthSnapshot snapshot = tally.Snapshot();
        Assert.Equal(2, snapshot.ErrorCounts["sapi_error"]);
        Assert.Equal(1, snapshot.ErrorCounts["secret_missing"]);
        Assert.False(snapshot.IsHealthy);

        // Every error refreshes the window, like the plugin's transient.
        time.Advance(TimeSpan.FromMinutes(59));
        tally.RecordGateError("sapi_error");
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(3, tally.Snapshot().ErrorCounts["sapi_error"]);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(tally.Snapshot().ErrorCounts);
        Assert.True(tally.Snapshot().IsHealthy);
    }

    [Fact]
    public void A_new_error_after_expiry_starts_a_fresh_tally()
    {
        var time = new FakeTime();
        var tally = new GateHealthTally(time);
        tally.RecordGateError("sapi_error");
        time.Advance(TimeSpan.FromHours(2));
        tally.RecordGateError("clip_not_found");
        Assert.Equal(["clip_not_found"], tally.Snapshot().ErrorCounts.Keys);
    }

    [Fact]
    public void The_pepper_failure_flag_expires_on_its_own_hour_and_is_separate()
    {
        var time = new FakeTime();
        var tally = new GateHealthTally(time);
        tally.RecordPepperPersistFailure();
        Assert.NotNull(tally.Snapshot().PepperFailedAt);
        Assert.Empty(tally.Snapshot().ErrorCounts);
        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Null(tally.Snapshot().PepperFailedAt);
    }

    [Fact]
    public async Task The_tally_is_thread_safe()
    {
        var tally = new GateHealthTally(new FakeTime());
        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                tally.RecordGateError(i % 2 == 0 ? "a" : "b");
                _ = tally.Snapshot().IsHealthy;
            }
        })));
        GateHealthSnapshot snapshot = tally.Snapshot();
        Assert.Equal(8000, snapshot.ErrorCounts["a"]);
        Assert.Equal(8000, snapshot.ErrorCounts["b"]);
    }

    [Fact]
    public async Task The_cache_is_safe_under_parallel_use()
    {
        var client = new FakeClient();
        for (int i = 1; i <= 50; i++)
        {
            client.Json($"/sapi/mediaclip/{i}", $$"""{"id":{{i}},"cpp":"abc"}""");
        }

        var lookup = new SapiCppLookup(client, new FakeTime());
        CppStatus[] results = await Task.WhenAll(Enumerable.Range(0, 500).Select(i => lookup.GetStatusAsync(GateEntity.Clip(i % 50 + 1))));
        Assert.All(results, r => Assert.Equal(CppStatus.HasCpp, r));
    }

    // Gate message -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/login?returnUrl=%2Fvideo", true)]
    [InlineData("https://site.example/login", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("//evil.example/login", false)]
    [InlineData("/login\" onclick=\"x", false)]
    [InlineData("/lo gin", false)]
    [InlineData("\\\\evil\\login", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_login_link_is_only_a_local_path_or_an_http_url(string? url, bool shown)
    {
        string html = GateMessage.Render(false, url, null);
        Assert.Equal(shown, html.Contains("<a href=", StringComparison.Ordinal));
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("bb-content-gate", html);
    }

    [Fact]
    public void The_login_link_is_attribute_encoded()
    {
        string html = GateMessage.Render(false, "/login?a=1&b=<x>", null);
        Assert.Contains("href=\"/login?a=1&amp;b=&lt;x&gt;\"", html);
    }

    [Fact]
    public void An_authenticated_viewer_gets_no_login_link()
    {
        string html = GateMessage.Render(true, "/login", null);
        Assert.DoesNotContain("<a ", html);
        Assert.Contains("does not have permission", html);
    }

    [Fact]
    public void A_custom_message_still_gets_the_login_link_for_an_anonymous_visitor()
    {
        string html = GateMessage.Render(false, "/login?returnUrl=%2Fvideo&x=<y>", "Members only <b>please</b>\nsecond line");

        Assert.Contains("Members only &lt;b&gt;please&lt;/b&gt;<br />\nsecond line", html);
        Assert.Contains("<a href=\"/login?returnUrl=%2Fvideo&amp;x=&lt;y&gt;\">Log in</a>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.StartsWith("<div class=\"bb-content-gate\" role=\"alert\">", html);
        Assert.EndsWith("</a></p></div>", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/login")]
    [InlineData("/lo gin")]
    [InlineData("")]
    [InlineData(null)]
    public void A_custom_message_gets_no_link_when_the_login_url_does_not_validate(string? url)
    {
        string html = GateMessage.Render(false, url, "Members only");

        Assert.Equal("<div class=\"bb-content-gate\" role=\"alert\">Members only</div>", html);
    }

    [Fact]
    public void A_custom_message_for_a_signed_in_viewer_has_no_login_link()
    {
        Assert.DoesNotContain("<a ", GateMessage.Render(true, "/login", "Members only"));
    }
}

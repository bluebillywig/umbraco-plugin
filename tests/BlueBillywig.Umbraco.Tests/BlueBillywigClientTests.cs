using System.Net;
using System.Security.Cryptography;
using System.Text;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// The client is the one door to SAPI, so its promises get their own tests, run against a fake HTTP handler:
/// cached reads, errors never cached, never throwing at callers, a wrong key being a failure, and the read-only
/// token never being built from the read-write secret.
/// </summary>
public class BlueBillywigClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("""{"ok":true}""");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (BlueBillywigClient Client, Handler Http, FakeSettings Settings) Rig(string secret = "secret")
    {
        var http = new Handler();
        var settings = new FakeSettings { Value = new BlueBillywigSettings { Publication = "demo", ApiId = "7", ApiSecret = secret } };
        var client = new BlueBillywigClient(settings, new MemoryCache(new MemoryCacheOptions()), NullLogger<BlueBillywigClient>.Instance, new HttpClient(http));
        return (client, http, settings);
    }

    /// <summary>The plugin's and SDK's token: HMAC-SHA1 of the 120 second counter, keyed with the secret text, prefixed with the key id.</summary>
    private static string Token(int id, string secret, long unixSeconds)
    {
        byte[] counter = BitConverter.GetBytes(unixSeconds / 120);
        Array.Reverse(counter);
        return id + "-" + Convert.ToHexString(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), counter)).ToLowerInvariant();
    }

    private static string[] Tokens(int id, string secret)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return [Token(id, secret, now - 120), Token(id, secret, now), Token(id, secret, now + 120)]; // a window boundary may pass during the test
    }

    [Fact]
    public async Task A_cached_read_is_fetched_once_until_the_cache_is_cleared()
    {
        (BlueBillywigClient client, Handler http, _) = Rig();

        SapiResult first = await client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromMinutes(5));
        SapiResult second = await client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromMinutes(5));

        Assert.True(first.Ok);
        Assert.Same(first, second);
        Assert.Single(http.Requests);

        client.ClearCache();
        await client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromMinutes(5));
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task A_changed_publication_or_origin_is_never_served_the_old_publications_cached_answer()
    {
        (BlueBillywigClient client, Handler http, FakeSettings settings) = Rig();
        int n = 0;
        http.Respond = _ => Json($$"""{"call":{{++n}}}""");
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        SapiResult first = await client.GetAsync("/sapi/mediaclip/1", cacheFor: ttl);
        SapiResult again = await client.GetAsync("/sapi/mediaclip/1", cacheFor: ttl);
        Assert.Same(first, again);

        // FakeSettings raises no change event, so only the key can keep the entries apart (a node that has not yet seen the
        // settings change, or a configuration reload).
        settings.Value = settings.Value with { Publication = "other" };
        SapiResult other = await client.GetAsync("/sapi/mediaclip/1", cacheFor: ttl);
        Assert.NotSame(first, other);
        Assert.Equal(2, http.Requests.Count);

        settings.Value = settings.Value with { BaseUrl = "https://other.dev.bbovp.eu" };
        SapiResult moved = await client.GetAsync("/sapi/mediaclip/1", cacheFor: ttl);
        Assert.NotSame(other, moved);
        Assert.Equal(3, http.Requests.Count);

        // Back to the first publication: its own entry is still there.
        settings.Value = settings.Value with { Publication = "demo", BaseUrl = null };
        Assert.Same(first, await client.GetAsync("/sapi/mediaclip/1", cacheFor: ttl));
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task The_cache_key_includes_the_query_and_uncached_reads_stay_uncached()
    {
        (BlueBillywigClient client, Handler http, _) = Rig();
        var a = new Dictionary<string, string> { ["q"] = "a" };
        var b = new Dictionary<string, string> { ["q"] = "b" };

        await client.GetAsync("/sapi/mediaclip", a, TimeSpan.FromMinutes(1));
        await client.GetAsync("/sapi/mediaclip", b, TimeSpan.FromMinutes(1));
        await client.GetAsync("/sapi/mediaclip", a, TimeSpan.FromMinutes(1));
        Assert.Equal(2, http.Requests.Count);

        await client.GetAsync("/sapi/mediaclip/5");
        await client.GetAsync("/sapi/mediaclip/5");
        Assert.Equal(4, http.Requests.Count); // a read without cacheFor is always live (the editor reads a clip fresh before a write)
    }

    [Fact]
    public async Task Errors_are_never_cached()
    {
        (BlueBillywigClient client, Handler http, _) = Rig();
        http.Respond = _ => Json("""{"message":"internal path /srv/secret"}""", HttpStatusCode.InternalServerError);

        SapiResult failed = await client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromHours(1));
        Assert.False(failed.Ok);
        Assert.Equal(500, failed.StatusCode);

        http.Respond = _ => Json("""{"ok":true}""");
        SapiResult recovered = await client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromHours(1));
        Assert.True(recovered.Ok); // the outage clears as soon as SAPI recovers
        Assert.Equal(2, http.Requests.Count);

        await client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromHours(1));
        Assert.Equal(2, http.Requests.Count); // and the good answer is cached
    }

    [Fact]
    public async Task An_upstream_error_body_is_never_returned_to_the_caller()
    {
        (BlueBillywigClient client, Handler http, _) = Rig();
        http.Respond = _ => Json("""{"message":"stack trace at /srv/app/secret.php line 3"}""", HttpStatusCode.BadRequest);

        SapiResult result = await client.PutAsync("/sapi/mediaclip/5", new { id = "5" });

        Assert.False(result.Ok);
        Assert.Equal("SAPI request failed (400)", result.Error);
        Assert.Null(result.RawBody);
        Assert.Null(result.Json);
    }

    [Fact]
    public async Task A_wrong_key_gives_a_failure_and_the_right_key_gives_data()
    {
        string[] valid = Tokens(7, "right");
        var http = new Handler { Respond = r => valid.Contains(r.Headers.GetValues("rpctoken").Single()) ? Json("""{"name":"demo"}""") : Json("""{"error":"bad token for key 7"}""", HttpStatusCode.Unauthorized) };
        FakeSettings Settings(string secret) => new() { Value = new BlueBillywigSettings { Publication = "demo", ApiId = "7", ApiSecret = secret } };
        BlueBillywigClient Client(FakeSettings s) => new(s, new MemoryCache(new MemoryCacheOptions()), NullLogger<BlueBillywigClient>.Instance, new HttpClient(http));

        SapiResult wrong = await Client(Settings("wrong")).TestConnectionAsync();
        SapiResult right = await Client(Settings("right")).TestConnectionAsync();

        Assert.False(wrong.Ok);
        Assert.Equal(401, wrong.StatusCode);
        Assert.Equal("SAPI request failed (401)", wrong.Error);
        Assert.True(right.Ok);
        Assert.Equal("/sapi/publication", http.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task The_client_never_throws_to_callers_for_network_failures_or_missing_settings()
    {
        (BlueBillywigClient client, Handler http, FakeSettings settings) = Rig();

        http.Respond = _ => throw new HttpRequestException("connection refused");
        SapiResult unreachable = await client.GetAsync("/sapi/publication");
        Assert.False(unreachable.Ok);
        Assert.Equal(0, unreachable.StatusCode);
        Assert.Equal("Could not reach Blue Billywig", unreachable.Error);

        http.Respond = _ => throw new TaskCanceledException("timeout");
        SapiResult timeout = await client.PutAsync("/sapi/mediaclip/5", new { id = "5" });
        Assert.False(timeout.Ok);

        settings.Value = new BlueBillywigSettings();
        Assert.False(client.IsConnected);
        foreach (SapiResult result in new[]
        {
            await client.GetAsync("/sapi/publication"),
            await client.PostAsync("/sapi/subtitle", new { }),
            await client.PutAsync("/sapi/mediaclip/5", new { }),
            await client.DeleteAsync("/sapi/mediaclip/5"),
            await client.TestConnectionAsync(),
        })
        {
            Assert.False(result.Ok);
            Assert.Equal("Blue Billywig API not configured", result.Error);
        }
    }

    [Fact]
    public async Task Only_cancellation_by_the_caller_is_rethrown()
    {
        (BlueBillywigClient client, Handler http, _) = Rig();
        using var cts = new CancellationTokenSource();
        http.Respond = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/sapi/publication", ct: cts.Token));
    }

    [Fact]
    public async Task Writes_go_out_with_the_verb_and_the_read_write_key_and_are_not_cached()
    {
        (BlueBillywigClient client, Handler http, _) = Rig("rw-secret");

        await client.PutAsync("/sapi/mediaclip/5", new { id = "5", title = "t" });
        await client.DeleteAsync("/sapi/mediaclip/5");

        Assert.Equal([HttpMethod.Put, HttpMethod.Delete], http.Requests.Select(r => r.Method));
        Assert.All(http.Requests, r => Assert.Contains(r.Headers.GetValues("rpctoken").Single(), Tokens(7, "rw-secret")));
    }

    [Fact]
    public void The_read_only_token_is_built_from_the_read_only_pair_and_never_from_the_read_write_secret()
    {
        (BlueBillywigClient client, _, FakeSettings settings) = Rig("rw-secret");
        settings.Value = settings.Value with { ReadOnlyApiId = "9", ReadOnlyApiSecret = "ro-secret" };

        string? token = client.GetReadOnlyToken();

        Assert.NotNull(token);
        Assert.Contains(token, Tokens(9, "ro-secret"));
        Assert.DoesNotContain(token, Tokens(7, "rw-secret"));
        Assert.StartsWith("9-", token);
        Assert.DoesNotContain("rw-secret", token);
        Assert.DoesNotContain("ro-secret", token); // a token, not the secret itself
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("9", "")]
    [InlineData("", "ro-secret")]
    [InlineData("not-a-number", "ro-secret")]
    public void Without_a_complete_read_only_pair_there_is_no_token_and_the_read_write_key_is_not_a_fallback(string id, string secret)
    {
        (BlueBillywigClient client, _, FakeSettings settings) = Rig("rw-secret");
        settings.Value = settings.Value with { ReadOnlyApiId = id, ReadOnlyApiSecret = secret };

        Assert.Null(client.GetReadOnlyToken());
    }
}

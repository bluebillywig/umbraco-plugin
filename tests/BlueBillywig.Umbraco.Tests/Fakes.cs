using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlueBillywig.Umbraco.Tests;

internal sealed class FakeSettings : IBlueBillywigSettingsStore
{
    public BlueBillywigSettings Value { get; set; } = new()
    {
        Publication = "demo",
        ApiId = "1",
        ApiSecret = "secret",
        Playout = "default",
        Embed = "javascript",
    };

    public event Action? Changed { add { } remove { } }

    public BlueBillywigSettings Get() => Value;

    public IReadOnlyCollection<string> ConfigOverrides() => [];

    public void Save(BlueBillywigSettings settings, string? apiSecret, string? readOnlyApiSecret) =>
        Value = settings with
        {
            ApiSecret = apiSecret ?? Value.ApiSecret,
            ReadOnlyApiSecret = readOnlyApiSecret ?? Value.ReadOnlyApiSecret,
        };

    public int Disconnects { get; private set; }

    public void Disconnect()
    {
        Disconnects++;
        Value = Value with { Publication = "", ApiId = "", ApiSecret = "", ReadOnlyApiId = "", ReadOnlyApiSecret = "" };
    }
}

/// <summary>Answers the credential check without a network, and records what it was asked.</summary>
internal sealed class FakeTester : ISapiCredentialTester
{
    public List<BlueBillywigSettings> Asked { get; } = [];

    public CredentialCheck Answer { get; set; } = new(true, "Demo publication", null);

    /// <summary>When set, decides per candidate (for tests where the read-write and read-only pairs differ).</summary>
    public Func<BlueBillywigSettings, CredentialCheck>? Decide { get; set; }

    public Task<CredentialCheck> TestAsync(BlueBillywigSettings candidate, CancellationToken ct = default)
    {
        Asked.Add(candidate);
        return Task.FromResult(Decide?.Invoke(candidate) ?? Answer);
    }
}

internal sealed class FakeClient : IBlueBillywigClient
{
    public Dictionary<string, SapiResult> Responses { get; } = new();

    public List<string> Requests { get; } = [];

    /// <summary>Every call with its verb, for tests that check what was sent and what was not.</summary>
    public List<(string Method, string Path, SapiRequest? Request)> Calls { get; } = [];

    /// <summary>Answers write calls and anything not in <see cref="Responses"/>. Null means "not supported".</summary>
    public Func<string, string, SapiRequest?, SapiResult>? Handler { get; set; }

    public string? ReadOnlyToken { get; set; }

    public bool IsConnected { get; set; } = true;

    public string Publication => "demo";

    public string BaseUrl => "https://demo.bbvms.com";

    public Task<SapiResult> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, TimeSpan? cacheFor = null, CancellationToken ct = default)
    {
        Requests.Add(path);
        Calls.Add(("GET", path, new SapiRequest { Query = query }));
        if (Responses.TryGetValue(path, out SapiResult? r))
        {
            return Task.FromResult(r);
        }

        return Task.FromResult(Handler?.Invoke("GET", path, new SapiRequest { Query = query }) ?? SapiResult.Failure(404, "not found"));
    }

    public Task<SapiResult> PostAsync(string path, object? json = null, CancellationToken ct = default) => Write("POST", path, json);

    public Task<SapiResult> PutAsync(string path, object? json = null, CancellationToken ct = default) => Write("PUT", path, json);

    private Task<SapiResult> Write(string method, string path, object? json)
    {
        var request = new SapiRequest { Json = json };
        Calls.Add((method, path, request));
        return Task.FromResult(Handler?.Invoke(method, path, request) ?? throw new NotSupportedException());
    }

    public Task<SapiResult> DeleteAsync(string path, CancellationToken ct = default)
    {
        Calls.Add(("DELETE", path, null));
        return Task.FromResult(Handler?.Invoke("DELETE", path, null) ?? throw new NotSupportedException());
    }

    public Task<SapiResult> SendAsync(string method, string path, SapiRequest request, CancellationToken ct = default)
    {
        Calls.Add((method, path, request));
        return Task.FromResult(Handler?.Invoke(method, path, request) ?? throw new NotSupportedException());
    }

    public Task<SapiResult> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct = default) => throw new NotSupportedException();

    public string? GetReadOnlyToken() => ReadOnlyToken;

    public Task<SapiResult> TestConnectionAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public void ClearCache() => Cleared++;

    public int Cleared { get; private set; }

    public void Json(string path, string json) => Responses[path] = SapiResult.Success(200, JsonNode.Parse(json), json);

    public void Raw(string path, string body) => Responses[path] = SapiResult.Success(200, null, body);

    public static SapiResult Ok(string json) => SapiResult.Success(200, JsonNode.Parse(json), json);
}

internal sealed class FixedConsent(bool granted) : IBlueBillywigConsentProvider
{
    public bool IsGranted(HttpContext? httpContext) => granted;
}

internal sealed class FixedNonce(string? nonce) : IBlueBillywigCspNonceProvider
{
    public string? GetNonce(HttpContext? httpContext) => nonce;
}

internal sealed class FixedOptions(BlueBillywigOptions value) : IOptionsMonitor<BlueBillywigOptions>
{
    public BlueBillywigOptions CurrentValue => value;

    public BlueBillywigOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<BlueBillywigOptions, string?> listener) => null;
}

internal static class Rig
{
    public static (BlueBillywigEmbedRenderer Renderer, FakeClient Client, FakeSettings Settings) Create(bool consent = true, string? nonce = null, DefaultHttpContext? http = null, BlueBillywigOptions? options = null, IClipPlayoutStore? playouts = null)
    {
        var client = new FakeClient();
        var settings = new FakeSettings();
        var accessor = new HttpContextAccessor { HttpContext = http };
        return (new BlueBillywigEmbedRenderer(settings, client, new FixedConsent(consent), new FixedNonce(nonce), accessor, NullLogger<BlueBillywigEmbedRenderer>.Instance, options: options is null ? null : new FixedOptions(options), playouts: playouts), client, settings);
    }

    public static async Task<string> Render(BlueBillywigEmbedRenderer renderer, BlueBillywigVideo video) =>
        (await renderer.RenderAsync(video)).ToHtmlString();
}

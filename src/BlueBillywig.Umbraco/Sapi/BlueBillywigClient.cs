using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlueBillywig.Sapi;
using BlueBillywig.Sapi.Authentication;
using BlueBillywig.Sapi.Exceptions;
using BlueBillywig.Umbraco.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace BlueBillywig.Umbraco.Sapi;

internal sealed class BlueBillywigClient : IBlueBillywigClient, IDisposable
{
    private readonly IBlueBillywigSettingsStore _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BlueBillywigClient> _logger;
    private readonly object _sdkLock = new();

    private Sdk? _sdk;
    private BlueBillywigSettings? _sdkSettings;
    private CancellationTokenSource _cacheGeneration = new();
    private readonly HttpClient? _http;
    private readonly SapiCacheSignal _signal;
    private readonly bool _ownsSignal;

    /// <summary>
    /// <paramref name="signal"/> is the shared clear signal (settings changes raise it; the gate's CPP lookup listens to it).
    /// Without one, as in tests, the client makes its own and listens to the settings store itself.
    /// </summary>
    public BlueBillywigClient(IBlueBillywigSettingsStore settings, IMemoryCache cache, ILogger<BlueBillywigClient> logger, SapiCacheSignal? signal = null)
    {
        _settings = settings;
        _cache = cache;
        _logger = logger;
        _ownsSignal = signal is null;
        _signal = signal ?? new SapiCacheSignal(settings);
        _signal.Cleared += DropReadCache;
    }

    /// <summary>For tests: the SDK sends its requests through this client instead of opening its own connections.</summary>
    internal BlueBillywigClient(IBlueBillywigSettingsStore settings, IMemoryCache cache, ILogger<BlueBillywigClient> logger, HttpClient http, SapiCacheSignal? signal = null)
        : this(settings, cache, logger, signal) => _http = http;

    public bool IsConnected => _settings.Get().HasCredentials;

    public string Publication => _settings.Get().Publication;

    public string BaseUrl => _settings.Get().ResolveBaseUrl();

    public async Task<SapiResult> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, TimeSpan? cacheFor = null, CancellationToken ct = default)
    {
        if (cacheFor is null)
        {
            return await SendCoreAsync("GET", path, new RequestOptions { Query = query }, ct);
        }

        BlueBillywigSettings current = _settings.Get();
        string key = CacheKey(current.Publication, current.ResolveBaseUrl(), path, query);
        if (_cache.TryGetValue(key, out SapiResult? cached) && cached is not null)
        {
            return cached;
        }

        SapiResult result = await SendCoreAsync("GET", path, new RequestOptions { Query = query }, ct);

        // Errors are never cached, so an outage clears as soon as SAPI recovers.
        if (result.Ok)
        {
            var entry = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = cacheFor };
            entry.AddExpirationToken(new CancellationChangeToken(_cacheGeneration.Token));
            _cache.Set(key, result, entry);
        }

        return result;
    }

    public Task<SapiResult> PostAsync(string path, object? json = null, CancellationToken ct = default) =>
        SendCoreAsync("POST", path, new RequestOptions { Json = json }, ct);

    public Task<SapiResult> PutAsync(string path, object? json = null, CancellationToken ct = default) =>
        SendCoreAsync("PUT", path, new RequestOptions { Json = json }, ct);

    public Task<SapiResult> DeleteAsync(string path, CancellationToken ct = default) =>
        SendCoreAsync("DELETE", path, new RequestOptions(), ct);

    public Task<SapiResult> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct = default) =>
        SendCoreAsync(method.Method, path, new RequestOptions { Body = content }, ct);

    public Task<SapiResult> SendAsync(string method, string path, SapiRequest request, CancellationToken ct = default) =>
        SendCoreAsync(method, path, new RequestOptions { Query = request.Query, Json = request.Json, Headers = request.Headers is null ? null : new Dictionary<string, string>(request.Headers) }, ct);

    public string? GetReadOnlyToken()
    {
        BlueBillywigSettings s = _settings.Get();
        if (s.ReadOnlyApiId.Length == 0 || s.ReadOnlyApiSecret.Length == 0 || !int.TryParse(s.ReadOnlyApiId, out int id))
        {
            return null;
        }

        // A "read-only" pair that is the read-write key (same id, or same secret) would hand the read-write token to a
        // browser in a preview URL. Refuse it here, whatever the settings screen let through.
        if (s.ReadOnlyApiId == s.ApiId || s.ReadOnlyApiSecret == s.ApiSecret)
        {
            _logger.LogWarning("The read-only API pair is the read-write pair; no preview token is minted");
            return null;
        }

        return new RpcTokenAuthenticator(id, s.ReadOnlyApiSecret).Authenticate()["rpctoken"];
    }

    public Task<SapiResult> TestConnectionAsync(CancellationToken ct = default) =>
        SendCoreAsync("GET", "/sapi/publication", new RequestOptions(), ct);

    /// <summary>
    /// Drops every cached read, here and (on a load-balanced site) on the other nodes, and with them whatever else listens to
    /// <see cref="ISapiCacheSignal"/>, such as the gate's CPP lookup. See <see cref="SapiCacheSignal"/> for the multi-node limit.
    /// Throws if part of the clear failed, after trying all of it.
    /// </summary>
    public void ClearCache() => _signal.Clear();

    /// <summary>The thumbnail state is deliberately not part of this: see <see cref="Configuration.IBlueBillywigCleanup"/>.</summary>
    private void DropReadCache()
    {
        CancellationTokenSource previous = Interlocked.Exchange(ref _cacheGeneration, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        _signal.Cleared -= DropReadCache;
        if (_ownsSignal)
        {
            _signal.Dispose();
        }

        _sdk?.Dispose();
        _cacheGeneration.Dispose();
    }

    private async Task<SapiResult> SendCoreAsync(string method, string path, RequestOptions options, CancellationToken ct)
    {
        Sdk? sdk = GetSdk();
        if (sdk is null)
        {
            return SapiResult.Failure(0, "Blue Billywig API not configured");
        }

        try
        {
            // SendRequestAsync returns non-2xx responses rather than throwing them.
            SapiResponse response = await sdk.SendRequestAsync(method, path, options, ct);
            if (response.StatusCode is < 200 or >= 300)
            {
                // SAPI error bodies can carry internal paths, so they go to the log and not to the caller.
                _logger.LogWarning("SAPI {Method} {Path} returned {Status}: {Body}", method, path, response.StatusCode, response.Body);
                return SapiResult.Failure(response.StatusCode, $"SAPI request failed ({response.StatusCode})");
            }

            return SapiResult.Success(response.StatusCode, ParseJson(response.Body), response.Body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SapiConnectionException)
        {
            _logger.LogWarning(ex, "SAPI {Method} {Path} could not be reached", method, path);
            return SapiResult.Failure(0, "Could not reach Blue Billywig");
        }
    }

    private Sdk? GetSdk()
    {
        BlueBillywigSettings settings = _settings.Get();
        if (!settings.HasCredentials || !int.TryParse(settings.ApiId, out int tokenId))
        {
            return null;
        }

        lock (_sdkLock)
        {
            if (_sdk is null || _sdkSettings != settings)
            {
                _sdk?.Dispose();
                _sdk = Sdk.WithRpcTokenAuthentication(
                    settings.Publication,
                    tokenId,
                    settings.ApiSecret,
                    new SdkOptions { BaseUri = settings.ResolveBaseUrl(), HttpClient = _http });
                _sdkSettings = settings;
            }

            return _sdk;
        }
    }

    private static JsonNode? ParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // Some endpoints, such as subtitle contents and embed code, return plain text.
            return null;
        }
    }

    /// <summary>
    /// The publication and the SAPI origin are part of the key, so an answer fetched for one publication (or one origin) can
    /// never be served for another, also when a settings change reaches this node late (multi-node) or not through the store's
    /// change event (a configuration reload). The clear on a settings save stays as well. Credentials are not in the key.
    /// </summary>
    private static string CacheKey(string publication, string baseUrl, string path, IReadOnlyDictionary<string, string>? query)
    {
        string prefix = "bb:" + publication + "@" + baseUrl;
        if (query is null || query.Count == 0)
        {
            return prefix + path;
        }

        return prefix + path + "?" + string.Join('&', query.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => $"{q.Key}={q.Value}"));
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>
/// Makes the <see cref="ContentGate"/> for one render. The gate is cheap to build; building it per render lets the
/// "which hosts may receive a token" rule follow the publication in use (a development OVP is not under
/// <c>bbvms.com</c>), and resolves the visitor once per request before the synchronous viewer seam is asked.
/// </summary>
internal sealed class ContentGateFactory
{
    private readonly IGateSettings _settings;
    private readonly IGateSecretStore _secrets;
    private readonly IGateViewerProvider _viewers;
    private readonly ICppLookup _cpp;
    private readonly IGateHealth _health;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ContentGateFactory(
        IGateSettings settings,
        IGateSecretStore secrets,
        IGateViewerProvider viewers,
        ICppLookup cpp,
        IGateHealth health,
        TimeProvider time,
        ILogger<ContentGate>? logger = null)
    {
        _settings = settings;
        _secrets = secrets;
        _viewers = viewers;
        _cpp = cpp;
        _health = health;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public bool Enabled => _settings.Enabled;

    public string? CustomMessage => _settings.CustomMessage;

    public IGateHealth Health => _health;

    public async Task<ContentGate> CreateAsync(Func<string, bool> isGatedHost, CancellationToken ct)
    {
        if (_viewers is IGateViewerPrimer primer && _settings.Enabled)
        {
            await primer.PrimeAsync(ct);
        }

        return new ContentGate(_settings, _secrets, _viewers, _cpp, _health, isGatedHost, _time, _logger);
    }
}

/// <summary>
/// G6: a response that carries a token (its <c>sub</c> names the viewer) or a gate message (it varies with login state)
/// must not be stored by a shared cache (plugin: <c>mark_response_private</c>).
/// </summary>
public static class GateResponse
{
    /// <summary>
    /// Sends <c>Cache-Control: private, no-store</c> and keeps the response out of ASP.NET Core's output cache. Returns false
    /// when that is no longer possible (no request, or the headers have already gone out): the caller must then not emit a
    /// token. <c>Pragma: no-cache</c> covers HTTP/1.0 caches.
    /// </summary>
    public static bool MarkPrivate(HttpContext? http, ILogger? logger = null)
    {
        if (http is null)
        {
            return false;
        }

        if (http.Response.HasStarted)
        {
            logger?.LogError("Blue Billywig ContentGate: could not send Cache-Control: private because the response headers were already sent.");
            return false;
        }

        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.Pragma = "no-cache";

        // Output caching stores a response unless its policy is told not to; the request-level policy cannot see this decision.
        if (http.Features.Get<IOutputCacheFeature>() is { } feature)
        {
            feature.Context.AllowCacheStorage = false;
        }

        return true;
    }
}

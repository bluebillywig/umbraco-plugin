using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Configuration;

/// <summary>
/// Checks a set of credentials against SAPI without storing anything and without touching the live client or its cache.
/// Counterpart of the reference plugin's <c>Filters::test_connection</c>, which builds a throwaway SDK for the typed values.
/// </summary>
public interface ISapiCredentialTester
{
    /// <summary>
    /// Asks SAPI for the publication with exactly these values. Never throws. On success <see cref="CredentialCheck.Publication"/>
    /// is the publication's name; on failure <see cref="CredentialCheck.Error"/> is a short fixed sentence that is safe to show
    /// (never what SAPI answered).
    /// </summary>
    Task<CredentialCheck> TestAsync(BlueBillywigSettings candidate, CancellationToken ct = default);
}

public sealed record CredentialCheck(bool Ok, string? Publication, string? Error)
{
    /// <summary>The reference plugin's hints for 401, 403 and 404, as fixed text.</summary>
    public static CredentialCheck Failed(int status) => new(false, null, status switch
    {
        401 => "Invalid API ID and secret combination (HTTP 401)",
        403 => "The API key has no permission on this publication (HTTP 403)",
        404 => "Publication not found, check the publication name (HTTP 404)",
        0 => "Could not reach Blue Billywig",
        _ => $"Blue Billywig refused the connection test (HTTP {status})",
    });
}

internal sealed class SapiCredentialTester : ISapiCredentialTester
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ILoggerFactory? _logs;
    private readonly HttpClient? _http;

    public SapiCredentialTester(ILoggerFactory? logs = null) => _logs = logs;

    /// <summary>For tests: the SDK sends its requests through this client instead of opening its own connections.</summary>
    internal SapiCredentialTester(ILoggerFactory? logs, HttpClient http)
        : this(logs) => _http = http;

    public async Task<CredentialCheck> TestAsync(BlueBillywigSettings candidate, CancellationToken ct = default)
    {
        if (candidate.BaseUrlRejected)
        {
            return new CredentialCheck(false, null, "The configured BlueBillywig:BaseUrl is not accepted (https, a Blue Billywig domain, no path)");
        }

        if (!candidate.HasCredentials || !int.TryParse(candidate.ApiId, out _))
        {
            return new CredentialCheck(false, null, "Enter the publication, API ID and API secret to test the connection");
        }

        // A client of its own over fixed values: its cache and SDK are thrown away, so the live client, its cache and the
        // stored settings are untouched whatever the answer. The effective base URL is used, or a development
        // publication would be tested against the production host.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new BlueBillywigClient(new FixedSettings(candidate), cache, _logs?.CreateLogger<BlueBillywigClient>() ?? NullLogger<BlueBillywigClient>.Instance, _http!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            SapiResult result = await client.TestConnectionAsync(timeout.Token);
            if (!result.Ok)
            {
                return CredentialCheck.Failed(result.StatusCode);
            }

            // The reference counts a 2xx without a name as a failed check.
            return result.Json?["name"] is JsonValue v && v.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name)
                ? new CredentialCheck(true, name, null)
                : new CredentialCheck(false, null, "Unexpected response from Blue Billywig");
        }
        catch (OperationCanceledException)
        {
            return CredentialCheck.Failed(0);
        }
    }

    /// <summary>Settings that never change and are never saved.</summary>
    private sealed class FixedSettings(BlueBillywigSettings value) : IBlueBillywigSettingsStore
    {
        public event Action? Changed { add { } remove { } }

        public BlueBillywigSettings Get() => value;

        public IReadOnlyCollection<string> ConfigOverrides() => [];

        public void Save(BlueBillywigSettings settings, string? apiSecret, string? readOnlyApiSecret) => throw new NotSupportedException();

        public void Disconnect() => throw new NotSupportedException();
    }
}

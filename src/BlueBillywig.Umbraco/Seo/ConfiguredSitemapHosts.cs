using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Seo;

/// <summary>
/// Which hosts the video sitemap is served for, and under which canonical origin. The route is anonymous and its answer
/// names absolute URLs and is cached, so the request's Host header is only ever compared with what the site has configured,
/// never copied: a forged name gets no sitemap, no cache entry and no build.
/// </summary>
public interface ISitemapHostPolicy
{
    /// <summary>The canonical site for a request, or null when the host is not one of the site's own.</summary>
    Task<SitemapSite?> ResolveAsync(HostString host, string scheme, CancellationToken ct);
}

/// <summary>
/// The site's own hosts, in this order: the domains assigned to content in Umbraco (Settings, Culture and Hostnames),
/// <c>Umbraco:CMS:WebRouting:UmbracoApplicationUrl</c>, and the plain (wildcard-free) entries of <c>AllowedHosts</c>. A request
/// whose host matches none of them is not answered (404): there is no canonical origin to build its URLs on, and serving the
/// default site's sitemap under a name the site does not own would only hand a forger a cacheable copy. A site with none of
/// the three configured has no sitemap until it sets one.
/// <para>
/// A configured entry names scheme, host and optionally a port. A host matches by name (and by port when the entry has one);
/// the origin used is the entry's, with the request's scheme (http or https) only where the entry has none.
/// </para>
/// </summary>
internal sealed class ConfiguredSitemapHosts : ISitemapHostPolicy
{
    private static readonly TimeSpan DomainsFor = TimeSpan.FromMinutes(1);

    private readonly IDomainService _domains;
    private readonly IOptionsMonitor<WebRoutingSettings> _routing;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfiguredSitemapHosts> _logger;
    private readonly object _lock = new();
    private IReadOnlyList<string> _domainNames = [];
    private DateTimeOffset _domainsLoaded = DateTimeOffset.MinValue;

    public ConfiguredSitemapHosts(IDomainService domains, IOptionsMonitor<WebRoutingSettings> routing, IConfiguration configuration, TimeProvider time, ILogger<ConfiguredSitemapHosts> logger)
    {
        _domains = domains;
        _routing = routing;
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    public async Task<SitemapSite?> ResolveAsync(HostString host, string scheme, CancellationToken ct)
    {
        if (!host.HasValue)
        {
            return null;
        }

        IReadOnlyList<string> names = await DomainNamesAsync(ct);
        var entries = names.Concat([_routing.CurrentValue.UmbracoApplicationUrl ?? ""])
            .Concat((_configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return Resolve(entries, host, scheme, out _);
    }

    /// <summary>The matching, without the I/O. The first entry that matches wins.</summary>
    internal static SitemapSite? Resolve(IEnumerable<string> entries, HostString requestHost, string requestScheme, out bool anyConfigured)
    {
        anyConfigured = false;
        string wanted = requestHost.Host.ToLowerInvariant();
        foreach (string entry in entries)
        {
            if (!TryParseEntry(entry, out string? entryScheme, out string entryHost, out int? entryPort))
            {
                continue;
            }

            anyConfigured = true;
            if (entryHost != wanted)
            {
                continue;
            }

            int requestPort = requestHost.Port ?? (requestScheme == Uri.UriSchemeHttps ? 443 : 80);
            if (entryPort is not null && entryPort != requestPort)
            {
                continue;
            }

            string scheme = entryScheme ?? (requestScheme == Uri.UriSchemeHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp);
            var builder = new UriBuilder(scheme, entryHost);
            if (entryPort is { } port)
            {
                builder.Port = port;
            }

            return new SitemapSite(builder.Uri);
        }

        return null;
    }

    /// <summary>
    /// Reads "example.com", "example.com:8080", "example.com/en", "https://example.com/en" or "https://example.com:8443".
    /// Wildcards ("*", "*.example.com") are not an origin and are skipped, as is anything that is not a host name.
    /// </summary>
    internal static bool TryParseEntry(string entry, out string? scheme, out string host, out int? port)
    {
        scheme = null;
        host = "";
        port = null;
        entry = entry.Trim();
        if (entry.Length == 0 || entry.Contains('*'))
        {
            return false;
        }

        string candidate = entry.Contains("://", StringComparison.Ordinal) ? entry : "http://" + entry;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0 || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (entry.Contains("://", StringComparison.Ordinal))
        {
            scheme = uri.Scheme;
        }

        host = uri.IdnHost.ToLowerInvariant();
        if (HasExplicitPort(entry))
        {
            port = uri.Port;
        }

        return true;
    }

    private static bool HasExplicitPort(string entry)
    {
        string authority = entry.Contains("://", StringComparison.Ordinal) ? entry[(entry.IndexOf("://", StringComparison.Ordinal) + 3)..] : entry;
        int slash = authority.IndexOf('/');
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        int colon = authority.LastIndexOf(':');
        return colon >= 0 && !authority.EndsWith(']') && int.TryParse(authority[(colon + 1)..], out _);
    }

    private async Task<IReadOnlyList<string>> DomainNamesAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_time.GetUtcNow() - _domainsLoaded < DomainsFor)
            {
                return _domainNames;
            }
        }

        IReadOnlyList<string> loaded;
        try
        {
            loaded = (await _domains.GetAllAsync(false)).Select(d => d.DomainName).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep what was known: the answer then is the last good list, or none, which means 404 rather than a guess.
            _logger.LogWarning(ex, "Blue Billywig video sitemap: could not read the domains assigned in Umbraco.");
            lock (_lock)
            {
                return _domainNames;
            }
        }

        lock (_lock)
        {
            _domainNames = loaded;
            _domainsLoaded = _time.GetUtcNow();
            return _domainNames;
        }
    }
}

namespace BlueBillywig.Umbraco.Configuration;

/// <summary>
/// Where the SAPI origin may point. The read-write API token is sent to this origin with every call, public pages load
/// player scripts from it and gate tokens are appended to its URLs, so it is never taken from a request body: it comes from
/// the site owner's configuration (<c>BlueBillywig:BaseUrl</c>) and even then only on a Blue Billywig domain. A development
/// OVP such as <c>example.dev.bbovp.eu</c> is under <c>bbovp.eu</c>.
/// </summary>
internal static class BlueBillywigHosts
{
    private static readonly string[] SapiDomains = ["bbvms.com", "bluebillywig.eu", "bluebillywig.com", "bbovp.eu"];

    /// <summary>True for a Blue Billywig domain or one of its subdomains. Not for look-alikes such as <c>evilbbvms.com</c>.</summary>
    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return SapiDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A SAPI origin: https, an allowed host, the default port, no credentials, no path, query or fragment. Returns the origin
    /// without a trailing slash.
    /// </summary>
    public static bool TryNormalizeBaseUrl(string? value, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/"
            || !IsAllowedHost(uri.IdnHost))
        {
            return false;
        }

        origin = "https://" + uri.IdnHost.ToLowerInvariant();
        return true;
    }
}

namespace BlueBillywig.Umbraco.Cta;

/// <summary>
/// The CTA link and form-endpoint validator. The plugin used its host framework's URL validator and sanitiser:
/// http or https only, a host, no credentials. A <c>javascript:</c> or <c>data:</c> link must never reach a widget's
/// navigation action, so anything doubtful becomes the empty string. Unlike the plugin this does not block private
/// hosts: the URL is followed by the visitor's browser, never fetched by the server.
/// </summary>
public static class CtaUrl
{
    public const int MaxLength = 2048;

    private const string Forbidden = "<>\"'`\\";

    /// <summary>The trimmed URL when it is a plain http(s) URL, otherwise "".</summary>
    public static string Sanitize(string? value)
    {
        string s = CtaText.PhpTrim(value ?? "");
        if (s.Length == 0 || s.Length > MaxLength)
        {
            return "";
        }

        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        foreach (char c in s)
        {
            if (c <= ' ' || c == '\u007f' || Forbidden.Contains(c) || char.IsSurrogate(c) && !IsPairedSurrogate(s))
            {
                return "";
            }
        }

        return Uri.TryCreate(s, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            && uri.UserInfo.Length == 0
            ? s
            : "";
    }

    public static bool IsHttpUrl(string? value) => Sanitize(value).Length > 0;

    /// <summary>
    /// The form's submit endpoint: https, or http on a local host only (localhost, *.localhost, 127.0.0.1, [::1]), so an
    /// editor can test against a local handler. The visitor's email is POSTed there, and over http on any other host it would
    /// travel in cleartext; the browser script applies the same rule (and additionally needs the page itself to be local).
    /// Anything else becomes "".
    /// </summary>
    public static string SanitizeEndpoint(string? value)
    {
        string s = Sanitize(value);
        if (s.Length == 0)
        {
            return "";
        }

        var uri = new Uri(s);
        return uri.Scheme == Uri.UriSchemeHttps || IsLocalHost(uri.Host) ? s : "";
    }

    public static bool IsEndpointUrl(string? value) => SanitizeEndpoint(value).Length > 0;

    internal static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host is "127.0.0.1" or "[::1]" or "::1";

    private static bool IsPairedSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return false;
            }
        }

        return true;
    }
}

using System.Net;
using System.Text;
using BlueBillywig.Umbraco.Localization;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>
/// The HTML shown instead of a protected embed (plugin: <c>getGateMessage</c>). The markup varies with the viewer's
/// login state, so a response that contains it must never be stored by a shared cache: see <see cref="GateResult.MustBePrivate"/>.
/// </summary>
public static class GateMessage
{
    public const string CssClass = "bb-content-gate";

    /// <summary>
    /// A custom message is HTML-encoded: the plugin let an admin supply markup (passed through an HTML filter), but the
    /// package has no filter it could rely on offline, and a gate message must never be a script injection point. Line
    /// breaks become <c>&lt;br /&gt;</c>. An anonymous visitor still gets the validated, encoded login link after it.
    /// </summary>
    public static string Render(bool isAuthenticated, string? loginUrl, string? customMessage)
    {
        if (!string.IsNullOrWhiteSpace(customMessage))
        {
            string text = string.Join("<br />\n", customMessage.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(WebUtility.HtmlEncode));
            // The login link is appended after a custom message too, for an anonymous visitor: otherwise a site that sets its own
            // wording would show a dead end with no way to sign in. The URL passes the same check and encoding as below.
            string link = !isAuthenticated && SafeLoginUrl(loginUrl) is { } customUrl
                ? $"<p><a href=\"{WebUtility.HtmlEncode(customUrl)}\">{WebUtility.HtmlEncode(VisitorText.Get("gateLogIn"))}</a></p>"
                : "";
            return $"<div class=\"{CssClass}\" role=\"alert\">{text}{link}</div>";
        }

        var sb = new StringBuilder();
        sb.Append($"<div class=\"{CssClass}\" role=\"alert\" style=\"padding:48px 32px;text-align:center;background:linear-gradient(135deg,#f3f5f5 0%,#e8eef3 100%);border:1px solid #dde3e8;border-radius:8px;\">");
        sb.Append("<p style=\"font-size:18px;font-weight:600;color:#002837;margin:0 0 8px;\">" + WebUtility.HtmlEncode(VisitorText.Get("gateRequiresAccess")) + "</p>");
        if (isAuthenticated)
        {
            sb.Append("<p style=\"color:#5a6872;\">" + WebUtility.HtmlEncode(VisitorText.Get("gateNoPermission")) + "</p>");
        }
        else
        {
            sb.Append("<p style=\"color:#5a6872;margin-bottom:16px;\">" + WebUtility.HtmlEncode(VisitorText.Get("gateLogInPrompt")) + "</p>");
            if (SafeLoginUrl(loginUrl) is { } url)
            {
                sb.Append($"<a href=\"{WebUtility.HtmlEncode(url)}\" style=\"display:inline-block;padding:10px 24px;background:#3578bb;color:#fff;border-radius:4px;text-decoration:none;font-weight:600;\">{WebUtility.HtmlEncode(VisitorText.Get("gateLogIn"))}</a>");
            }
        }

        return sb.Append("</div>").ToString();
    }

    /// <summary>A relative path (not protocol-relative) or an http(s) URL without control characters; anything else (javascript:, data:) is dropped.</summary>
    public static string? SafeLoginUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(c => c <= ' ' || c == '\u007f' || c == '\\'))
        {
            return null;
        }

        if (url.StartsWith('/'))
        {
            return url.StartsWith("//", StringComparison.Ordinal) ? null : url;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? url : null;
    }
}

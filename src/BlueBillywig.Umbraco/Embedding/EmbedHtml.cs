using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// Reads the embed code SAPI returns and rewrites it. The reference plugin rebuilds script tags through
/// DOMDocument to set the CSP nonce; this does the same with a small scanner, because no HTML parser ships
/// with .NET. Unlike the reference plugin it also treats the body as untrusted: only a few elements are kept, scripts
/// and iframes must be https on an allowed host, the package's own vocabulary (<c>data-bb-*</c> attributes and <c>bb-*</c>
/// class names, which the lazy loader and the form overlay act on) is stripped, and anything unexpected rejects the whole body.
/// </summary>
internal static class EmbedHtml
{
    internal static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "div", "span", "a", "noscript",
    };

    /// <summary>
    /// Returns false when the body is not something the renderer is willing to print. Any nonce already on a
    /// script is dropped and replaced by <paramref name="nonce"/>: upstream nonces are never trusted.
    /// </summary>
    public static bool TryPrepare(string html, string? nonce, Func<string, bool> isAllowedHost, out string result)
    {
        result = "";
        var sb = new StringBuilder(html.Length + 32);
        int i = 0;
        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            sb.Append(html, i, lt - i);

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                int end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 3;
                continue;
            }

            bool closing = lt + 1 < html.Length && html[lt + 1] == '/';
            int p = lt + (closing ? 2 : 1);
            int nameStart = p;
            while (p < html.Length && (char.IsAsciiLetterOrDigit(html[p]))) p++;
            if (p == nameStart) return false;
            string name = html[nameStart..p].ToLowerInvariant();
            if (!AllowedTags.Contains(name)) return false;

            if (closing)
            {
                int gt = html.IndexOf('>', p);
                if (gt < 0 || html[p..gt].Trim().Length > 0) return false;
                sb.Append("</").Append(name).Append('>');
                i = gt + 1;
                continue;
            }

            if (!TryReadAttributes(html, ref p, out List<(string Name, string Value)>? attributes, out bool selfClosing))
            {
                return false;
            }

            if (!TryWriteTag(sb, name, attributes, nonce, isAllowedHost, selfClosing))
            {
                return false;
            }

            i = p;
            if (name == "script")
            {
                // Raw text element: it may only be empty, so no inline code ever reaches the page.
                int close = html.IndexOf("</script", i, StringComparison.OrdinalIgnoreCase);
                if (close < 0 || html[i..close].Trim().Length > 0) return false;
                int gt = html.IndexOf('>', close);
                if (gt < 0) return false;
                sb.Append("</script>");
                i = gt + 1;
            }
        }

        result = sb.ToString();
        return true;
    }

    /// <summary>True for <c>data-bb-*</c>, in any case.</summary>
    internal static bool IsPackageAttribute(string name) => name.StartsWith("data-bb-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes every class token that starts with <c>bb-</c> (any case; tokens are separated by any ASCII whitespace). The
    /// value is already entity-decoded, so <c>bb&amp;#45;lazy-embed</c> is seen as what the browser will see.
    /// </summary>
    internal static string StripPackageClasses(string value) =>
        string.Join(' ', value.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith("bb-", StringComparison.OrdinalIgnoreCase)));

    private static bool TryReadAttributes(string html, ref int p, out List<(string Name, string Value)> attributes, out bool selfClosing)
    {
        attributes = [];
        selfClosing = false;
        while (true)
        {
            while (p < html.Length && char.IsWhiteSpace(html[p])) p++;
            if (p >= html.Length) return false;
            if (html[p] == '>') { p++; return true; }
            if (html[p] == '/')
            {
                selfClosing = true;
                p++;
                continue;
            }

            int nameStart = p;
            while (p < html.Length && !char.IsWhiteSpace(html[p]) && html[p] is not ('=' or '>' or '/' or '"' or '\'' or '<')) p++;
            if (p == nameStart) return false;
            string attrName = html[nameStart..p].ToLowerInvariant();
            while (p < html.Length && char.IsWhiteSpace(html[p])) p++;

            string value = "";
            if (p < html.Length && html[p] == '=')
            {
                p++;
                while (p < html.Length && char.IsWhiteSpace(html[p])) p++;
                if (p >= html.Length) return false;
                if (html[p] is '"' or '\'')
                {
                    char quote = html[p++];
                    int end = html.IndexOf(quote, p);
                    if (end < 0) return false;
                    value = html[p..end];
                    p = end + 1;
                }
                else
                {
                    int vs = p;
                    while (p < html.Length && !char.IsWhiteSpace(html[p]) && html[p] != '>') p++;
                    value = html[vs..p];
                }
            }

            attributes.Add((attrName, WebUtility.HtmlDecode(value)));
        }
    }

    private static bool TryWriteTag(StringBuilder sb, string name, List<(string Name, string Value)> attributes, string? nonce, Func<string, bool> isAllowedHost, bool selfClosing)
    {
        sb.Append('<').Append(name);
        foreach ((string attr, string rawValue) in attributes)
        {
            string value = rawValue;
            if (attr == "nonce" || attr == "srcdoc" || attr.StartsWith("on", StringComparison.Ordinal))
            {
                continue;
            }

            // The loader (bb-lazy.js) and the overlay (bb-cta-form-overlay.js) act on this vocabulary. Markup that came from
            // upstream must not be able to speak it: a forged data-bb-embed would have the loader build any script from it,
            // a forged bb-cta-form class would turn an element into an email form.
            if (IsPackageAttribute(attr))
            {
                continue;
            }

            if (attr == "class")
            {
                value = StripPackageClasses(value);
                if (value.Length == 0)
                {
                    continue;
                }
            }

            if (attr is "src" or "href")
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")) return false;
                // Links are plain navigation (http or https). Whatever loads something, a script or an iframe, must be https
                // on an allowed host: over http the content could be replaced in transit, and a gate token appended to the URL
                // would travel in cleartext.
                if (name is "script" or "iframe" && (uri.Scheme != Uri.UriSchemeHttps || !isAllowedHost(uri.Host))) return false;
                value = uri.AbsoluteUri;
            }
            else if (name == "script")
            {
                // type, async, defer and the like are fine; nothing else changes how a script runs.
                if (attr is not ("type" or "async" or "defer" or "charset" or "crossorigin")) continue;
            }

            sb.Append(' ').Append(attr);
            if (value.Length > 0 || attr is "src" or "href")
            {
                sb.Append("=\"").Append(Encoder.Encode(value)).Append('"');
            }
        }

        if (name == "script" && !string.IsNullOrEmpty(nonce))
        {
            sb.Append(" nonce=\"").Append(Encoder.Encode(nonce)).Append('"');
        }

        sb.Append(selfClosing ? " />" : ">");
        return true;
    }
}

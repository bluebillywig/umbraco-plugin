using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>The embed HTML after injection, how many URLs got the token, and whether anything was replaced by the gate message.</summary>
public sealed record InjectResult(string Html, int Substitutions, bool FailedClosed);

/// <summary>
/// Puts the JWT into the player URLs of an embed (plugin: <c>ContentGate::injectJwt</c>), including the URLs hidden in
/// base64 <c>data-bb-embed</c> lazy wrappers. Every path that cannot be handled safely returns the gate message instead of
/// the embed: an un-injected lazy wrapper would load an ungated player once the visitor scrolls.
/// <para>
/// Seam for the wiring: <paramref name="isGatedHost"/> must accept only hosts that serve Blue Billywig players (the
/// publication's own host and <c>*.bbvms.com</c>). The token is a credential; it must never be appended to a URL on any
/// other host. <paramref name="gateMessage"/> must return markup that is safe to emit in place of the embed.
/// </para>
/// </summary>
public sealed partial class GateInjector
{
    /// <summary>Nested base64 beyond this depth is pathological input and fails closed.</summary>
    public const int MaxDepth = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string, bool> _isGatedHost;
    private readonly Func<string> _gateMessage;
    private readonly ILogger _logger;

    public GateInjector(Func<string, bool> isGatedHost, Func<string> gateMessage, ILogger? logger = null)
    {
        _isGatedHost = isGatedHost;
        _gateMessage = gateMessage;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The default host rule: <c>bbvms.com</c> and its subdomains.</summary>
    public static bool IsBbvmsHost(string host) => host.Equals("bbvms.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".bbvms.com", StringComparison.OrdinalIgnoreCase);

    public InjectResult Inject(string content, string jwt) => Inject(content, jwt, 0);

    // Wrapper emitted by wrap_lazy_embed: the div, and the <noscript> fallback that follows it. The noscript is consumed with
    // the div so that, when the payload cannot be decoded, the replacement removes both: the fallback carries a working URL
    // that pass 2 would otherwise inject a token into and leak to visitors without JavaScript. Case-insensitive, unlike the
    // plugin, because HTML tag and attribute names are.
    [GeneratedRegex(@"<div\b[^>]*\bdata-bb-embed=""([A-Za-z0-9+/=]+)""[^>]*>[\s\S]*?</div>(?:\s*<noscript>[\s\S]*?</noscript>)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LazyWrapper();

    [GeneratedRegex("data-bb-embed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LazyAttributeName();

    // A player URL that starts a token (after start, whitespace, a quote or '>'), so a URL buried in another URL's query or
    // path (https://evil.example/?u=https://pub.bbvms.com/p/..) is not touched. The query excludes '?' from the path class so
    // a URL that already has a query keeps it in the query group, as in the plugin (fixes ".js?a=1?jwt=").
    [GeneratedRegex(@"(?<![^""'\s>])(?<scheme>(?:https?:)?//)(?<host>[A-Za-z0-9.-]+)(?<port>:\d{1,5})?(?<path>/(?:p|ch|cpp|view|amp)/[^""'?\s<>]+)(?<query>\?[^""'\s<>]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PlayerUrl();

    [GeneratedRegex(@"[?&;]jwt=", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HasJwtParameter();

    internal InjectResult Inject(string content, string jwt, int depth)
    {
        if (depth > MaxDepth)
        {
            // Returning the content unchanged would hand an ungated embed to the outer re-encode: the bypass the cap exists to stop.
            _logger.LogError("Blue Billywig ContentGate: injectJwt depth cap exceeded; returning the gate message. Likely a crafted nested-base64 payload.");
            return new InjectResult(_gateMessage(), 0, true);
        }

        try
        {
            return InjectCore(content, jwt, depth);
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogError("Blue Billywig ContentGate: injectJwt pattern timed out; returning the gate message.");
            return new InjectResult(_gateMessage(), 0, true);
        }
    }

    private InjectResult InjectCore(string content, string jwt, int depth)
    {
        string encodedJwt = WebUtility.UrlEncode(jwt);
        bool failed = false;

        // Every mention of the lazy attribute must be handled by the wrapper pattern. A variant it does not match (single
        // quotes, odd spacing) would stay un-gated and load the player later, so any leftover closes the gate for the lot.
        int mentioned = LazyAttributeName().Matches(content).Count;
        int handled = 0;

        // Pass 1: lazy wrappers. Decode, inject (recursively), re-encode.
        string pass1 = LazyWrapper().Replace(content, m =>
        {
            handled++;
            string payload = m.Groups[1].Value;
            if (!TryDecode(payload, out string? decoded))
            {
                _logger.LogError("Blue Billywig ContentGate: lazy embed base64 decode failed; showing the gate message and dropping the noscript sibling.");
                failed = true;
                return _gateMessage();
            }

            InjectResult inner = Inject(decoded, jwt, depth + 1);
            if (inner.FailedClosed)
            {
                // Stricter than the plugin, which re-encoded the inner gate message into the wrapper: nothing of an embed that
                // failed to inject stays behind, not even as a base64 attribute.
                failed = true;
                return _gateMessage();
            }

            string reencoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner.Html));

            // Swap only the attribute value: the rest of the placeholder and the noscript sibling (pass 2 injects there) stay.
            return m.Value.Replace($"data-bb-embed=\"{payload}\"", $"data-bb-embed=\"{reencoded}\"", StringComparison.OrdinalIgnoreCase);
        });

        if (mentioned != handled)
        {
            _logger.LogError("Blue Billywig ContentGate: {Unhandled} data-bb-embed attribute(s) the injector cannot rewrite; returning the gate message.", mentioned - handled);
            return new InjectResult(_gateMessage(), 0, true);
        }

        // Pass 2: inline player URLs, including the noscript fallback and eagerly rendered embeds.
        int substitutions = 0;
        string pass2 = PlayerUrl().Replace(pass1, m =>
        {
            // The token is a credential: never onto http or a protocol-relative URL (which takes the page's scheme), only https.
            if (!m.Groups["scheme"].Value.Equals("https://", StringComparison.OrdinalIgnoreCase) || !_isGatedHost(m.Groups["host"].Value))
            {
                return m.Value;
            }

            substitutions++;
            string query = m.Groups["query"].Value;

            // Idempotent: a URL that already carries a jwt parameter keeps it.
            if (query.Length > 0 && HasJwtParameter().IsMatch(query))
            {
                return m.Value;
            }

            string separator = query.Length == 0 ? "?" : query.Contains("&amp;", StringComparison.Ordinal) ? "&amp;" : "&";
            return m.Value + separator + "jwt=" + encodedJwt;
        });

        return new InjectResult(pass2, substitutions, failed);
    }

    private static bool TryDecode(string payload, out string decoded)
    {
        decoded = "";
        byte[] buffer = new byte[payload.Length];
        if (!Convert.TryFromBase64String(payload, buffer, out int written))
        {
            return false;
        }

        try
        {
            decoded = StrictUtf8.GetString(buffer, 0, written);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

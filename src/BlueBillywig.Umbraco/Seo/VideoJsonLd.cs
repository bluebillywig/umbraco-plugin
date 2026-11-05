using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Seo;

/// <summary>What the JSON-LD VideoObject is built from. Empty optional values are left out.</summary>
/// <param name="ThumbnailUrl">The absolute URL of the main thumbnail (publication asset path plus the thumbnail src).</param>
/// <param name="LengthSeconds">Clip length in seconds; only whole seconds of at least 1 are written.</param>
/// <param name="Transcript">May contain markup; tags are stripped.</param>
public sealed record VideoJsonLdInput(string? ClipId, string Title, string? Description, string? UploadDate, string? ThumbnailUrl, double? LengthSeconds, string? Transcript);

/// <summary>
/// The schema.org <c>VideoObject</c> block (plugin: <c>Admin::render_block()</c>). Clip titles and descriptions are written
/// by anyone with OVP write access, so they are untrusted: the 2.0.3 stored-XSS fix (ADO 19070) is the core of this class.
/// <para>
/// Escaping: System.Text.Json's default encoder writes <c>&lt; &gt; &amp; ' "</c> and every non-ASCII character, U+2028 and
/// U+2029 included, as <c>\uXXXX</c>: the equivalent of <c>JSON_HEX_TAG|JSON_HEX_AMP|JSON_HEX_APOS|JSON_HEX_QUOT</c>. The
/// relaxed encoder must never be used here. Text that is not valid UTF-16 (a lone surrogate) is refused up front, and the
/// result is also checked for raw <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and <c>'</c> before it is returned. On any failure
/// nothing is emitted and the reason is logged, as in the plugin, rather than shipping an empty script block.
/// </para>
/// <para>
/// Seam for the wiring: the render chokepoint calls <see cref="Render"/> for iframe, oembed, url and amp embeds only
/// (see <see cref="ShouldEmitFor"/>), because the JavaScript embed injects its own JSON-LD at runtime, and appends the
/// returned markup next to the embed. It must pass the CSP nonce it uses for scripts.
/// </para>
/// </summary>
public static partial class VideoJsonLd
{
    [GeneratedRegex(@"^[A-Za-z0-9+/=_-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NonceShape();

    [GeneratedRegex(@"<!--[\s\S]*?-->|<(script|style)\b[^>]*>[\s\S]*?</\1\s*>|</?[A-Za-z!?][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Markup();

    /// <summary>The plugin emits server-side JSON-LD only for these embed types; the JavaScript embed brings its own.</summary>
    public static bool ShouldEmitFor(string? embedType) => embedType is "iframe" or "oembed" or "url" or "amp";

    /// <summary>Reads the fields from a SAPI mediaclip: <c>title, description, createddate, length, transcript</c>.</summary>
    public static VideoJsonLdInput FromClip(JsonNode clip, string? thumbnailUrl) => new(
        SapiJson.Str(clip["id"]),
        SapiJson.Str(clip["title"]),
        SapiJson.Str(clip["description"]),
        SapiJson.Str(clip["createddate"]),
        thumbnailUrl,
        SapiJson.PhpEmpty(clip["length"]) ? null : SapiJson.Num(clip["length"], 0),
        SapiJson.Str(clip["transcript"]));

    /// <summary>The object, or null when there is no title (the plugin requires one).</summary>
    public static JsonObject? Build(VideoJsonLdInput input)
    {
        if (SapiJson.PhpEmpty(input.Title))
        {
            return null;
        }

        var ld = new JsonObject
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "VideoObject",
            ["name"] = input.Title,
            ["description"] = SapiJson.PhpEmpty(input.Description) ? input.Title : input.Description,
        };

        // The plugin wrote an empty uploadDate when SAPI had none, which no rich-results parser accepts; it is left out here.
        if (!string.IsNullOrEmpty(input.UploadDate))
        {
            ld["uploadDate"] = input.UploadDate;
        }

        if (!string.IsNullOrEmpty(input.ThumbnailUrl))
        {
            ld["thumbnailUrl"] = input.ThumbnailUrl;
        }

        if (input.LengthSeconds is double length && double.IsFinite(length) && length >= 1 && length < long.MaxValue)
        {
            ld["duration"] = "PT" + ((long)length).ToString(CultureInfo.InvariantCulture) + "S";
        }

        if (!SapiJson.PhpEmpty(input.Transcript) && StripTags(input.Transcript!) is { Length: > 0 } transcript)
        {
            ld["transcript"] = transcript;
        }

        return ld;
    }

    /// <summary>The safe JSON text, or null (logged) when it cannot be encoded or fails the final check.</summary>
    public static string? Encode(JsonObject ld, string? clipId = null, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (FindInvalidUtf16(ld) is { } bad)
        {
            logger.LogWarning("Blue Billywig: JSON-LD encoding failed for clip {Clip}: invalid UTF-16 in {Field}.", clipId ?? "(unknown)", bad);
            return null;
        }

        string json;
        try
        {
            json = ld.ToJsonString();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            logger.LogWarning(e, "Blue Billywig: JSON-LD encoding failed for clip {Clip}.", clipId ?? "(unknown)");
            return null;
        }

        // Belt and braces: nothing that can end the script element, open a comment or start a line terminator may remain.
        if (json.AsSpan().IndexOfAny("<>&'") >= 0 || json.Contains('\u2028') || json.Contains('\u2029'))
        {
            logger.LogWarning("Blue Billywig: JSON-LD for clip {Clip} still contained HTML-significant characters after encoding; nothing emitted.", clipId ?? "(unknown)");
            return null;
        }

        return json;
    }

    /// <summary>
    /// The whole <c>&lt;script type="application/ld+json"&gt;</c> element, or null when there is nothing to emit. A nonce
    /// that is not base64 or url-safe characters is left off (the browser then blocks the script under a strict CSP, which is safe).
    /// </summary>
    public static string? Render(VideoJsonLdInput input, string? nonce = null, ILogger? logger = null)
    {
        JsonObject? ld = Build(input);
        if (ld is null || Encode(ld, input.ClipId, logger) is not { } json)
        {
            return null;
        }

        string nonceAttribute = !string.IsNullOrEmpty(nonce) && NonceShape().IsMatch(nonce)
            ? $" nonce=\"{nonce}\""
            : "";
        return $"<script type=\"application/ld+json\"{nonceAttribute}>{json}</script>";
    }

    /// <summary>Strip-all-tags behaviour of the plugin: drops comments, script and style blocks and tags, then trims.</summary>
    public static string StripTags(string html) => Sapi.SapiJson.PhpEmpty(html) ? "" : Cta.CtaText.PhpTrim(Markup().Replace(html, ""));

    private static string? FindInvalidUtf16(JsonNode? node, string path = "$")
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var pair in o)
                {
                    if (!IsWellFormed(pair.Key))
                    {
                        return path;
                    }

                    if (FindInvalidUtf16(pair.Value, pair.Key) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValue v when v.TryGetValue(out string? s):
                return IsWellFormed(s) ? null : path;
            default:
                return null;
        }
    }

    internal static bool IsWellFormed(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return false;
            }
        }

        return true;
    }
}

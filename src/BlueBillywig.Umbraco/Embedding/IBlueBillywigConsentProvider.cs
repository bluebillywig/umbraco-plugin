using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// Decides whether the current visitor may load Blue Billywig players. Until it says yes, the renderer
/// emits a neutral placeholder and nothing is requested from the player host. Replace it with a
/// registration of your own that reads your consent platform. The default grants consent.
/// </summary>
public interface IBlueBillywigConsentProvider
{
    bool IsGranted(HttpContext? httpContext);

    /// <summary>
    /// The sentence shown in the consent placeholder, plain text (it is HTML-encoded on output, so markup shows as text), or
    /// null to use the site's setting (<c>BlueBillywig:ConsentPlaceholderMessage</c> or the settings dashboard) and then the
    /// built-in text. Counterpart of the reference plugin's <c>bluebillywig_consent_placeholder_message</c> filter, which
    /// also lets a consent platform word the notice. Longer than <see cref="ConsentMessage.MaxLength"/> is cut.
    /// </summary>
    string? GetPlaceholderMessage(HttpContext? httpContext) => null;
}

/// <summary>The consent placeholder's message: the built-in text and the rules every custom text passes.</summary>
public static class ConsentMessage
{
    public const int MaxLength = 500;

    /// <summary>
    /// Deliberately names no host: the placeholder must not mention, link or load anything from the player domain before
    /// consent. (The reference plugin's message names bbvms.com; the wording here says "Blue Billywig".)
    /// </summary>
    public const string Default = "Watching this video will transmit data to Blue Billywig, our video provider. Clicking Load will grant consent for all Blue Billywig videos on this page.";

    /// <summary>Trims, drops control characters and cuts to <see cref="MaxLength"/>. Empty means "use the default". Not encoded: output encodes.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        string cleaned = string.Concat(text.Where(c => !char.IsControl(c) || c is ' ')).Trim();
        return cleaned.Length > MaxLength ? cleaned[..MaxLength].TrimEnd() : cleaned;
    }
}

internal sealed class AlwaysGrantedConsentProvider : IBlueBillywigConsentProvider
{
    public bool IsGranted(HttpContext? httpContext) => true;
}

/// <summary>Supplies the CSP nonce for the script tags the renderer emits. The default has none.</summary>
public interface IBlueBillywigCspNonceProvider
{
    string? GetNonce(HttpContext? httpContext);
}

internal sealed class NoCspNonceProvider : IBlueBillywigCspNonceProvider
{
    public string? GetNonce(HttpContext? httpContext) => null;
}

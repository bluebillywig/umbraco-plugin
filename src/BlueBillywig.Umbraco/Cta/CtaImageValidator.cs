using System.Text.RegularExpressions;

namespace BlueBillywig.Umbraco.Cta;

/// <summary>
/// The input rules of the reference plugin's <c>upload_cta_image</c>: a data URI of a JPEG, PNG, GIF or WebP, at most
/// 10 MB. Stricter than the plugin in one way: the bytes must look like the declared type, so a renamed file is refused
/// before anything is sent to SAPI.
/// </summary>
public static partial class CtaImageValidator
{
    /// <summary>The plugin's limit on the data URI string.</summary>
    public const int MaxDataLength = 10 * 1024 * 1024;

    public const string InvalidMessage = "Invalid image data";

    public const string TooLargeMessage = "Image too large (max 10 MB)";

    [GeneratedRegex(@"^data:image/(jpeg|png|gif|webp);base64,", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    /// <summary>Null when valid, otherwise the message for the editor.</summary>
    public static string? Validate(string? dataUri)
    {
        if (string.IsNullOrEmpty(dataUri))
        {
            return InvalidMessage;
        }

        Match header = Header().Match(dataUri);
        if (!header.Success)
        {
            return InvalidMessage;
        }

        if (dataUri.Length > MaxDataLength)
        {
            return TooLargeMessage;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUri[header.Length..]);
        }
        catch (FormatException)
        {
            return InvalidMessage;
        }

        return LooksLike(header.Groups[1].Value, bytes) ? null : InvalidMessage;
    }

    private static bool LooksLike(string type, ReadOnlySpan<byte> b) => type switch
    {
        "png" => b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "gif" => b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)),
        "webp" => b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}

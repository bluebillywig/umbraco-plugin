using System.Buffers.Text;
using System.Text;

namespace BlueBillywig.Umbraco;

/// <summary>
/// Base64url without padding (RFC 7515 appendix C), written by hand so the JWT and the CTA form-config class share
/// one strict implementation. The reference plugin does the same with a plain base64 encode, character swap and padding trim.
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Encode(string utf8Text) => Encode(Encoding.UTF8.GetBytes(utf8Text));

    /// <summary>
    /// Strict decode: only the url alphabet, no padding, no whitespace, canonical (re-encoding gives the input back).
    /// Null for anything else, so callers can fail closed instead of guessing.
    /// </summary>
    public static byte[]? TryDecode(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length % 4 == 1)
        {
            return null;
        }

        foreach (char c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return null;
            }
        }

        string padded = text.Replace('-', '+').Replace('_', '/').PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
        byte[] buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(padded.Length)];
        if (!Convert.TryFromBase64String(padded, buffer, out int written))
        {
            return null;
        }

        byte[] result = buffer[..written];
        return Encode(result) == text ? result : null;
    }
}

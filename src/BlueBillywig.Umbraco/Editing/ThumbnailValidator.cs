using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>
/// Validates an uploaded clip thumbnail before it is sent to SAPI. Port of the plugin's <c>ThumbnailValidator</c>.
/// Blue Billywig only keeps JPG and PNG thumbnails: other types are accepted by the SAPI update without an error and
/// never stored, so the upload would silently do nothing. The type is read from the decoded bytes, not from the data
/// URI's declared type, so a renamed GIF is refused. No image library is used: the PNG and JPEG headers are read directly.
/// </summary>
public static partial class ThumbnailValidator
{
    /// <summary>The plugin's limit on the data URI string.</summary>
    public const int MaxDataLength = 10 * 1024 * 1024;

    /// <summary>How far the image's aspect ratio may differ from the video's (0.15 is 15%).</summary>
    public const double ShapeTolerance = 0.15;

    public const string TypeError = "Only JPG and PNG images can be used as a thumbnail.";

    /// <summary>Returns null when the image is valid, otherwise the message for the editor.</summary>
    public static string? Validate(string? dataUri, int videoWidth = 0, int videoHeight = 0)
    {
        if (dataUri is null)
        {
            return TypeError;
        }

        Match header = DataUriHeader().Match(dataUri);
        if (!header.Success)
        {
            return TypeError;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUri[header.Length..]);
        }
        catch (FormatException)
        {
            return TypeError;
        }

        if (!TryReadSize(bytes, out int imageWidth, out int imageHeight) || imageWidth <= 0 || imageHeight <= 0)
        {
            return TypeError;
        }

        // Without the video size there is nothing to compare with.
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return null;
        }

        double imageRatio = (double)imageWidth / imageHeight;
        double videoRatio = (double)videoWidth / videoHeight;
        // Compared symmetrically so wider and taller are judged the same way. The small margin keeps a shape exactly
        // at the tolerance from failing on floating point rounding.
        if (Math.Max(imageRatio / videoRatio, videoRatio / imageRatio) - 1 > ShapeTolerance + 1e-9)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"The image is {imageWidth} × {imageHeight} pixels, which is a different shape than the video ({videoWidth} × {videoHeight} pixels). Upload an image with the same shape as the video, for example {videoWidth} × {videoHeight} pixels.");
        }

        return null;
    }

    /// <summary>Reads the pixel size of a PNG (IHDR) or JPEG (the first start-of-frame marker). False for anything else, including GIF and WebP.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length >= 24 && data[..8].SequenceEqual(png))
        {
            // The first chunk must be IHDR: length (4), "IHDR", width (4), height (4).
            if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
            {
                return false;
            }

            width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), int.MaxValue);
            height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[20..]), int.MaxValue);
            return true;
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            int i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                byte marker = data[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                // Markers without a length: stand-alone ones (TEM, RSTn, SOI, EOI).
                if (marker is 0x01 or 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
                {
                    i += 2;
                    continue;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
                bool startOfFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                if (startOfFrame)
                {
                    if (i + 9 > data.Length)
                    {
                        return false;
                    }

                    height = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..]);
                    return true;
                }

                if (length < 2)
                {
                    return false;
                }

                i += 2 + length;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^data:image/(jpeg|png);base64,")]
    private static partial Regex DataUriHeader();
}

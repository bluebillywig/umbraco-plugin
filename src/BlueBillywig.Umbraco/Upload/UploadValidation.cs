using System.Text;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Upload;

/// <summary>What init sends to SAPI to create the clip, after validation.</summary>
public sealed record NewClip(string Filename, long Filesize, string ContentType, string Title, string? Description, IReadOnlyList<string> Tags, string Status);

/// <summary>Input checks for the four upload calls. Ports <c>init_upload</c>, <c>sign_part</c>, <c>complete_upload</c> and <c>abort_upload</c>.</summary>
public static partial class UploadValidation
{
    public const long MaxFileSize = 21_474_836_480; // 20 GB, the plugin's Uppy limit
    public const int MaxParts = 10_000;              // the S3 multipart ceiling

    /// <summary>The same allowlist the reference plugin enforces on init_upload (2.0.3), and on every other write path.</summary>
    public static readonly string[] Statuses = ["published", "draft", "archived"];

    public static bool TryParseInit(InitUploadRequestModel? request, out NewClip clip, out string error)
    {
        clip = new NewClip("", 0, "", "", null, [], "published");
        error = "";
        if (request is null || string.IsNullOrWhiteSpace(request.Filename) || request.Filesize <= 0)
        {
            error = "Missing filename or filesize";
            return false;
        }

        string filename = SanitizeFileName(request.Filename);
        if (filename.Length == 0)
        {
            error = "Invalid filename";
            return false;
        }

        if (request.Filesize > MaxFileSize)
        {
            error = "File is too large";
            return false;
        }

        string status = "published";
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            status = request.Status.Trim();
            // Rejected rather than dropped, so the upload form tells the user instead of creating a differently-stated clip.
            if (!Statuses.Contains(status))
            {
                error = "Invalid status value";
                return false;
            }
        }

        string contentType = !string.IsNullOrWhiteSpace(request.ContentType) && ContentTypePattern().IsMatch(request.ContentType.Trim())
            ? request.ContentType.Trim()
            : "application/octet-stream";

        // The title falls back to the filename without its extension, like the plugin's pathinfo(PATHINFO_FILENAME).
        string title = SanitizeText(request.Title, 255, multiline: false);
        if (title.Length == 0)
        {
            title = Path.GetFileNameWithoutExtension(filename);
        }

        string description = SanitizeText(request.Description, 4000, multiline: true);
        clip = new NewClip(filename, request.Filesize, contentType, title, description.Length > 0 ? description : null, ParseTags(request.Tags), status);
        return true;
    }

    /// <summary>Like <c>sanitize_file_name</c>: no directories, no control characters, no characters a filesystem or header would object to.</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        string leaf = name.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder();
        foreach (char c in leaf)
        {
            if (char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                continue;
            }

            sb.Append(c);
        }

        string cleaned = sb.ToString().Trim().TrimStart('.');
        return cleaned.Length > 255 ? cleaned[..255] : cleaned;
    }

    /// <summary>Like <c>sanitize_text_field</c> and <c>sanitize_textarea_field</c>: tags removed, control characters dropped, length bounded.</summary>
    public static string SanitizeText(string? text, int maxLength, bool multiline)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        string cleaned = TagPattern().Replace(text, " ").Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder();
        foreach (char c in cleaned)
        {
            sb.Append(c == '\n' && multiline ? '\n' : char.IsControl(c) ? ' ' : c);
        }

        string result = multiline
            ? string.Join('\n', sb.ToString().Split('\n').Select(line => HorizontalSpace().Replace(line, " ").Trim())).Trim()
            : WhitespacePattern().Replace(sb.ToString(), " ").Trim();
        return result.Length > maxLength ? result[..maxLength].TrimEnd() : result;
    }

    /// <summary>Comma separated tags, trimmed, empty ones dropped, case-insensitive duplicates removed.</summary>
    public static IReadOnlyList<string> ParseTags(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();
        foreach (string raw in SanitizeText(csv, 2000, multiline: false).Split(','))
        {
            string tag = raw.Trim();
            if (tag.Length == 0 || tag.Length > 64 || !seen.Add(tag))
            {
                continue;
            }

            tags.Add(tag);
            if (tags.Count == 50)
            {
                break;
            }
        }

        return tags;
    }

    public static bool IsUploadId(string? id) => id is not null && UploadIdPattern().IsMatch(id);

    public static bool TryParseSign(SignPartRequestModel? request, out string uploadId, out int partNumber, out string error)
    {
        uploadId = request?.UploadId?.Trim() ?? "";
        partNumber = request?.PartNumber ?? 0;
        error = "";
        if (!IsUploadId(uploadId) || partNumber < 1)
        {
            error = "Missing uploadId or partNumber";
            return false;
        }

        if (partNumber > MaxParts)
        {
            error = "Invalid partNumber";
            return false;
        }

        return true;
    }

    public static bool TryParseComplete(CompleteUploadRequestModel? request, out string uploadId, out List<(int PartNumber, string ETag)> parts, out string error)
    {
        uploadId = request?.UploadId?.Trim() ?? "";
        parts = [];
        error = "";
        if (!IsUploadId(uploadId) || request?.Parts is null || request.Parts.Count == 0)
        {
            error = "Missing uploadId or parts";
            return false;
        }

        if (request.Parts.Count > MaxParts)
        {
            error = "Too many parts";
            return false;
        }

        var numbers = new HashSet<int>();
        foreach (UploadPartModel part in request.Parts)
        {
            string etag = part.ETag?.Trim() ?? "";
            if (part.PartNumber is < 1 or > MaxParts || etag.Length is 0 or > 256 || etag.Any(char.IsControl) || !numbers.Add(part.PartNumber))
            {
                error = "Malformed parts: missing PartNumber or ETag";
                return false;
            }

            parts.Add((part.PartNumber, etag));
        }

        return true;
    }

    /// <summary>The Upload-Metadata header value: <c>key base64(value)</c> pairs.</summary>
    public static string EncodeTusMetadata(IEnumerable<KeyValuePair<string, string>> metadata) =>
        string.Join(',', metadata.Select(m => m.Key + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Value))));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}$")]
    private static partial Regex ContentTypePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._~+/=:-]{1,512}$")]
    private static partial Regex UploadIdPattern();

    [GeneratedRegex("<[^>]*>?")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex HorizontalSpace();
}

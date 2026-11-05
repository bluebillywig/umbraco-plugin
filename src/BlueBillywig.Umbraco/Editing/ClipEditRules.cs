using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Upload;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>
/// The input rules of every clip write, ported from the reference plugin's <c>update_clip_metadata</c>,
/// <c>update_clip_status</c> and <c>bulk_action</c>. SAPI accepts any status or CPP string with a 200 and ignores
/// it (checked against bb.dev), so these checks are the only thing between a typo and a silent no-op.
/// </summary>
public static partial class ClipEditRules
{
    /// <summary>The plugin's bulk cap: more ids than this is refused, not truncated.</summary>
    public const int MaxBulk = 50;

    public const long MaxId = 1_000_000_000_000;

    public static readonly string[] Statuses = UploadValidation.Statuses;

    public static readonly string[] BulkActions = ["status", "folder", "cpp"];

    public static bool IsStatus(string? value) => value is not null && Statuses.Contains(value, StringComparer.Ordinal);

    /// <summary>A CPP is identified on a clip by its uuid <c>code</c>. The empty string means "clear" and is handled by callers.</summary>
    public static bool IsCppCode(string? value) => value is not null && CppPattern().IsMatch(value);

    /// <summary>Folder codes are short alphanumeric codes from SAPI. Empty means "move to the root".</summary>
    public static bool IsFolderCode(string? value) => value is not null && LibraryQuery.IsFolderCode(value);

    /// <summary>The plugin's embargo format: a date, optionally with a time and a zone. Empty clears the embargo.</summary>
    public static bool IsEmbargo(string? value) => value is not null && (value.Length == 0 || EmbargoPattern().IsMatch(value));

    /// <summary>
    /// What SAPI holds for an embargo that was "cleared". Checked on bb.dev 2026-11-05: an empty string or null in the PUT
    /// is answered 200 and changes nothing; any other non-date (<c>"0"</c>, <c>false</c>, text) makes both fields
    /// <c>2001-12-02T23:00:00Z</c>, a window in the past, which is no embargo. The editor shows that value as empty.
    /// </summary>
    public static string? EmbargoOrNull(string? stored) =>
        string.IsNullOrEmpty(stored) || stored.StartsWith("2001-12-02T23:", StringComparison.Ordinal) ? null : stored;

    public static bool IsIsoCode(string? value) => value is not null && IsoPattern().IsMatch(value);

    public static bool IsPlayout(string? value) => BlueBillywigVideo.IsValidPlayout(value) && value!.Length <= 64;

    /// <summary>Custom field names the way <c>sanitize_key</c> writes them: lower case letters, digits, dash and underscore.</summary>
    public static string SanitizeKey(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        return new string(name.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-').ToArray());
    }

    public static string Title(string? text) => UploadValidation.SanitizeText(text, 255, multiline: false);

    public static string Description(string? text) => UploadValidation.SanitizeText(text, 4000, multiline: true);

    public static string FieldValue(string? text) => UploadValidation.SanitizeText(text, 1000, multiline: true);

    public static IReadOnlyList<string> Tags(IEnumerable<string>? tags) =>
        UploadValidation.ParseTags(tags is null ? null : string.Join(',', tags.Select(t => (t ?? "").Replace(',', ' '))));

    /// <summary>
    /// Validates the ids of a bulk request. The plugin's order: empty list is a 400, more than the cap is a 400,
    /// and the cap counts distinct, valid ids (a repeated id is one clip).
    /// </summary>
    public static bool TryParseBulkIds(IEnumerable<long>? raw, out List<long> ids, out string error)
    {
        ids = (raw ?? []).Where(i => i is > 0 and < MaxId).Distinct().ToList();
        error = "";
        if (ids.Count == 0)
        {
            error = "No clip IDs provided";
            return false;
        }

        if (ids.Count > MaxBulk)
        {
            error = "Please process in batches of 50 or fewer.";
            return false;
        }

        return true;
    }

    /// <summary>Validates one bulk action and its value. For cpp and folder the empty string is a valid value (clear / root).</summary>
    public static bool TryParseBulkValue(string? action, string? rawValue, out string value, out string error)
    {
        value = "";
        error = "";
        string trimmed = (rawValue ?? "").Trim();
        switch (action)
        {
            case "status":
                if (!IsStatus(trimmed))
                {
                    error = "Invalid status value";
                    return false;
                }

                break;
            case "cpp":
                if (trimmed.Length > 0 && !IsCppCode(trimmed))
                {
                    error = "Invalid CPP code";
                    return false;
                }

                break;
            case "folder":
                if (trimmed.Length > 0 && !IsFolderCode(trimmed))
                {
                    error = "Invalid folder code";
                    return false;
                }

                break;
            default:
                error = "Unknown bulk action";
                return false;
        }

        value = trimmed;
        return true;
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex CppPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2})?(Z|[+\-]\d{2}:?\d{2})?)?$")]
    private static partial Regex EmbargoPattern();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$")]
    private static partial Regex IsoPattern();
}

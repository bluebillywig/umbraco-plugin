using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueBillywig.Umbraco.Library;

/// <summary>A validated library query. Only <see cref="LibraryQuery.TryParse"/> builds one.</summary>
public sealed record LibraryFilter(string Search, string Folder, string SourceType, string MediaType, DateOnly? From, DateOnly? To, int Page);

/// <summary>
/// Validation and SAPI query building for the library lists. The folder and source type patterns are the
/// ones the reference plugin applies in <c>search_media_clips</c> and <c>load_library_videos</c> (2.0.1):
/// both values end up inside a Solr <c>fq</c>, so anything beyond a short code is refused, not cleaned.
/// </summary>
public static partial class LibraryQuery
{
    public const int MaxSearchLength = 200;

    /// <summary>
    /// The largest page the settings may ask for, and the largest <c>limit</c> sent to SAPI (the settings page validates
    /// 1 to this value too). 200 is the reference plugin's page size cap; the dev SAPI accepts it for <c>/sapi/mediaclip</c>.
    /// </summary>
    public const int MaxPageSize = 200;

    public const int MaxPage = 100_000;

    public static readonly string[] MediaTypes = ["all", "video", "audio", "image", "document"];

    /// <summary>Validates raw request values. On failure <paramref name="error"/> says which input was wrong.</summary>
    public static bool TryParse(string? search, string? folder, string? sourceType, string? mediaType, string? from, string? to, int? page, out LibraryFilter filter, out string error)
    {
        filter = new LibraryFilter("", "", "", "all", null, null, 0);
        error = "";

        string folderCode = (folder ?? "").Trim();
        if (folderCode.Length > 0 && !FolderPattern().IsMatch(folderCode))
        {
            error = "Invalid folder code";
            return false;
        }

        string source = (sourceType ?? "").Trim();
        if (source.Length > 0 && !SourceTypePattern().IsMatch(source))
        {
            error = "Invalid source type";
            return false;
        }

        string media = string.IsNullOrWhiteSpace(mediaType) ? "all" : mediaType.Trim().ToLowerInvariant();
        if (!MediaTypes.Contains(media))
        {
            error = "Invalid media type";
            return false;
        }

        if (!TryDate(from, out DateOnly? fromDate) || !TryDate(to, out DateOnly? toDate))
        {
            error = "Invalid date";
            return false;
        }

        if (fromDate is not null && toDate is not null && toDate < fromDate)
        {
            error = "The end date is before the start date";
            return false;
        }

        if (page is < 0 or > MaxPage)
        {
            error = "Invalid page";
            return false;
        }

        filter = new LibraryFilter(SanitizeSearch(search), folderCode, source, media, fromDate, toDate, page ?? 0);
        return true;
    }

    public static bool IsFolderCode(string? code) => code is not null && FolderPattern().IsMatch(code);

    /// <summary>The SAPI query for <c>/sapi/mediaclip</c>. Values are escaped or validated before they get here.</summary>
    public static Dictionary<string, string> ToSapiQuery(LibraryFilter filter, int pageSize)
    {
        int size = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = new Dictionary<string, string>
        {
            ["limit"] = size.ToString(CultureInfo.InvariantCulture),
            ["offset"] = ((long)filter.Page * size).ToString(CultureInfo.InvariantCulture),
            ["sort"] = "createddate desc",
        };

        if (filter.Search.Length > 0)
        {
            query["q"] = BuildSearchQuery(filter.Search);
        }

        // One fq joined with AND: the query dictionary cannot repeat the fq[] key, and the result is the same.
        List<string> filters = [];
        if (filter.Folder.Length > 0) filters.Add("foldercode_string:" + filter.Folder);
        if (filter.SourceType.Length > 0) filters.Add("sourcetype:" + filter.SourceType);
        if (filter.From is not null || filter.To is not null)
        {
            // Checked against bb.dev: this range includes a clip created inside it and excludes one outside.
            string start = filter.From is { } f ? f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z" : "*";
            string end = filter.To is { } t ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T23:59:59Z" : "*";
            filters.Add($"createddate:[{start} TO {end}]");
        }

        if (filters.Count > 0)
        {
            query["fq[]"] = string.Join(" AND ", filters);
        }

        if (filter.MediaType != "all")
        {
            query["filterset"] = "[" + JsonSerializer.Serialize(new
            {
                filters = new[] { new { type = "mediaclip", field = "mediatype", @operator = "is", value = filter.MediaType } },
            }) + "]";
        }

        return query;
    }

    /// <summary>The SAPI query for <c>/sapi/cliplist</c>. Playlists are filtered by title only.</summary>
    public static Dictionary<string, string> ToPlaylistQuery(LibraryFilter filter, int pageSize)
    {
        int size = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = new Dictionary<string, string>
        {
            ["limit"] = size.ToString(CultureInfo.InvariantCulture),
            ["offset"] = ((long)filter.Page * size).ToString(CultureInfo.InvariantCulture),
        };
        if (filter.Search.Length > 0)
        {
            query["filterset"] = "[" + JsonSerializer.Serialize(new
            {
                filters = new[] { new { type = "MediaClipList", field = "title", @operator = "contains", value = new[] { filter.Search } } },
            }) + "]";
        }

        return query;
    }

    /// <summary>Like <c>sanitize_text_field</c>: no tags, no control characters, collapsed whitespace, bounded length.</summary>
    public static string SanitizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return "";
        }

        string text = TagPattern().Replace(search, " ");
        text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        text = WhitespacePattern().Replace(text, " ").Trim();
        return text.Length > MaxSearchLength ? text[..MaxSearchLength].TrimEnd() : text;
    }

    /// <summary>
    /// The <c>q</c> value for a search box entry. SAPI reads an unquoted hyphenated word as several OR-ed words (checked
    /// 2026-11-06 on the development publication: <c>verify-10</c> and <c>verify\-10</c> matched 21 and 22 clips, the quoted phrase
    /// matched exactly 1), so a single hyphenated word is sent as a quoted phrase. Anything else keeps the escaping that
    /// the reference plugin applies. Inside the quotes only the quote and the backslash need escaping.
    /// </summary>
    public static string BuildSearchQuery(string search)
    {
        bool oneHyphenatedWord = search.Contains('-') && !search.Any(char.IsWhiteSpace);
        return oneHyphenatedWord
            ? "\"" + search.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : EscapeSolr(search);
    }

    /// <summary>Backslash-escapes Solr's special characters, the set the reference plugin escapes with <c>addcslashes</c>.</summary>
    public static string EscapeSolr(string text)
    {
        const string special = "+-&|!(){}[]^\"~*?:\\/";
        return string.Concat(text.Select(c => special.Contains(c) ? "\\" + c : c.ToString()));
    }

    private static bool TryDate(string? raw, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex FolderPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex SourceTypePattern();

    [GeneratedRegex("<[^>]*>?")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

using BlueBillywig.Umbraco.Library;

namespace BlueBillywig.Umbraco.Tests;

public class LibraryQueryTests
{
    private static LibraryFilter Parse(string? search = null, string? folder = null, string? source = null, string? media = null, string? from = null, string? to = null, int? page = null)
    {
        Assert.True(LibraryQuery.TryParse(search, folder, source, media, from, to, page, out LibraryFilter filter, out string error), error);
        return filter;
    }

    private static string Reject(string? folder = null, string? source = null, string? media = null, string? from = null, string? to = null, int? page = null)
    {
        Assert.False(LibraryQuery.TryParse(null, folder, source, media, from, to, page, out _, out string error));
        return error;
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("A_b-9")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")] // 64
    public void Folder_codes_that_look_like_codes_are_accepted(string code) => Assert.Equal(code, Parse(folder: code).Folder);

    [Theory]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("a OR b")]
    [InlineData("a)(b")]
    [InlineData("*")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a]")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65
    public void Folder_codes_that_could_change_the_solr_query_are_refused(string code) => Assert.Equal("Invalid folder code", Reject(folder: code));

    [Theory]
    [InlineData("live")]
    [InlineData("on_demand")]
    [InlineData("01234567890123456789012345678901")] // 32
    public void Source_types_are_accepted(string type) => Assert.Equal(type, Parse(source: type).SourceType);

    [Theory]
    [InlineData("live OR x")]
    [InlineData("live)")]
    [InlineData("a:b")]
    [InlineData("012345678901234567890123456789012")] // 33, the plugin's limit is 32
    public void Source_types_that_could_change_the_solr_query_are_refused(string type) => Assert.Equal("Invalid source type", Reject(source: type));

    [Theory]
    [InlineData("video")]
    [InlineData("AUDIO")]
    [InlineData("image")]
    [InlineData("document")]
    [InlineData("all")]
    [InlineData(null)]
    public void Known_media_types_pass(string? type) => Assert.Contains(Parse(media: type).MediaType, LibraryQuery.MediaTypes);

    [Theory]
    [InlineData("movie")]
    [InlineData("video\" or")]
    public void Unknown_media_types_are_refused(string type) => Assert.Equal("Invalid media type", Reject(media: type));

    [Theory]
    [InlineData("2026/11/01")]
    [InlineData("1-11-2026")]
    [InlineData("2026-13-01")]
    [InlineData("yesterday")]
    [InlineData("2026-11-01T00:00:00Z")]
    public void Dates_must_be_iso_days(string date)
    {
        Assert.Equal("Invalid date", Reject(from: date));
        Assert.Equal("Invalid date", Reject(to: date));
    }

    [Fact]
    public void A_range_that_ends_before_it_starts_is_refused() => Assert.Equal("The end date is before the start date", Reject(from: "2026-11-02", to: "2026-11-01"));

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void Pages_are_bounded(int page) => Assert.Equal("Invalid page", Reject(page: page));

    [Fact]
    public void Query_has_limit_offset_and_newest_first()
    {
        Dictionary<string, string> q = LibraryQuery.ToSapiQuery(Parse(page: 3), 15);

        Assert.Equal("15", q["limit"]);
        Assert.Equal("45", q["offset"]);
        Assert.Equal("createddate desc", q["sort"]);
        Assert.False(q.ContainsKey("q"));
        Assert.False(q.ContainsKey("fq[]"));
        Assert.False(q.ContainsKey("filterset"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, 200)]
    public void Page_size_is_capped(int requested, int expected) => Assert.Equal(expected.ToString(), LibraryQuery.ToSapiQuery(Parse(), requested)["limit"]);

    [Fact]
    public void Search_text_is_escaped_for_solr() => Assert.Equal(@"a\:b \(c\)", LibraryQuery.ToSapiQuery(Parse(search: "a:b (c)"), 10)["q"]);

    [Fact]
    public void Folder_source_and_date_become_one_fq()
    {
        Dictionary<string, string> q = LibraryQuery.ToSapiQuery(Parse(folder: "f1", source: "live", from: "2026-11-01", to: "2026-11-02"), 10);

        Assert.Equal("foldercode_string:f1 AND sourcetype:live AND createddate:[2026-11-01T00:00:00Z TO 2026-11-02T23:59:59Z]", q["fq[]"]);
    }

    [Fact]
    public void Open_ended_date_ranges_use_a_wildcard()
    {
        Assert.Equal("createddate:[2026-11-01T00:00:00Z TO *]", LibraryQuery.ToSapiQuery(Parse(from: "2026-11-01"), 10)["fq[]"]);
        Assert.Equal("createddate:[* TO 2026-11-01T23:59:59Z]", LibraryQuery.ToSapiQuery(Parse(to: "2026-11-01"), 10)["fq[]"]);
    }

    [Fact]
    public void Media_type_is_a_filterset_and_all_is_no_filter()
    {
        Assert.Contains("\"value\":\"audio\"", LibraryQuery.ToSapiQuery(Parse(media: "audio"), 10)["filterset"]);
        Assert.False(LibraryQuery.ToSapiQuery(Parse(media: "all"), 10).ContainsKey("filterset"));
    }

    [Fact]
    public void Playlist_query_filters_by_title_inside_json()
    {
        Dictionary<string, string> q = LibraryQuery.ToPlaylistQuery(Parse(search: "a\"b"), 10);

        Assert.Contains("\"field\":\"title\"", q["filterset"]);
        Assert.Contains("a\\u0022b", q["filterset"]); // quote escaped by the JSON encoder, not concatenated
    }

    [Fact]
    public void Folder_code_check_is_shared() => Assert.True(LibraryQuery.IsFolderCode("abc") && !LibraryQuery.IsFolderCode("a b") && !LibraryQuery.IsFolderCode(null));
}

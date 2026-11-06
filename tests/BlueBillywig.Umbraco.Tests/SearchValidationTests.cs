using BlueBillywig.Umbraco.Library;

namespace BlueBillywig.Umbraco.Tests;

public class SearchValidationTests
{
    [Theory]
    [InlineData("  hello   world ", "hello world")]
    [InlineData("<b>bold</b> text", "bold text")]
    [InlineData("a\u0000b\tc", "a b c")]
    [InlineData(null, "")]
    public void Search_text_is_sanitised(string? input, string expected) => Assert.Equal(expected, LibraryQuery.SanitizeSearch(input));

    [Fact]
    public void Search_text_is_bounded() => Assert.Equal(200, LibraryQuery.SanitizeSearch(new string('a', 500)).Length);

    [Fact]
    public void Solr_special_characters_are_escaped()
    {
        Assert.Equal(@"a\:b \(c\) \*", LibraryQuery.EscapeSolr("a:b (c) *"));
        Assert.Equal(@"\\\/", LibraryQuery.EscapeSolr(@"\/"));
    }

    [Fact]
    public void A_single_hyphenated_word_is_sent_as_one_quoted_phrase()
    {
        // SAPI reads an unquoted hyphenated word as several OR-ed words and matched most of the library; the quoted phrase
        // matches the clip (checked on the development publication, 2026-11-06).
        Assert.True(LibraryQuery.TryParse("umbraco-poc-test-b1", null, null, null, null, null, null, out LibraryFilter filter, out _));

        Dictionary<string, string> query = LibraryQuery.ToSapiQuery(filter, 15);

        Assert.Equal("\"umbraco-poc-test-b1\"", query["q"]);
        Assert.False(query.ContainsKey("fq[]"));
    }

    [Theory]
    [InlineData("verify", @"verify")]
    [InlineData("two words", "two words")]
    [InlineData("a-b c", @"a\-b c")]
    [InlineData("a:b", @"a\:b")]
    public void Anything_but_a_single_hyphenated_word_keeps_the_escaping(string search, string expected)
    {
        Assert.Equal(expected, LibraryQuery.BuildSearchQuery(search));
    }

    [Theory]
    [InlineData("a-\"b", "\"a-\\\"b\"")]
    [InlineData(@"a-\b", "\"a-\\\\b\"")]
    public void Quotes_and_backslashes_inside_a_quoted_phrase_are_escaped(string search, string expected)
    {
        Assert.Equal(expected, LibraryQuery.BuildSearchQuery(search));
    }
}

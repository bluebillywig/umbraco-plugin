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
    public void A_hyphenated_term_is_sent_as_one_escaped_q_with_nothing_else_added()
    {
        // Pins what goes to SAPI for a full hyphenated term (the reference plugin escapes it the same way). An install test saw
        // such a term return the unfiltered list while its parts filtered; whether that is SAPI's reading of "\-" is
        // documented in docs/USER-GUIDE.md as not confirmed.
        Assert.True(LibraryQuery.TryParse("umbraco-poc-test-b1", null, null, null, null, null, null, out LibraryFilter filter, out _));

        Dictionary<string, string> query = LibraryQuery.ToSapiQuery(filter, 15);

        Assert.Equal(@"umbraco\-poc\-test\-b1", query["q"]);
        Assert.False(query.ContainsKey("fq[]"));
    }
}

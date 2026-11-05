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
}

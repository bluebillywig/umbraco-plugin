using BlueBillywig.Umbraco.Embedding;

namespace BlueBillywig.Umbraco.Tests;

public class UrlTests
{
    [Theory]
    [InlineData("https://demo.bbvms.com/p/default/c/4567890.html", "demo", "default", 4567890)]
    [InlineData("http://demo.bbvms.com/p/my_play-out/c/12.html", "demo", "my_play-out", 12)]
    [InlineData("https://Demo-2.BBVMS.com/view/wide/55.html", "demo-2", "wide", 55)]
    [InlineData("https://demo.bbvms.com/p/default/c/9.html?autoplay=1#t=3", "demo", "default", 9)]
    public void Clip_shapes_match(string url, string publication, string playout, long id)
    {
        BlueBillywigVideo? v = BlueBillywigUrls.TryParseOEmbedUrl(url);

        Assert.NotNull(v);
        Assert.Equal(BlueBillywigVideoType.Clip, v.Type);
        Assert.Equal(publication, v.Publication);
        Assert.Equal(playout, v.Playout);
        Assert.Equal(id, v.Id);
        Assert.False(v.ShowTranscript);
    }

    [Fact]
    public void Channel_shape_matches()
    {
        BlueBillywigVideo? v = BlueBillywigUrls.TryParseOEmbedUrl("https://demo.bbvms.com/ch/31.html");

        Assert.Equal(BlueBillywigVideoType.Channel, v?.Type);
        Assert.Equal(31, v?.Id);
        Assert.False(v?.LazyLoad);
    }

    [Theory]
    [InlineData("https://demo.bbvms.com/p/default/c/12.js")]
    [InlineData("https://demo.bbvms.com/p/default/c/abc.html")]
    [InlineData("https://demo.bbvms.com/p/default/l/12.html")]
    [InlineData("https://demo.bbvms.com/sh/12.html")]
    [InlineData("https://demo.bbvms.com/ch/12.js")]
    [InlineData("https://demo.bbvms.com.evil.com/p/default/c/12.html")]
    [InlineData("https://evil.com/?u=https://demo.bbvms.com/p/default/c/12.html")]
    [InlineData("https://evil.com/demo.bbvms.com/p/default/c/12.html")]
    [InlineData("ftp://demo.bbvms.com/p/default/c/12.html")]
    [InlineData("https://bbvms.com/p/default/c/12.html")]
    [InlineData("https://demo.bbvms.com/p/de fault/c/12.html")]
    [InlineData("https://demo.bbvms.com/p/default/c/12.html extra")]
    [InlineData("https://demo.bbvms.com/p/default/c/0.html")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_shapes_do_not_match(string? url) => Assert.Null(BlueBillywigUrls.TryParseOEmbedUrl(url));

    [Fact]
    public void The_provider_exposes_exactly_the_three_reference_plugin_shapes()
    {
        Assert.Equal(3, BlueBillywigUrls.OEmbedPatterns.Length);
        Assert.Contains(BlueBillywigUrls.OEmbedPatterns, p => p.Contains("/p/") && p.Contains("/c/"));
        Assert.Contains(BlueBillywigUrls.OEmbedPatterns, p => p.Contains("/view/"));
        Assert.Contains(BlueBillywigUrls.OEmbedPatterns, p => p.Contains("/ch/"));
    }
}

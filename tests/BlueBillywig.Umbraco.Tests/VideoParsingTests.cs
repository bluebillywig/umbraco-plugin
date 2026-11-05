using BlueBillywig.Umbraco.Embedding;

namespace BlueBillywig.Umbraco.Tests;

public class VideoParsingTests
{
    [Fact]
    public void Clip_uses_the_reference_plugin_block_defaults()
    {
        BlueBillywigVideo? v = BlueBillywigVideo.Parse("""{"type":"clip","id":123}""");

        Assert.NotNull(v);
        Assert.Equal(BlueBillywigVideoType.Clip, v.Type);
        Assert.Equal(123, v.Id);
        Assert.True(v.ShowTranscript);
        Assert.False(v.ShowChapters);
        Assert.True(v.LazyLoad);
        Assert.Null(v.Playout);
    }

    [Fact]
    public void Channel_defaults_to_eager_and_600px()
    {
        BlueBillywigVideo? v = BlueBillywigVideo.Parse("""{"type":"channel","id":7}""");

        Assert.NotNull(v);
        Assert.False(v.LazyLoad);
        Assert.Equal("600px", v.Height);
    }

    [Fact]
    public void Shorts_default_to_700px_and_not_fullscreen()
    {
        BlueBillywigVideo? v = BlueBillywigVideo.Parse("""{"type":"shorts","id":9}""");

        Assert.NotNull(v);
        Assert.Equal("700px", v.Height);
        Assert.False(v.Fullscreen);
    }

    [Fact]
    public void Id_may_be_a_numeric_string_like_the_reference_plugin_attribute()
    {
        Assert.Equal(55, BlueBillywigVideo.Parse("""{"type":"clip","id":"55"}""")?.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"clip"}""")]
    [InlineData("""{"type":"clip","id":0}""")]
    [InlineData("""{"type":"clip","id":-4}""")]
    [InlineData("""{"type":"clip","id":"12abc"}""")]
    [InlineData("""{"type":"clip","id":1.5}""")]
    [InlineData("""{"type":"playlist","id":3}""")]
    [InlineData("""{"type":"clip","id":99999999999999999999}""")]
    public void Unusable_values_parse_to_null(string? json) => Assert.Null(BlueBillywigVideo.Parse(json));

    [Fact]
    public void Hostile_playout_height_and_publication_are_dropped()
    {
        BlueBillywigVideo? v = BlueBillywigVideo.Parse("""{"type":"channel","id":3,"playout":"../x","height":"600px;background:url(//evil)","publication":"a.evil.com/x"}""");

        Assert.NotNull(v);
        Assert.Null(v.Playout);
        Assert.Equal("600px", v.Height);
        Assert.Null(v.Publication);
    }

    [Fact]
    public void Json_round_trips_per_type()
    {
        var clip = new BlueBillywigVideo { Type = BlueBillywigVideoType.Clip, Id = 4, Playout = "wide", ShowChapters = true, LazyLoad = false };
        BlueBillywigVideo? back = BlueBillywigVideo.Parse(clip.ToJson());

        Assert.Equal(clip.Id, back?.Id);
        Assert.Equal("wide", back?.Playout);
        Assert.True(back?.ShowChapters);
        Assert.False(back?.LazyLoad);
        Assert.DoesNotContain("height", clip.ToJson());
    }

    [Fact]
    public void A_json_element_value_is_accepted()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"type":"shorts","id":2,"fullscreen":true}""");

        Assert.True(BlueBillywigVideo.Parse(doc.RootElement)?.Fullscreen);
    }
}

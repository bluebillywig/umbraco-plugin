using BlueBillywig.Umbraco.Embedding;

namespace BlueBillywig.Umbraco.Tests;

public class PathEscapingTests
{
    [Theory]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("../x", "..%2Fx")]
    [InlineData("a?b#c", "a%3Fb%23c")]
    [InlineData("plain-name_1", "plain-name_1")]
    public void Segment_escapes_separators(string input, string expected) => Assert.Equal(expected, BlueBillywigUrls.Segment(input));

    [Fact]
    public void Player_urls_cannot_leave_their_path()
    {
        string url = BlueBillywigUrls.ClipScript("https://demo.bbvms.com/", "../../admin", 5);

        Assert.Equal("https://demo.bbvms.com/p/..%2F..%2Fadmin/c/5.js", url);
    }

    [Fact]
    public async Task The_embed_code_request_escapes_the_embed_type_segment()
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, FakeSettings settings) = Rig.Create();
        settings.Value = settings.Value with { Embed = "../publication" };

        await Rig.Render(renderer, new BlueBillywigVideo { Id = 5 });

        Assert.Contains("/sapi/embedcode/5/default/..%2Fpublication", client.Requests);
        Assert.DoesNotContain(client.Requests, r => r.Contains("/../"));
    }
}

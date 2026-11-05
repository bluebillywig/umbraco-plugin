using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>E16 (chapters with clickable timestamps, opt-in), E3 (shorts) and the clickable-chapter wiring in the loader script.</summary>
public class ChaptersAndShortsTests
{
    private static async Task<string> Clip(string clipJson, bool chapters = true)
    {
        (BlueBillywigEmbedRenderer renderer, FakeClient client, _) = Rig.Create(http: new DefaultHttpContext());
        client.Json("/sapi/mediaclip/9", clipJson);
        return await Rig.Render(renderer, new BlueBillywigVideo { Id = 9, LazyLoad = false, ShowChapters = chapters, ShowTranscript = false });
    }

    [Fact]
    public async Task Chapters_are_off_unless_asked_for()
    {
        string html = await Clip("""{"id":9,"title":"T","chapters":[{"time":10,"title":"Intro"}]}""", chapters: false);

        Assert.DoesNotContain("bb-chapters", html);
        Assert.DoesNotContain("Intro", html);
    }

    [Fact]
    public async Task Each_chapter_is_a_link_with_its_time_and_the_list_names_the_clip()
    {
        string html = await Clip("""{"id":9,"title":"T","chapters":[{"time":0,"title":"Intro"},{"time":75,"title":"Main part"},{"time":3725,"title":"End"}]}""");

        Assert.Contains("data-bb-chapter-target=\"9\"", html);
        Assert.Contains("class=\"bb-chapter-link\" data-time=\"0\"", html);
        Assert.Contains("data-time=\"75\"", html);
        Assert.Contains("1:15</span> Main part", html);
        Assert.Contains("62:05</span> End", html);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chapters_stored_as_a_json_string_work_too_and_bad_ones_are_skipped()
    {
        string html = await Clip("""{"id":9,"chapters":"[{\"time\":\"20\",\"title\":\"Stringy\"},{\"time\":-4,\"title\":\"Early\"},{\"time\":5,\"title\":\"\"},{\"time\":7}]"}""");

        Assert.Contains("data-time=\"20\"", html);
        Assert.Contains("Stringy", html);
        Assert.Contains("data-time=\"0\"", html); // negative time clamps to the start
        Assert.Contains("Early", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "bb-chapter-link").Count);
    }

    [Theory]
    [InlineData("""{"id":9,"chapters":[]}""")]
    [InlineData("""{"id":9,"chapters":"not json"}""")]
    [InlineData("""{"id":9}""")]
    public async Task No_usable_chapters_means_no_chapter_block(string clipJson) =>
        Assert.DoesNotContain("bb-chapters", await Clip(clipJson));

    [Fact]
    public async Task Chapter_titles_from_sapi_are_encoded()
    {
        string html = await Clip("""{"id":9,"chapters":[{"time":1,"title":"<img src=x onerror=alert(1)>"}]}""");

        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void The_loader_handles_chapter_clicks_from_data_attributes_without_inline_script()
    {
        string js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../src/BlueBillywig.Umbraco/Client/public/bb-lazy.js"));

        Assert.Contains("closest( '.bb-chapter-link' )", js);
        Assert.Contains("data-bb-chapter-target", js);
        Assert.Contains("bbSeek", js);
    }

    [Fact]
    public async Task Shorts_render_as_a_sandbox_free_iframe_to_the_shorts_page_and_respect_the_fullscreen_switch()
    {
        (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(http: new DefaultHttpContext());

        string narrow = await Rig.Render(renderer, new BlueBillywigVideo { Type = BlueBillywigVideoType.Shorts, Id = 31, Height = "640px" });
        string wide = await Rig.Render(renderer, new BlueBillywigVideo { Type = BlueBillywigVideoType.Shorts, Id = 31, Height = "640px", Fullscreen = true });

        Assert.Contains("src=\"https://demo.bbvms.com/sh/31.html\"", narrow);
        Assert.Contains("max-width:400px;height:640px", narrow);
        Assert.Contains("width:100%;height:640px;border:none;\"", wide);
        Assert.DoesNotContain("max-width", wide);
    }
}

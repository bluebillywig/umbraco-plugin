using System.Text.Json.Nodes;
using System.Xml.Linq;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Seo;

namespace BlueBillywig.Umbraco.Tests.Seo;

/// <summary>Q3. The sitemap must parse back as XML, follow the Google video schema, and contain no unescaped input.</summary>
public class VideoSitemapTests
{
    private static readonly XNamespace Sm = VideoSitemap.SitemapNamespace;
    private static readonly XNamespace Vd = VideoSitemap.VideoNamespace;

    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);
    private static readonly string LoneHigh = ((char)0xD800).ToString();
    private static readonly string LoneLow = ((char)0xDC00).ToString();
    private static readonly string Ctl = ((char)0x0001).ToString();

    private static SitemapVideo Video(string title = "Title", string description = "Description", string thumb = "https://assets.example.com/t.jpg", string player = "https://pub.bbvms.com/p/default/c/42.html", int? duration = 125, DateTimeOffset? published = null) =>
        new(title, description, thumb, player, duration, published ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private static XDocument Parse(string xml) => XDocument.Parse(xml);

    private static string Write(params SitemapPage[] pages) => VideoSitemap.Write(pages);

    [Fact]
    public void A_page_with_a_video_produces_the_google_video_sitemap_structure_in_schema_order()
    {
        string xml = Write(new SitemapPage("https://site.example/news/story", [Video()], new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero)));
        XDocument doc = Parse(xml);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Equal(Sm + "urlset", doc.Root!.Name);
        Assert.Equal("video", doc.Root.GetPrefixOfNamespace(Vd));

        XElement url = Assert.Single(doc.Root.Elements());
        Assert.Equal(["loc", "lastmod", "video"], url.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("https://site.example/news/story", url.Element(Sm + "loc")!.Value);
        Assert.Equal("2026-02-03T04:05:06Z", url.Element(Sm + "lastmod")!.Value);

        XElement video = url.Element(Vd + "video")!;
        Assert.Equal(["thumbnail_loc", "title", "description", "player_loc", "duration", "publication_date"], video.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.All(video.Elements(), e => Assert.Equal(Vd, e.Name.Namespace));
        Assert.Equal("https://assets.example.com/t.jpg", video.Element(Vd + "thumbnail_loc")!.Value);
        Assert.Equal("Title", video.Element(Vd + "title")!.Value);
        Assert.Equal("Description", video.Element(Vd + "description")!.Value);
        Assert.Equal("https://pub.bbvms.com/p/default/c/42.html", video.Element(Vd + "player_loc")!.Value);
        Assert.Equal("125", video.Element(Vd + "duration")!.Value);
        Assert.Equal("2026-01-02T03:04:05Z", video.Element(Vd + "publication_date")!.Value);
    }

    [Fact]
    public void An_empty_sitemap_is_a_valid_empty_urlset()
    {
        XDocument doc = Parse(Write());
        Assert.Equal(Sm + "urlset", doc.Root!.Name);
        Assert.Empty(doc.Root.Elements());
    }

    [Fact]
    public void Hostile_text_is_escaped_and_parses_back_unchanged()
    {
        const string title = "</title><script>alert(1)</script> & \"q\" 'a' <![CDATA[x]]> ]]>";
        const string description = "<!-- c --> &amp; &lt;b&gt; <video:video> </video:video></url></urlset>";
        const string url = "https://site.example/a?x=1&y=<2>&z=\"3\"";
        string xml = Write(new SitemapPage("https://site.example/p?a=1&b=2", [Video(title, description, thumb: "https://assets.example.com/t.jpg?a=1&b=2", player: "https://pub.bbvms.com/p/default/c/42.html?x=1&y=2")]));

        // The input is not present as markup anywhere in the document.
        Assert.DoesNotContain("<script>", xml);
        Assert.DoesNotContain("<!-- c -->", xml);
        Assert.DoesNotContain("<![CDATA[x]]>", xml);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xml, "</urlset>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xml, "</url>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xml, "<video:video>"));
        Assert.DoesNotContain("a=1&b=2", xml); // '&' only ever appears as '&amp;'
        Assert.DoesNotContain(url, xml);

        XElement video = Parse(xml).Root!.Element(Sm + "url")!.Element(Vd + "video")!;
        Assert.Equal(title, video.Element(Vd + "title")!.Value);
        Assert.Equal(description, video.Element(Vd + "description")!.Value);
        Assert.Equal("https://assets.example.com/t.jpg?a=1&b=2", video.Element(Vd + "thumbnail_loc")!.Value);
        Assert.Equal("https://pub.bbvms.com/p/default/c/42.html?x=1&y=2", video.Element(Vd + "player_loc")!.Value);
        Assert.Equal("https://site.example/p?a=1&b=2", Parse(xml).Root!.Element(Sm + "url")!.Element(Sm + "loc")!.Value);
    }

    [Fact]
    public void Characters_xml_cannot_carry_are_removed_instead_of_throwing()
    {
        string title = "a" + Ctl + "b" + LoneHigh + "c" + LoneLow + "d\u0008e￾" + Emoji + "\ttab";
        string xml = Write(new SitemapPage("https://site.example/p", [Video(title: title, description: "x" + Ctl + "y" + LoneHigh)]));
        XElement video = Parse(xml).Root!.Element(Sm + "url")!.Element(Vd + "video")!;
        Assert.Equal("abcde" + Emoji + "\ttab", video.Element(Vd + "title")!.Value);
        Assert.Equal("xy", video.Element(Vd + "description")!.Value);
    }

    [Fact]
    public void A_title_or_description_that_is_nothing_but_invalid_characters_is_not_a_video()
    {
        Assert.Empty(Parse(Write(new SitemapPage("https://site.example/p", [Video(title: Ctl + LoneHigh)]))).Root!.Elements());
        // An empty description falls back to the title.
        XElement video = Parse(Write(new SitemapPage("https://site.example/p", [Video(description: Ctl)]))).Root!.Element(Sm + "url")!.Element(Vd + "video")!;
        Assert.Equal("Title", video.Element(Vd + "description")!.Value);
    }

    [Fact]
    public void Limits_follow_the_google_documentation_and_never_split_a_surrogate_pair()
    {
        string longTitle = new string('t', 99) + Emoji + "tail";
        string longDescription = new string('d', 2047) + Emoji + "tail";
        string xml = Write(new SitemapPage("https://site.example/p", [Video(title: longTitle, description: longDescription)]));
        XElement video = Parse(xml).Root!.Element(Sm + "url")!.Element(Vd + "video")!;
        string title = video.Element(Vd + "title")!.Value;
        string description = video.Element(Vd + "description")!.Value;
        Assert.True(title.Length <= VideoSitemap.MaxTitle, title.Length.ToString());
        Assert.True(description.Length <= VideoSitemap.MaxDescription);
        Assert.Equal(new string('t', 99), title);
        Assert.Equal(new string('d', 2047), description);
        Assert.Equal(100, Video(title: new string('x', 500)).Title.Length == 500 ? 100 : 0); // sanity of the helper
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(28800, true)]
    [InlineData(28801, false)]
    public void Duration_is_one_to_28800_seconds_or_left_out(int seconds, bool written)
    {
        XElement video = Parse(Write(new SitemapPage("https://site.example/p", [Video(duration: seconds)]))).Root!.Element(Sm + "url")!.Element(Vd + "video")!;
        Assert.Equal(written, video.Element(Vd + "duration") is not null);
        if (written)
        {
            Assert.Equal(seconds.ToString(), video.Element(Vd + "duration")!.Value);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative/thumb.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("ftp://example.com/t.jpg")]
    [InlineData("https://example.com/a b.jpg")]
    [InlineData("https:///nohost")]
    public void A_video_without_a_usable_thumbnail_is_left_out_because_google_requires_it(string thumb)
    {
        Assert.Empty(Parse(Write(new SitemapPage("https://site.example/p", [Video(thumb: thumb)]))).Root!.Elements());
    }

    [Theory]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//pub.bbvms.com/p/default/c/1.html")]
    public void A_video_without_a_usable_player_url_is_left_out(string player)
    {
        Assert.Empty(Parse(Write(new SitemapPage("https://site.example/p", [Video(player: player)]))).Root!.Elements());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/news/story")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://site.example/a b")]
    [InlineData("https://site.example/<x>\"")]
    public void A_page_url_that_is_not_absolute_http_is_left_out(string page)
    {
        string xml = Write(new SitemapPage(page, [Video()]));
        if (page.Contains('<'))
        {
            // Allowed by Uri, so it must at least be escaped.
            Assert.DoesNotContain("<x>", xml);
            Parse(xml);
            return;
        }

        Assert.Empty(Parse(xml).Root!.Elements());
    }

    [Fact]
    public void Pages_without_any_valid_video_are_not_listed_and_mixed_pages_keep_only_the_valid_ones()
    {
        string xml = Write(
            new SitemapPage("https://site.example/none", []),
            new SitemapPage("https://site.example/bad", [Video(thumb: "")]),
            new SitemapPage("https://site.example/mixed", [Video(thumb: ""), Video(title: "Good", player: "https://pub.bbvms.com/p/default/c/2.html"), Video(title: "Also good")]));
        XDocument doc = Parse(xml);
        XElement url = Assert.Single(doc.Root!.Elements());
        Assert.Equal("https://site.example/mixed", url.Element(Sm + "loc")!.Value);
        Assert.Equal(["Good", "Also good"], url.Elements(Vd + "video").Select(v => v.Element(Vd + "title")!.Value).ToArray());
    }

    [Fact]
    public void Videos_per_page_are_capped_at_a_thousand()
    {
        var videos = Enumerable.Range(1, 1100).Select(i => Video(title: "V" + i)).ToList();
        XElement url = Assert.Single(Parse(Write(new SitemapPage("https://site.example/p", videos))).Root!.Elements());
        Assert.Equal(1000, url.Elements(Vd + "video").Count());
    }

    [Fact]
    public void Descriptions_default_to_the_title_like_the_plugin()
    {
        JsonNode clip = JsonNode.Parse("""{"id":42,"title":"Only a title","description":"","length":"61.7","createddate":"2026-03-04T05:06:07Z"}""")!;
        SitemapVideo video = VideoSitemap.FromClip(clip, BlueBillywigUrls.ClipPage("https://pub.bbvms.com", "default", 42), "https://assets.example.com/t.jpg")!;
        Assert.Equal("Only a title", video.Description);
        Assert.Equal("https://pub.bbvms.com/p/default/c/42.html", video.PlayerUrl);
        Assert.Equal(61, video.DurationSeconds);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), video.PublicationDate);
    }

    [Theory]
    [InlineData("2026-03-04T05:06:07Z", "2026-03-04T05:06:07Z")]
    [InlineData("2026-03-04T07:06:07+02:00", "2026-03-04T05:06:07Z")]
    [InlineData("2026-03-04 05:06:07", "2026-03-04T05:06:07Z")]
    public void Publication_dates_are_written_as_utc_iso_8601(string created, string expected)
    {
        JsonNode clip = JsonNode.Parse($$"""{"id":42,"title":"T","createddate":"{{created}}"}""")!;
        SitemapVideo video = VideoSitemap.FromClip(clip, "https://pub.bbvms.com/p/default/c/42.html", "https://assets.example.com/t.jpg")!;
        XElement el = Parse(Write(new SitemapPage("https://site.example/p", [video]))).Root!.Element(Sm + "url")!.Element(Vd + "video")!;
        Assert.Equal(expected, el.Element(Vd + "publication_date")!.Value);
    }

    [Fact]
    public void A_missing_or_unparseable_date_is_left_out()
    {
        foreach (string json in new[] { """{"id":1,"title":"T"}""", """{"id":1,"title":"T","createddate":"yesterday"}""" })
        {
            SitemapVideo video = VideoSitemap.FromClip(JsonNode.Parse(json)!, "https://pub.bbvms.com/p/default/c/1.html", "https://assets.example.com/t.jpg")!;
            Assert.Null(video.PublicationDate);
            XElement el = Parse(Write(new SitemapPage("https://site.example/p", [video]))).Root!.Element(Sm + "url")!.Element(Vd + "video")!;
            Assert.Null(el.Element(Vd + "publication_date"));
        }
    }

    [Fact]
    public void A_clip_without_a_title_or_thumbnail_gives_no_video()
    {
        Assert.Null(VideoSitemap.FromClip(JsonNode.Parse("""{"id":1}""")!, "https://pub.bbvms.com/p/default/c/1.html", "https://assets.example.com/t.jpg"));
        Assert.Null(VideoSitemap.FromClip(JsonNode.Parse("""{"id":1,"title":"T"}""")!, "https://pub.bbvms.com/p/default/c/1.html", null));
        Assert.Null(VideoSitemap.FromClip(JsonNode.Parse("""{"id":1,"title":"T"}""")!, "https://pub.bbvms.com/p/default/c/1.html", ""));
    }

    [Fact]
    public void Property_loop_over_hostile_text_always_gives_well_formed_xml_that_round_trips()
    {
        string[] atoms = ["<", ">", "&", "'", "\"", "]]>", "<![CDATA[", "-->", "<!--", "/", "script", " ", "\n", "\t", Ctl, "\u0008", LoneHigh, LoneLow, Emoji, "é", "a", "￾", "￿", "&amp;", "&#0;"];
        var random = new Random(20260504);
        for (int i = 0; i < 2000; i++)
        {
            string Make(int max) => string.Concat(Enumerable.Range(0, random.Next(1, max)).Select(_ => atoms[random.Next(atoms.Length)]));
            string title = Make(12), description = Make(12);
            string xml = Write(new SitemapPage("https://site.example/p?" + random.Next(), [Video(title, description)]));
            XDocument doc = Parse(xml); // throws if the document is not well formed

            string expectedTitle = VideoSitemap.Truncate(VideoSitemap.XmlClean(title).Trim(), VideoSitemap.MaxTitle);
            XElement? video = doc.Root!.Element(Sm + "url")?.Element(Vd + "video");
            if (expectedTitle.Length == 0)
            {
                Assert.Null(video);
                continue;
            }

            Assert.NotNull(video);
            Assert.Equal(expectedTitle, video.Element(Vd + "title")!.Value.Replace("\r\n", "\n"));
            Assert.DoesNotContain("<script", xml);
        }
    }
}

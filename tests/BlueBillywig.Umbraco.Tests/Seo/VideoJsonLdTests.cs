using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Tests.Gate;
using BlueBillywig.Umbraco.Seo;

namespace BlueBillywig.Umbraco.Tests.Seo;

/// <summary>Q1 and Q2. Ports JsonLdEscapingTest (ADO 19070, plugin 2.0.3) and attacks the encoder with hostile text.</summary>
public class VideoJsonLdTests
{
    // Built from code points so no editor or tool can turn them into raw characters on the way.
    private static readonly string LineSep = ((char)0x2028).ToString();
    private static readonly string ParaSep = ((char)0x2029).ToString();
    private static readonly string LoneHigh = ((char)0xD800).ToString();
    private static readonly string LoneLow = ((char)0xDC00).ToString();
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static VideoJsonLdInput Input(string title = "Title", string? description = "Description", string? transcript = null) =>
        new("42", title, description, "2026-01-02T03:04:05Z", "https://assets.example.com/thumb.jpg", 125.9, transcript);

    private static string Script(VideoJsonLdInput input, ListLogger? log = null, string? nonce = null) =>
        VideoJsonLd.Render(input, nonce, log) ?? throw new Xunit.Sdk.XunitException("nothing was emitted");

    private static string Body(string script)
    {
        Match m = Regex.Match(script, "^<script type=\"application/ld\\+json\"(?: nonce=\"[^\"]*\")?>(.*)</script>$", RegexOptions.Singleline);
        Assert.True(m.Success, script);
        return m.Groups[1].Value;
    }

    // JsonLdEscapingTest: the encoded value is emitted inside the application/ld+json script element --------------

    [Fact]
    public void The_encoded_json_is_the_content_of_one_application_ld_json_script_element()
    {
        string script = Script(Input());
        Assert.StartsWith("<script type=\"application/ld+json\">", script);
        Assert.EndsWith("</script>", script);
        JsonNode ld = JsonNode.Parse(Body(script))!;
        Assert.Equal("VideoObject", (string?)ld["@type"]);
    }

    [Fact]
    public void Hex_escaping_neutralises_the_script_breakout()
    {
        // The plugin test's payload.
        var input = new VideoJsonLdInput("1", "</script><img src=x onerror=alert(1)>", "A & B \"quoted\" 'apostrophes'", null, null, null, null);
        string body = Body(Script(input));

        Assert.DoesNotContain("</script>", body);
        Assert.DoesNotContain("<img", body);
        Assert.DoesNotContain("<", body);
        Assert.DoesNotContain(">", body);
        Assert.DoesNotContain("&", body);
        Assert.DoesNotContain("'", body);

        // Still valid JSON, and the data survives.
        JsonNode decoded = JsonNode.Parse(body)!;
        Assert.Equal(input.Title, (string?)decoded["name"]);
        Assert.Equal(input.Description, (string?)decoded["description"]);
    }

    [Fact]
    public void The_escapes_are_unicode_escapes_not_removals()
    {
        string body = Body(Script(Input(title: "<a>&'\"")));
        Assert.Contains("\\u003C", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003E", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u0026", body);
        Assert.Contains("\\u0027", body);
        Assert.Contains("\\u0022", body);
    }

    public static TheoryData<string> Hostile =>
    [
        "</script>",
        "</SCRIPT >",
        "</script\t\n/><script>alert(1)</script>",
        "<!--",
        "<!-- <script>",
        "-->",
        "<script>alert(1)</script>",
        "]]>",
        "&amp; &lt;/script&gt; &#60;/script&#62;",
        "it's \"quoted\" `tick` ${x} {{x}}",
        "\\\"; alert(1); //",
        "line\nbreak\r\nand\ttab",
        "null\0byte",
        "café 中文 مرحبا",
        "+ADw-script+AD4- utf-7",
        "%3C/script%3E",
        "</script>",
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_text_in_title_description_and_transcript_cannot_end_the_script_element(string text)
    {
        foreach (VideoJsonLdInput input in new[] { Input(title: text), Input(description: text), Input(transcript: text), Input(title: text, description: text, transcript: text) })
        {
            string script = Script(input);
            AssertSealed(script);
            JsonNode ld = JsonNode.Parse(Body(script))!;
            if (input.Title == text)
            {
                Assert.Equal(text, (string?)ld["name"]);
            }
        }
    }

    [Fact]
    public void Line_and_paragraph_separators_and_astral_characters_are_escaped_and_round_trip()
    {
        string text = "a" + LineSep + "b" + ParaSep + "c" + Emoji;
        string body = Body(Script(Input(title: text, description: text)));
        Assert.DoesNotContain(LineSep, body);
        Assert.DoesNotContain(ParaSep, body);
        Assert.Contains("\\u2028", body);
        Assert.Contains("\\u2029", body);
        Assert.Equal(text, (string?)JsonNode.Parse(body)!["name"]);
        Assert.All(body, c => Assert.True(c < 0x80, "ASCII only"));
    }

    [Fact]
    public void Property_loop_over_random_hostile_strings_never_leaves_an_unsafe_character()
    {
        string[] atoms = ["<", ">", "&", "'", "\"", "/", "\\", "script", "SCRIPT", "!--", "-->", "\n", "\r", "\t", " ", LineSep, ParaSep, Emoji, "é", "a", "0", "\u0001", LoneHigh, LoneLow];
        var random = new Random(20260504);
        int emitted = 0, refused = 0;
        for (int i = 0; i < 3000; i++)
        {
            string title = string.Concat(Enumerable.Range(0, random.Next(1, 14)).Select(_ => atoms[random.Next(atoms.Length)]));
            string description = string.Concat(Enumerable.Range(0, random.Next(0, 14)).Select(_ => atoms[random.Next(atoms.Length)]));
            bool wellFormed = VideoJsonLd.IsWellFormed(title) && VideoJsonLd.IsWellFormed(description);

            string? script = VideoJsonLd.Render(Input(title: title, description: description));
            if (!wellFormed)
            {
                Assert.Null(script);
                refused++;
                continue;
            }

            if (string.IsNullOrEmpty(title) || title == "0")
            {
                Assert.Null(script);
                continue;
            }

            Assert.NotNull(script);
            emitted++;
            AssertSealed(script);
            JsonNode ld = JsonNode.Parse(Body(script))!;
            Assert.Equal(title, (string?)ld["name"]);
            Assert.Equal(string.IsNullOrEmpty(description) || description == "0" ? title : description, (string?)ld["description"]);
        }

        Assert.True(emitted > 1000 && refused > 100, $"emitted={emitted} refused={refused}");
    }

    /// <summary>Exactly one opening and one closing tag, no comment opener, and nothing the HTML parser or a JS engine would treat specially.</summary>
    private static void AssertSealed(string script)
    {
        Assert.Equal(2, Regex.Matches(script, "<").Count); // the opening tag and the closing "</script>"
        Assert.Single(Regex.Matches(script, "</script>", RegexOptions.IgnoreCase));
        Assert.DoesNotContain("<!--", script);
        string body = Body(script);
        Assert.DoesNotContain("<", body);
        Assert.DoesNotContain(">", body);
        Assert.DoesNotContain("&", body);
        Assert.DoesNotContain("'", body);
        Assert.DoesNotContain(LineSep, body);
        Assert.DoesNotContain(ParaSep, body);
    }

    // Nothing is emitted when encoding fails ---------------------------------------------------------------------

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("transcript")]
    public void Invalid_utf16_emits_nothing_and_logs_why(string field)
    {
        string bad = "ok " + LoneHigh + " ok";
        var log = new ListLogger();
        VideoJsonLdInput input = field switch
        {
            "title" => Input(title: bad),
            "description" => Input(description: bad),
            _ => Input(transcript: bad),
        };

        Assert.Null(VideoJsonLd.Render(input, null, log));
        Assert.True(log.Logged("JSON-LD encoding failed for clip 42"));
        Assert.True(log.Logged("invalid UTF-16"));
    }

    [Theory]
    [InlineData("a", "high at end")]
    [InlineData("b", "low alone")]
    [InlineData("c", "reversed pair")]
    public void Every_shape_of_unpaired_surrogate_is_refused(string kind, string label)
    {
        string bad = kind switch
        {
            "a" => "x" + LoneHigh,
            "b" => LoneLow + "x",
            _ => LoneLow + LoneHigh,
        };
        Assert.Null(VideoJsonLd.Render(Input(title: bad)));
        Assert.False(string.IsNullOrEmpty(label));
        // A correctly paired surrogate is fine.
        Assert.NotNull(VideoJsonLd.Render(Input(title: "x" + Emoji)));
    }

    [Fact]
    public void Encode_returns_null_for_an_object_with_an_invalid_key_or_nested_value()
    {
        var ld = new JsonObject { ["ok"] = "fine", ["nested"] = new JsonObject { ["x"] = LoneHigh } };
        Assert.Null(VideoJsonLd.Encode(ld));
        var keyed = new JsonObject { [LoneHigh] = "v" };
        Assert.Null(VideoJsonLd.Encode(keyed));
        Assert.NotNull(VideoJsonLd.Encode(new JsonObject { ["ok"] = "fine" }));
    }

    // Content (Q1) ------------------------------------------------------------------------------------------------

    [Fact]
    public void Fields_and_key_order_follow_the_plugin()
    {
        string body = Body(Script(Input(transcript: "<p>Hello <b>world</b></p><script>evil()</script><!-- c --> again")));
        var ld = (JsonObject)JsonNode.Parse(body)!;
        Assert.Equal(["@context", "@type", "name", "description", "uploadDate", "thumbnailUrl", "duration", "transcript"], ld.Select(p => p.Key).ToArray());
        Assert.Equal("https://schema.org", (string?)ld["@context"]);
        Assert.Equal("VideoObject", (string?)ld["@type"]);
        Assert.Equal("Title", (string?)ld["name"]);
        Assert.Equal("Description", (string?)ld["description"]);
        Assert.Equal("2026-01-02T03:04:05Z", (string?)ld["uploadDate"]);
        Assert.Equal("https://assets.example.com/thumb.jpg", (string?)ld["thumbnailUrl"]);
        Assert.Equal("PT125S", (string?)ld["duration"]);
        Assert.Equal("Hello world again", (string?)ld["transcript"]);
        Assert.Contains("https://assets.example.com/thumb.jpg", body); // slashes are not escaped (JSON_UNESCAPED_SLASHES)
    }

    [Fact]
    public void A_clip_without_a_title_has_no_json_ld()
    {
        Assert.Null(VideoJsonLd.Render(Input(title: "")));
        Assert.Null(VideoJsonLd.Render(Input(title: "0")));
    }

    [Fact]
    public void The_description_falls_back_to_the_title_and_optional_parts_are_left_out()
    {
        var ld = (JsonObject)JsonNode.Parse(Body(Script(new VideoJsonLdInput("1", "Only title", "", null, null, null, ""))))!;
        Assert.Equal(["@context", "@type", "name", "description"], ld.Select(p => p.Key).ToArray());
        Assert.Equal("Only title", (string?)ld["description"]);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.9, false)]
    [InlineData(-5.0, false)]
    [InlineData(1.0, true)]
    [InlineData(59.99, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Duration_is_written_only_for_a_positive_whole_second(double length, bool written)
    {
        var ld = (JsonObject)JsonNode.Parse(Body(Script(new VideoJsonLdInput("1", "T", null, null, null, length, null))))!;
        Assert.Equal(written, ld.ContainsKey("duration"));
    }

    [Fact]
    public void A_transcript_that_is_only_markup_is_left_out()
    {
        var ld = (JsonObject)JsonNode.Parse(Body(Script(Input(transcript: "<br/><script>x</script><!-- y -->")))) !;
        Assert.False(ld.ContainsKey("transcript"));
    }

    [Fact]
    public void Embed_types_follow_the_plugin()
    {
        foreach (string type in new[] { "iframe", "oembed", "url", "amp" })
        {
            Assert.True(VideoJsonLd.ShouldEmitFor(type));
        }

        foreach (string? type in new[] { "javascript", "", null, "IFRAME", "script" })
        {
            Assert.False(VideoJsonLd.ShouldEmitFor(type));
        }
    }

    [Fact]
    public void A_sapi_clip_maps_to_the_input()
    {
        JsonNode clip = JsonNode.Parse("""{"id":"77","title":"T","description":"","createddate":"2026-03-04T05:06:07Z","length":"61.5","transcript":"<i>x</i>"}""")!;
        VideoJsonLdInput input = VideoJsonLd.FromClip(clip, "https://assets.example.com/t.jpg");
        Assert.Equal("77", input.ClipId);
        Assert.Equal(61.5, input.LengthSeconds);
        var ld = (JsonObject)JsonNode.Parse(Body(Script(input)))!;
        Assert.Equal("T", (string?)ld["description"]);
        Assert.Equal("PT61S", (string?)ld["duration"]);
        Assert.Equal("x", (string?)ld["transcript"]);
        Assert.Null(VideoJsonLd.FromClip(JsonNode.Parse("""{"title":"T"}""")!, null).LengthSeconds);
    }

    // The nonce ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_valid_nonce_is_added_and_a_hostile_one_is_dropped_not_echoed()
    {
        Assert.StartsWith("<script type=\"application/ld+json\" nonce=\"abc123+/=_-\">", Script(Input(), nonce: "abc123+/=_-"));

        foreach (string nonce in new[] { "\"><script>alert(1)</script>", "a b", "a\"b", "x'y", "<", "é", new string('a', 129) })
        {
            string script = Script(Input(), nonce: nonce);
            Assert.StartsWith("<script type=\"application/ld+json\">", script);
            Assert.DoesNotContain("nonce", script);
        }

        Assert.StartsWith("<script type=\"application/ld+json\">", Script(Input(), nonce: ""));
        Assert.StartsWith("<script type=\"application/ld+json\">", Script(Input(), nonce: null));
    }
}

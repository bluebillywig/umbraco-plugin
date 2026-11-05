using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Editing;
using Microsoft.Extensions.Caching.Memory;

namespace BlueBillywig.Umbraco.Tests;

internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class Img
{
    /// <summary>The header bytes of a PNG: signature and IHDR. Enough for the validator, which reads the size from them.</summary>
    public static byte[] Png(int w, int h)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange(BigEndian(w, 4));
        bytes.AddRange(BigEndian(h, 4));
        bytes.AddRange([8, 2, 0, 0, 0, 0, 0, 0, 0]);
        return [.. bytes];
    }

    /// <summary>A JPEG with an APP0 segment before the frame header, as real files have. <paramref name="sof"/> 0xC2 is progressive.</summary>
    public static byte[] Jpeg(int w, int h, byte sof = 0xC0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
        bytes.AddRange([0xFF, 0xC4, 0, 3, 0]); // a DHT segment is not a frame header
        bytes.AddRange([0xFF, sof, 0, 17, 8]);
        bytes.AddRange(BigEndian(h, 2));
        bytes.AddRange(BigEndian(w, 2));
        bytes.AddRange([3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        return [.. bytes];
    }

    public static byte[] Gif(int w, int h) => [.. "GIF89a"u8.ToArray(), (byte)(w & 255), (byte)(w >> 8), (byte)(h & 255), (byte)(h >> 8), 0, 0, 0];

    public static byte[] Webp() => [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBPVP8 "u8.ToArray(), 0, 0, 0, 0];

    public static string Uri(string declared, byte[] bytes) => $"data:image/{declared};base64,{Convert.ToBase64String(bytes)}";

    private static IEnumerable<byte> BigEndian(int value, int size) => Enumerable.Range(0, size).Select(i => (byte)(value >> (8 * (size - 1 - i))));
}

/// <summary>Port of the plugin's ThumbnailValidatorTest.</summary>
public class ThumbnailValidatorTests
{
    private const string TypeMessage = "Only JPG and PNG images can be used as a thumbnail.";

    [Fact]
    public void Jpeg_and_png_pass()
    {
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("jpeg", Img.Jpeg(160, 90))));
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(160, 90))));
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("jpeg", Img.Jpeg(160, 90, 0xC2))));
    }

    [Fact]
    public void Gif_and_webp_are_refused()
    {
        Assert.Equal(TypeMessage, ThumbnailValidator.Validate(Img.Uri("gif", Img.Gif(16, 9))));
        Assert.Equal(TypeMessage, ThumbnailValidator.Validate(Img.Uri("webp", Img.Webp())));
    }

    [Fact]
    public void A_renamed_gif_is_refused_because_the_contents_decide()
    {
        Assert.Equal(TypeMessage, ThumbnailValidator.Validate(Img.Uri("png", Img.Gif(16, 9))));
        Assert.Equal(TypeMessage, ThumbnailValidator.Validate(Img.Uri("jpeg", Img.Gif(16, 9))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a data uri")]
    [InlineData("data:image/png;base64,!!!notbase64")]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/gif;base64,R0lGODlh")]
    [InlineData("data:text/html;base64,PGI+")]
    public void Garbage_and_non_data_uris_are_refused(string? value) => Assert.Equal(TypeMessage, ThumbnailValidator.Validate(value));

    [Fact]
    public void A_different_shape_is_refused_with_a_size_hint()
    {
        string? message = ThumbnailValidator.Validate(Img.Uri("png", Img.Png(100, 100)), 1920, 1080);

        Assert.NotNull(message);
        Assert.Contains("100 × 100", message);
        Assert.Contains("1920 × 1080", message);
        Assert.Contains("for example 1920 × 1080 pixels", message);
    }

    [Fact]
    public void The_shape_tolerance_is_fifteen_percent_both_ways()
    {
        // 16:9 video. 16:9 passes, a slightly different shape passes, square and portrait do not.
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(1280, 720)), 1920, 1080));
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(1600, 1000)), 1920, 1080));
        Assert.NotNull(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(1080, 1080)), 1920, 1080));
        Assert.NotNull(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(1080, 1920)), 1920, 1080));
        // Judged symmetrically: a video that is taller than the image is as wrong as one that is wider.
        Assert.NotNull(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(1920, 1080)), 1080, 1920));
    }

    [Fact]
    public void A_shape_exactly_at_the_tolerance_passes()
    {
        // 115 : 100 is exactly 15% wider than a square video.
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(115, 100)), 100, 100));
        Assert.NotNull(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(116, 100)), 100, 100));
    }

    [Fact]
    public void An_unknown_video_size_skips_the_shape_check()
    {
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(10, 500)), 0, 0));
        Assert.Null(ThumbnailValidator.Validate(Img.Uri("png", Img.Png(10, 500)), 1920, 0));
    }

    [Fact]
    public void Sizes_are_read_from_png_and_jpeg_headers_only()
    {
        Assert.True(ThumbnailValidator.TryReadSize(Img.Png(1920, 1080), out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));
        Assert.True(ThumbnailValidator.TryReadSize(Img.Jpeg(640, 360), out w, out h));
        Assert.Equal((640, 360), (w, h));
        Assert.False(ThumbnailValidator.TryReadSize(Img.Gif(5, 5), out _, out _));
        Assert.False(ThumbnailValidator.TryReadSize([0xFF, 0xD8, 0xFF], out _, out _));
        Assert.False(ThumbnailValidator.TryReadSize([], out _, out _));
    }
}

/// <summary>Port of the plugin's ThumbnailStateTest. Time is a fake, so holds and expiry are exact.</summary>
public class ThumbnailStateTests
{
    private const long Clip = 7251205;

    private readonly FakeTime _time = new();
    private readonly ThumbnailState _state;

    public ThumbnailStateTests() => _state = new ThumbnailState(new MemoryCache(new MemoryCacheOptions()), _time);

    private static JsonObject Thumb(string src, bool main, bool crops = true)
    {
        var th = new JsonObject { ["src"] = src, ["main"] = main };
        if (crops)
        {
            th["crops"] = new JsonObject { ["landscape"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1, ["main"] = main } };
        }

        return th;
    }

    private static JsonArray List(params JsonObject[] thumbs) => new(thumbs.Select(t => (JsonNode)t.DeepClone()).ToArray());

    private static string[] Srcs(JsonArray list) => list.Select(ThumbnailLists.Src).ToArray();

    private static string Json(JsonArray list) => list.ToJsonString();

    [Fact]
    public void Without_a_write_the_read_is_used()
    {
        JsonArray read = List(Thumb("/a.jpg", true));
        ResolvedThumbnails resolved = _state.Resolve(Clip, read);

        Assert.Equal(Json(read), Json(resolved.Thumbnails));
        Assert.Empty(resolved.Unconfirmed);
    }

    [Fact]
    public void A_stale_read_is_replaced_by_the_confirmed_list()
    {
        JsonArray stale = List(Thumb("/a.jpg", true), Thumb("/b.jpg", false));
        JsonArray confirmed = List(Thumb("/new.jpeg", true), Thumb("/a.jpg", false), Thumb("/b.jpg", false));
        _state.Remember(Clip, confirmed);

        ResolvedThumbnails resolved = _state.Resolve(Clip, stale);

        Assert.Equal(Json(confirmed), Json(resolved.Thumbnails));
        Assert.Equal(["/new.jpeg"], resolved.Unconfirmed);
    }

    [Fact]
    public void A_read_that_caught_up_wins_and_clears_the_memory()
    {
        _state.Remember(Clip, List(Thumb("/new.jpeg", true), Thumb("/a.jpg", false)));
        JsonArray read = List(Thumb("/a.jpg", false), Thumb("/new.jpeg", true)); // same mains, other order
        _time.Advance(ThumbnailState.Hold);

        ResolvedThumbnails resolved = _state.Resolve(Clip, read);

        Assert.Equal(Json(read), Json(resolved.Thumbnails));
        Assert.Empty(resolved.Unconfirmed);
        // Cleared: a later disagreeing read is used as it is.
        JsonArray other = List(Thumb("/z.jpg", true));
        Assert.Equal(Json(other), Json(_state.Resolve(Clip, other).Thumbnails));
    }

    [Fact]
    public void A_main_change_is_kept_until_the_read_shows_it()
    {
        JsonArray confirmed = List(Thumb("/a.jpg", false), Thumb("/b.jpg", true));
        _state.Remember(Clip, confirmed);
        JsonArray stale = List(Thumb("/a.jpg", true), Thumb("/b.jpg", false));

        ResolvedThumbnails resolved = _state.Resolve(Clip, stale);

        Assert.Equal(Json(confirmed), Json(resolved.Thumbnails));
        Assert.Empty(resolved.Unconfirmed); // nothing is new, only the main differs
    }

    [Fact]
    public void The_top_level_main_counts_for_thumbnails_without_crops()
    {
        JsonArray confirmed = List(Thumb("/a.jpg", false, false), Thumb("/b.jpg", true, false));
        _state.Remember(Clip, confirmed);

        Assert.Equal(Json(confirmed), Json(_state.Resolve(Clip, List(Thumb("/a.jpg", true, false), Thumb("/b.jpg", false, false))).Thumbnails));
    }

    [Fact]
    public void Memory_is_per_clip_and_expires_after_the_ttl()
    {
        _state.Remember(Clip, List(Thumb("/new.jpeg", true)));
        JsonArray other = List(Thumb("/x.jpg", true));
        Assert.Equal(Json(other), Json(_state.Resolve(123, other).Thumbnails));

        Assert.Equal(TimeSpan.FromSeconds(900), ThumbnailState.Ttl);
        Assert.True(ThumbnailState.Hold >= TimeSpan.FromSeconds(600), "reads have lagged about 5 minutes");
        _time.Advance(ThumbnailState.Ttl + TimeSpan.FromSeconds(1));
        JsonArray read = List(Thumb("/a.jpg", true));
        Assert.Equal(Json(read), Json(_state.Resolve(Clip, read).Thumbnails));
    }

    [Fact]
    public void Index_of_finds_by_src()
    {
        JsonArray thumbs = List(Thumb("/a.jpg", true), Thumb("/b.jpg", false));
        Assert.Equal(1, ThumbnailLists.IndexOf(thumbs, "/b.jpg"));
        Assert.Equal(-1, ThumbnailLists.IndexOf(thumbs, "/zz.jpg"));
        Assert.Equal(-1, ThumbnailLists.IndexOf(thumbs, ""));
    }

    [Fact]
    public void An_agreeing_read_right_after_a_write_is_not_trusted()
    {
        // A, then B, then A: the read may show A by accident and pass through B later while it catches up.
        JsonArray confirmed = List(Thumb("/a.jpg", true), Thumb("/b.jpg", false));
        _state.Remember(Clip, confirmed);
        JsonArray laggingB = List(Thumb("/a.jpg", false), Thumb("/b.jpg", true));

        Assert.Equal(Json(confirmed), Json(_state.Resolve(Clip, confirmed).Thumbnails));
        Assert.Equal(Json(confirmed), Json(_state.Resolve(Clip, laggingB).Thumbnails)); // the read agreeing once must not end the hold
        _time.Advance(ThumbnailState.Hold);
        Assert.Equal(Json(confirmed), Json(_state.Resolve(Clip, laggingB).Thumbnails)); // after the hold a disagreeing read still loses
    }

    [Fact]
    public void Main_src_follows_the_crop_flag_not_the_top_level_one()
    {
        // After "set main" SAPI moves only the crop flag; the top-level one stays on a.
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/a.jpg","main":true,"crops":{"landscape":{"main":false}}},{"src":"/b.jpg","main":false,"crops":{"landscape":{"main":true}}}]""")!.AsArray();
        Assert.Equal("/b.jpg", ThumbnailLists.MainSrc(thumbs));

        Assert.Equal("/b.jpg", ThumbnailLists.MainSrc(List(Thumb("/a.jpg", false, false), Thumb("/b.jpg", true, false))));

        JsonArray portraitOnly = JsonNode.Parse("""[{"src":"/p1.jpg","crops":{"portrait":{"main":false}}},{"src":"/p2.jpg","crops":{"portrait":{"main":true}}}]""")!.AsArray();
        Assert.Equal("/p2.jpg", ThumbnailLists.MainSrc(portraitOnly));

        Assert.Equal("/a.jpg", ThumbnailLists.MainSrc(List(Thumb("/a.jpg", false), Thumb("/b.jpg", false)))); // no main: first
        Assert.Equal("", ThumbnailLists.MainSrc([]));
    }

    [Fact]
    public void Embed_urls_get_a_version_only_after_a_recent_change()
    {
        string html = $"""<script src="https://acc.bbvms.com/p/default/c/{Clip}.js"></script><script src="https://acc.bbvms.com/p/default/c/999.js"></script>""";
        Assert.Equal(html, _state.VersionEmbedHtml(html, Clip));

        _state.Remember(Clip, List(Thumb("/a.jpg", true)));
        long ver = _time.GetUtcNow().ToUnixTimeSeconds();
        string output = _state.VersionEmbedHtml(html, Clip);
        Assert.Contains($"/c/{Clip}.js?v={ver}\"", output);
        Assert.Contains("/c/999.js\"", output); // other clips are left alone

        // Playout names may contain spaces.
        Assert.Contains($"/p/Tolkie playout/c/{Clip}.js?v={ver}\"", _state.VersionEmbedHtml($"""<script src="https://acc.bbvms.com/p/Tolkie playout/c/{Clip}.js"></script>""", Clip));

        // An existing query is kept and an old v replaced.
        string iframe = $"""<iframe src="https://acc.bbvms.com/p/default/c/{Clip}.html?inheritDimensions=true&v=1"></iframe>""";
        Assert.Contains($".html?inheritDimensions=true&v={ver}\"", _state.VersionEmbedHtml(iframe, Clip));
    }

    [Fact]
    public void Development_hosts_are_versioned_too()
    {
        // The plugin only knew *.bbvms.com. A dev OVP serves players from other hosts.
        _state.Touch(Clip);
        string html = $"""<script src="https://bb.dev.ovp.bluebillywig.eu/p/default/c/{Clip}.js"></script>""";
        Assert.Contains(".js?v=", _state.VersionEmbedHtml(html, Clip));
        Assert.DoesNotContain("?v=", _state.VersionEmbedHtml($"""<a href="http://evil.test/p/default/c/{Clip}.js">x</a>""", Clip)); // https only
    }

    [Fact]
    public void The_version_stops_after_its_ttl()
    {
        _state.Touch(Clip);
        string html = $"""<script src="https://acc.bbvms.com/p/default/c/{Clip}.js"></script>""";
        Assert.Contains("?v=", _state.VersionEmbedHtml(html, Clip));
        _time.Advance(ThumbnailState.VersionTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(html, _state.VersionEmbedHtml(html, Clip));
    }

    [Fact]
    public void Touch_versions_the_player_url_without_a_thumbnail_list()
    {
        // A title edit must not be hidden by a browser's cached player data (2.0.x fix).
        string html = $"""<script src="https://acc.bbvms.com/p/default/c/{Clip}.js"></script>""";
        _state.Touch(Clip);
        Assert.Contains(".js?v=", _state.VersionEmbedHtml(html, Clip));
        Assert.Empty(_state.Resolve(Clip, List(Thumb("/a.jpg", true))).Unconfirmed);
    }

    [Fact]
    public void With_main_moves_crop_and_top_level_flag_together()
    {
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/a.jpg","main":true,"crops":{"landscape":{"main":true}}},{"src":"/b.jpg","main":false,"crops":{"landscape":{"main":false}}}]""")!.AsArray();
        JsonArray output = ThumbnailLists.WithMain(thumbs, 1, "landscape");

        Assert.False((bool)output[0]!["main"]!);
        Assert.False((bool)output[0]!["crops"]!["landscape"]!["main"]!);
        Assert.True((bool)output[1]!["main"]!);
        Assert.True((bool)output[1]!["crops"]!["landscape"]!["main"]!);
        Assert.Equal("/b.jpg", ThumbnailLists.MainSrc(output));
        Assert.True((bool)thumbs[0]!["main"]!); // the input is not changed
    }

    [Fact]
    public void With_main_on_a_secondary_crop_set_leaves_the_top_level_flag()
    {
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/a.jpg","main":true,"crops":{"landscape":{"main":true},"portrait":{"main":true}}},{"src":"/b.jpg","main":false,"crops":{"landscape":{"main":false},"portrait":{"main":false}}}]""")!.AsArray();
        JsonArray output = ThumbnailLists.WithMain(thumbs, 1, "portrait");

        Assert.True((bool)output[0]!["main"]!); // landscape is primary; a portrait pick must not move the player thumbnail
        Assert.True((bool)output[0]!["crops"]!["landscape"]!["main"]!);
        Assert.True((bool)output[1]!["crops"]!["portrait"]!["main"]!);
        Assert.False((bool)output[0]!["crops"]!["portrait"]!["main"]!);
    }

    [Fact]
    public void Main_src_and_orientation_follow_the_video()
    {
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/l.jpg","main":true,"crops":{"landscape":{"main":true},"portrait":{"main":false}}},{"src":"/p.jpg","main":false,"crops":{"landscape":{"main":false},"portrait":{"main":true}}}]""")!.AsArray();
        Assert.Equal("/p.jpg", ThumbnailLists.MainSrc(thumbs, "portrait"));
        Assert.Equal("/l.jpg", ThumbnailLists.MainSrc(thumbs, "landscape"));
        Assert.Equal("portrait", ThumbnailLists.PrimaryCropset(JsonNode.Parse("""{"width":1080,"height":1920}""")));
        Assert.Equal("landscape", ThumbnailLists.PrimaryCropset(JsonNode.Parse("""{"width":"1920","height":"1080"}""")));
        Assert.Equal("landscape", ThumbnailLists.PrimaryCropset(JsonNode.Parse("{}")));
    }

    [Fact]
    public void With_main_on_a_portrait_video_moves_the_top_level_flag_with_portrait()
    {
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/a.jpg","main":true,"crops":{"landscape":{"main":true},"portrait":{"main":true}}},{"src":"/b.jpg","main":false,"crops":{"landscape":{"main":false},"portrait":{"main":false}}}]""")!.AsArray();
        JsonArray portrait = ThumbnailLists.WithMain(thumbs, 1, "portrait", "portrait");
        Assert.True((bool)portrait[1]!["main"]!);
        Assert.False((bool)portrait[0]!["main"]!);
        Assert.True((bool)portrait[0]!["crops"]!["landscape"]!["main"]!); // the landscape main stays

        Assert.True((bool)ThumbnailLists.WithMain(thumbs, 1, "landscape", "portrait")[0]!["main"]!); // a landscape pick on a portrait video leaves the top-level flag
    }

    [Fact]
    public void An_upload_goes_in_the_video_crop_set_and_keeps_other_mains()
    {
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/a.jpg","main":true,"crops":{"landscape":{"main":true},"portrait":{"main":true}}}]""")!.AsArray();
        Assert.Equal("portrait", ThumbnailLists.UploadCropset(thumbs, "portrait"));
        JsonArray output = ThumbnailLists.WithUpload(thumbs, "data:image/png;base64,AA==", "portrait");

        Assert.False((bool)output[0]!["main"]!);
        Assert.False((bool)output[0]!["crops"]!["portrait"]!["main"]!);
        Assert.True((bool)output[0]!["crops"]!["landscape"]!["main"]!); // the landscape main stays
        Assert.True((bool)output[1]!["main"]!);
        Assert.Equal(["portrait"], output[1]!["crops"]!.AsObject().Select(c => c.Key));
        Assert.True((bool)output[1]!["crops"]!["portrait"]!["main"]!);

        // A clip without that crop set uses the one it has; without crops, none.
        Assert.Equal("landscape", ThumbnailLists.UploadCropset(List(Thumb("/a.jpg", true)), "portrait"));
        Assert.Equal("default", ThumbnailLists.UploadCropset(List(Thumb("/a.jpg", true, false)), "landscape"));
        Assert.Equal("portrait", ThumbnailLists.UploadCropset([], "portrait"));
        Assert.Null(ThumbnailLists.WithUpload([], "data:image/png;base64,AA==", "default")[0]!["crops"]);
    }

    [Fact]
    public void The_real_bb_dev_shape_with_a_crop_set_named_default_works_end_to_end()
    {
        // What bb.dev returns for a processed clip: webp thumbnails with crops {"default": {...}}. The plugin's virtual
        // crop set "default" (thumbnails without crops) has the same name, and the two must behave the same.
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/a.webp","main":false,"crops":{"default":{"x":0,"y":0,"width":1,"height":1,"main":false}}},{"src":"/b.webp","main":true,"crops":{"default":{"x":0,"y":0,"width":1,"height":1,"main":true}}}]""")!.AsArray();
        string primary = ThumbnailLists.PrimaryCropset(JsonNode.Parse("""{"width":320,"height":180}"""));
        string cropset = ThumbnailLists.UploadCropset(thumbs, primary);
        Assert.Equal("default", cropset);

        JsonArray uploaded = ThumbnailLists.WithUpload(thumbs, "data:image/png;base64,AA==", cropset);
        Assert.Equal(3, uploaded.Count);
        Assert.False((bool)uploaded[1]!["crops"]!["default"]!["main"]!); // the old main is demoted in its crop set
        Assert.Equal("/b.webp", ThumbnailLists.MainSrc(thumbs, primary));
        // The new image has no src yet; the list that SAPI confirms will. Setting a main afterwards moves both flags.
        JsonArray confirmed = JsonNode.Parse("""[{"src":"/a.webp","main":false,"crops":{"default":{"main":false}}},{"src":"/b.webp","main":false,"crops":{"default":{"main":false}}},{"src":"/n.png","main":true,"crops":{"default":{"main":true}}}]""")!.AsArray();
        Assert.Equal("/n.png", ThumbnailLists.MainSrc(confirmed, primary));
        JsonArray moved = ThumbnailLists.WithMain(confirmed, 0, ThumbnailLists.UploadCropset(confirmed, primary), primary);
        Assert.Equal("/a.webp", ThumbnailLists.MainSrc(moved, primary));
        Assert.False((bool)moved[2]!["main"]!);
        Assert.False((bool)moved[2]!["crops"]!["default"]!["main"]!);
    }

    [Fact]
    public void Picking_a_thumbnail_without_an_entry_for_the_crop_set_gives_it_one()
    {
        // bb.dev: only the first frame of a processed clip has the video's crop set; the other frames only have "default".
        // Picking such a frame must make it main in the video's crop set too, or SAPI hands main back to the first frame.
        JsonArray thumbs = JsonNode.Parse("""[{"src":"/first.webp","main":true,"crops":{"default":{"main":false},"landscape":{"x":0,"y":0,"width":1,"height":1,"main":true}}},{"src":"/b.webp","main":false,"crops":{"default":{"main":false}}}]""")!.AsArray();

        JsonArray output = ThumbnailLists.WithMain(thumbs, 1, "landscape", "landscape");

        Assert.False((bool)output[0]!["crops"]!["landscape"]!["main"]!);
        Assert.True((bool)output[1]!["crops"]!["landscape"]!["main"]!);
        Assert.Equal(1, (int)output[1]!["crops"]!["landscape"]!["width"]!);
        Assert.True((bool)output[1]!["main"]!);
        Assert.False((bool)output[0]!["main"]!);
        Assert.Equal("/b.webp", ThumbnailLists.MainSrc(output, "landscape"));
        Assert.Null(thumbs[1]!["crops"]!["landscape"]); // the input is not changed

        // The virtual crop set "default" never gets an entry added.
        JsonArray plain = ThumbnailLists.WithMain(List(Thumb("/a.jpg", true, false), Thumb("/b.jpg", false, false)), 1, "default");
        Assert.Null(plain[1]!["crops"]);
    }

    [Fact]
    public void With_main_without_crops_uses_the_top_level_flag()
    {
        JsonArray output = ThumbnailLists.WithMain(List(Thumb("/a.jpg", true, false), Thumb("/b.jpg", false, false)), 1, "default");
        Assert.False((bool)output[0]!["main"]!);
        Assert.True((bool)output[1]!["main"]!);
    }

    [Fact]
    public void Dedupe_keeps_the_first_entry_per_src()
    {
        JsonArray thumbs = List(Thumb("/a.jpg", false), Thumb("/b.jpg", true), Thumb("/a.jpg", true), new JsonObject { ["data"] = "data:image/png;base64,xx", ["main"] = true }, Thumb("/b.jpg", false));
        JsonArray output = ThumbnailLists.Dedupe(thumbs);

        Assert.Equal(["/a.jpg", "/b.jpg"], Srcs(output).Take(2));
        Assert.False((bool)output[0]!["main"]!); // the first entry wins
        Assert.Equal(3, output.Count); // entries without a src (a new upload) are kept
    }

    [Fact]
    public void Resolve_lists_each_src_once()
    {
        JsonArray read = List(Thumb("/a.jpg", true), Thumb("/b.jpg", false), Thumb("/a.jpg", false));
        Assert.Equal(["/a.jpg", "/b.jpg"], Srcs(_state.Resolve(Clip, read).Thumbnails));

        _state.Remember(Clip, List(Thumb("/n.jpg", true), Thumb("/n.jpg", false)), "2026-10-19T14:13:19Z");
        Assert.Equal(["/n.jpg"], Srcs(_state.Resolve(Clip, read, "2026-10-19T14:00:00Z").Thumbnails));
    }

    [Fact]
    public void An_older_read_loses_to_the_write_even_after_the_hold()
    {
        JsonArray confirmed = List(Thumb("/new.jpeg", true), Thumb("/a.jpg", false));
        _state.Remember(Clip, confirmed, "2026-10-19T14:13:19Z");
        JsonArray stale = List(Thumb("/a.jpg", true));

        ResolvedThumbnails resolved = _state.Resolve(Clip, stale, "2026-10-19T14:10:00Z");
        Assert.Equal(Json(confirmed), Json(resolved.Thumbnails));
        Assert.Equal(["/new.jpeg"], resolved.Unconfirmed);

        _time.Advance(ThumbnailState.Hold);
        Assert.Equal(Json(confirmed), Json(_state.Resolve(Clip, stale, "2026-10-19T14:10:00Z").Thumbnails));
    }

    [Fact]
    public void A_read_as_new_as_the_write_wins_at_once()
    {
        _state.Remember(Clip, List(Thumb("/new.jpeg", true), Thumb("/a.jpg", false)), "2026-10-19T14:13:19Z");
        JsonArray read = List(Thumb("/a.jpg", false), Thumb("/new.jpeg", true));

        Assert.Equal(Json(read), Json(_state.Resolve(Clip, read, "2026-10-19T14:13:19Z").Thumbnails)); // no hold when the dates match
        JsonArray other = List(Thumb("/z.jpg", true));
        Assert.Equal(Json(other), Json(_state.Resolve(Clip, other).Thumbnails)); // and the memory is gone
    }

    [Fact]
    public void A_newer_change_made_elsewhere_is_not_overwritten()
    {
        _state.Remember(Clip, List(Thumb("/new.jpeg", true), Thumb("/a.jpg", false)), "2026-10-19T14:13:19Z");
        JsonArray elsewhere = List(Thumb("/new.jpeg", true));

        ResolvedThumbnails resolved = _state.Resolve(Clip, elsewhere, "2026-10-19T14:15:02Z");

        Assert.Equal(Json(elsewhere), Json(resolved.Thumbnails));
        Assert.Empty(resolved.Unconfirmed);
    }

    [Fact]
    public void Resolve_clip_reads_thumbnails_and_updateddate()
    {
        JsonArray confirmed = List(Thumb("/new.jpeg", true));
        _state.Remember(Clip, confirmed, "2026-10-19T14:13:19Z");
        JsonNode clip = JsonNode.Parse("""{"id":"7251205","updateddate":"2026-10-19T14:00:00Z","thumbnails":[{"src":"/a.jpg","main":true}]}""")!;
        Assert.Equal(Json(confirmed), Json(_state.ResolveClip(Clip, clip).Thumbnails));

        clip["updateddate"] = "2026-10-19T14:20:00Z";
        Assert.Equal("/a.jpg", Srcs(_state.ResolveClip(Clip, clip).Thumbnails).Single());
    }

    [Fact]
    public void The_clip_main_src_uses_the_confirmed_list_and_the_video_orientation()
    {
        JsonNode clip = JsonNode.Parse("""{"width":1080,"height":1920,"thumbnails":[{"src":"/l.jpg","main":true,"crops":{"landscape":{"main":true}}},{"src":"/p.jpg","main":false,"crops":{"portrait":{"main":true}}}]}""")!;
        Assert.Equal("/p.jpg", _state.ClipMainSrc(Clip, clip)); // portrait video: portrait main

        _state.Remember(Clip, List(Thumb("/new.jpg", true)));
        Assert.Equal("/new.jpg", _state.ClipMainSrc(Clip, clip)); // a lagging read loses to the confirmed list
        Assert.Null(_state.ClipMainSrc(999, JsonNode.Parse("""{"thumbnails":[]}""")));
    }
}

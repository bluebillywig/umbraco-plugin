using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>The thumbnail list to show and write back (each src once), and the srcs SAPI's own read does not contain yet.</summary>
public sealed record ResolvedThumbnails(JsonArray Thumbnails, IReadOnlyList<string> Unconfirmed);

/// <summary>
/// Remembers a clip's thumbnail list as confirmed by a SAPI write, until SAPI's own read catches up. Port of the
/// plugin's <c>ThumbnailState</c>. After a thumbnail PUT, <c>GET /sapi/mediaclip/{id}</c> keeps returning the old
/// thumbnails for a few minutes while the PUT response already has the new list, so rendering or writing from the read
/// would hide a fresh upload and drop it on the next write. Each write also bumps a per-clip version that player URLs
/// carry as <c>?v=</c>, because the player data is cached by browsers for up to about 15 minutes (title edits too).
/// </summary>
public interface IThumbnailState
{
    /// <summary>Remember the list a SAPI write returned (and bump the version).</summary>
    void Remember(long clipId, JsonArray thumbnails, string updated = "");

    /// <summary>Note that the clip changed now, so its player URLs get a new <c>v=</c>.</summary>
    void Touch(long clipId);

    ResolvedThumbnails ResolveClip(long clipId, JsonNode? clip);

    ResolvedThumbnails Resolve(long clipId, JsonArray readThumbnails, string readUpdated = "");

    /// <summary>The main thumbnail src of a clip as SAPI returned it, using a recently confirmed list. Null when there is none.</summary>
    string? ClipMainSrc(long clipId, JsonNode? clip);

    /// <summary>Adds <c>v=&lt;last change&gt;</c> to the clip's player URLs in embed HTML, only while a recent change could still be cached.</summary>
    string VersionEmbedHtml(string html, long clipId);

    /// <summary>
    /// Forgets every remembered list and version. Not part of a cache clear: like the plugin's <c>clearCache()</c>, which leaves
    /// <c>bb_thumbs_*</c> and <c>bb_clip_ver_*</c> alone, a clear must not bring back the lagging read this state hides. The
    /// cleanup action drops it, like the plugin's uninstall script. Node-local, like the state itself.
    /// </summary>
    void Clear();
}

internal sealed partial class ThumbnailState : IThumbnailState
{
    /// <summary>How long a confirmed list may override SAPI's read.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(900);

    /// <summary>Minimum time a confirmed list wins over a read without dates. Reads have lagged up to about 5 minutes.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(600);

    /// <summary>How long embeds carry <c>?v=</c>: the player data's max-age plus stale-while-revalidate, plus margin.</summary>
    public static readonly TimeSpan VersionTtl = TimeSpan.FromSeconds(1020);

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private CancellationTokenSource _generation = new();

    private sealed record Stored(DateTimeOffset At, string Updated, JsonArray Thumbs, DateTimeOffset Expires);

    private sealed record Version(long Value, DateTimeOffset Expires);

    public ThumbnailState(IMemoryCache cache, TimeProvider? time = null)
    {
        _cache = cache;
        _time = time ?? TimeProvider.System;
    }

    public void Remember(long clipId, JsonArray thumbnails, string updated = "")
    {
        DateTimeOffset now = _time.GetUtcNow();
        _cache.Set(ThumbKey(clipId), new Stored(now, updated, ThumbnailLists.Dedupe(thumbnails), now + Ttl), Options(Ttl));
        Touch(clipId);
    }

    public void Touch(long clipId)
    {
        DateTimeOffset now = _time.GetUtcNow();
        _cache.Set(VersionKey(clipId), new Version(now.ToUnixTimeSeconds(), now + VersionTtl), Options(VersionTtl));
    }

    public void Clear()
    {
        CancellationTokenSource previous = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }

    private MemoryCacheEntryOptions Options(TimeSpan ttl) =>
        new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(Volatile.Read(ref _generation).Token));

    public ResolvedThumbnails ResolveClip(long clipId, JsonNode? clip)
    {
        JsonArray thumbs = clip?["thumbnails"] as JsonArray ?? [];
        string updated = clip?["updateddate"] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";
        return Resolve(clipId, thumbs, updated);
    }

    public ResolvedThumbnails Resolve(long clipId, JsonArray readThumbnails, string readUpdated = "")
    {
        JsonArray read = ThumbnailLists.Dedupe(readThumbnails);
        var useRead = new ResolvedThumbnails(read, []);
        DateTimeOffset now = _time.GetUtcNow();

        if (!_cache.TryGetValue(ThumbKey(clipId), out Stored? stored) || stored is null || stored.Expires <= now)
        {
            return useRead;
        }

        JsonArray remembered = ThumbnailLists.Dedupe(stored.Thumbs);
        if (remembered.Count == 0)
        {
            return useRead;
        }

        var readBySrc = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonObject th in read.OfType<JsonObject>())
        {
            string src = ThumbnailLists.Src(th);
            if (src.Length > 0)
            {
                readBySrc[src] = th;
            }
        }

        var unconfirmed = new List<string>();
        bool caughtUp = true;
        foreach (JsonObject th in remembered.OfType<JsonObject>())
        {
            string src = ThumbnailLists.Src(th);
            if (!readBySrc.TryGetValue(src, out JsonObject? readTh))
            {
                unconfirmed.Add(src);
                caughtUp = false;
            }
            else if (ThumbnailLists.MainFlags(th) != ThumbnailLists.MainFlags(readTh))
            {
                caughtUp = false;
            }
        }

        // Dates decide when both are known: a read at least as new as our write has it (or a later change made
        // elsewhere, which must win).
        if (TryTime(stored.Updated, out DateTimeOffset writeTime) && TryTime(readUpdated, out DateTimeOffset readTime))
        {
            if (readTime >= writeTime)
            {
                _cache.Remove(ThumbKey(clipId));
                return useRead;
            }

            return new ResolvedThumbnails(remembered, unconfirmed);
        }

        // No dates: trust a read that agrees, but not right after the write (after A to B to A the read may show B again).
        bool held = now - stored.At < Hold;
        if (caughtUp && !held)
        {
            _cache.Remove(ThumbKey(clipId));
            return useRead;
        }

        return new ResolvedThumbnails(remembered, unconfirmed);
    }

    public string? ClipMainSrc(long clipId, JsonNode? clip)
    {
        string src = ThumbnailLists.MainSrc(ResolveClip(clipId, clip).Thumbnails, ThumbnailLists.PrimaryCropset(clip));
        return src.Length > 0 ? src : null;
    }

    public string VersionEmbedHtml(string html, long clipId)
    {
        if (string.IsNullOrEmpty(html)
            || !_cache.TryGetValue(VersionKey(clipId), out Version? version)
            || version is null
            || version.Expires <= _time.GetUtcNow())
        {
            return html;
        }

        // Hosts: the plugin only knew *.bbvms.com. A development OVP serves players from its own hosts, so any https host
        // is accepted as long as the path has the player shape /p/{playout}/c/{id}.js|html for this clip.
        var pattern = new Regex(
            @"(https://[a-z0-9.-]+/p/[^/""'<>?]+/c/" + clipId.ToString(CultureInfo.InvariantCulture) + @"\.(?:js|html))(\?[^""'\s<>]*)?",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return pattern.Replace(html, m =>
        {
            string query = m.Groups[2].Success ? m.Groups[2].Value : "";
            query = VersionParam().Replace(query, "$1").TrimEnd('?', '&');
            return m.Groups[1].Value + (query.Length == 0 ? "?" : query + "&") + "v=" + version.Value.ToString(CultureInfo.InvariantCulture);
        });
    }

    private static bool TryTime(string? value, out DateTimeOffset time) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time) && !string.IsNullOrWhiteSpace(value);

    private static string ThumbKey(long clipId) => "bb:thumbs:" + clipId.ToString(CultureInfo.InvariantCulture);

    private static string VersionKey(long clipId) => "bb:clipver:" + clipId.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"([?&])v=[^&]*&?")]
    private static partial Regex VersionParam();
}

/// <summary>The pure list operations of <c>ThumbnailState</c>: picking the main thumbnail and building the list to write.</summary>
public static class ThumbnailLists
{
    public static string Src(JsonNode? th) =>
        th is JsonObject o && o["src"] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    /// <summary>Keep each thumbnail src once (the first entry), in order. Entries without a src (a fresh upload) all stay.</summary>
    public static JsonArray Dedupe(JsonArray thumbs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (JsonNode? th in thumbs)
        {
            string src = Src(th);
            if (src.Length > 0 && !seen.Add(src))
            {
                continue;
            }

            result.Add(th?.DeepClone());
        }

        return result;
    }

    /// <summary>The crop set that matches the clip's orientation: <c>portrait</c> for a portrait video, otherwise <c>landscape</c>.</summary>
    public static string PrimaryCropset(JsonNode? clip) => ReadInt(clip?["height"]) > ReadInt(clip?["width"]) ? "portrait" : "landscape";

    /// <summary>
    /// The src of the main thumbnail, empty when there are none. The crop flag of the video's own orientation
    /// decides, then the top-level flag, then any crop flag, then the first thumbnail.
    /// </summary>
    public static string MainSrc(JsonArray thumbs, string primary = "landscape")
    {
        List<JsonObject> withSrc = thumbs.OfType<JsonObject>().Where(t => Src(t).Length > 0).ToList();
        Func<JsonObject, bool>[] checks =
        [
            t => IsTrue(t["crops"]?[primary]?["main"]),
            t => IsTrue(t["main"]),
            t => t["crops"] is JsonObject crops && crops.Any(c => IsTrue(c.Value?["main"])),
        ];
        foreach (Func<JsonObject, bool> check in checks)
        {
            JsonObject? hit = withSrc.FirstOrDefault(check);
            if (hit is not null)
            {
                return Src(hit);
            }
        }

        return withSrc.Count > 0 ? Src(withSrc[0]) : "";
    }

    /// <summary>
    /// The crop set a new upload goes in, and whose pick moves the top-level flag: the video's own crop set when the clip
    /// uses it (or has no thumbnails), otherwise the first crop set the thumbnails have (<c>default</c> for thumbnails
    /// without crops). On bb.dev a processed clip's thumbnails carry a real crop set named <c>default</c>, which is the
    /// same name the plugin uses for "no crops"; the two behave the same.
    /// </summary>
    public static string UploadCropset(JsonArray thumbs, string primary = "landscape")
    {
        string first = "";
        foreach (JsonObject th in thumbs.OfType<JsonObject>())
        {
            JsonObject? crops = th["crops"] as JsonObject;
            if (crops is not null && crops.ContainsKey(primary))
            {
                return primary;
            }

            if (first.Length == 0)
            {
                first = crops is { Count: > 0 } ? crops.First().Key : "default";
            }
        }

        return first.Length == 0 ? primary : first;
    }

    /// <summary>Add an uploaded image as the new main thumbnail of <paramref name="cropset"/>. The other crop sets keep their main.</summary>
    public static JsonArray WithUpload(JsonArray thumbs, string dataUri, string cropset)
    {
        JsonArray result = (JsonArray)thumbs.DeepClone();
        foreach (JsonObject th in result.OfType<JsonObject>())
        {
            th["main"] = false;
            if (th["crops"]?[cropset] is JsonObject crop)
            {
                crop["main"] = false;
            }
        }

        var added = new JsonObject { ["data"] = dataUri, ["main"] = true };
        if (cropset != "default")
        {
            added["crops"] = new JsonObject
            {
                [cropset] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1, ["main"] = true },
            };
        }

        result.Add(added);
        return result;
    }

    /// <summary>
    /// Make the thumbnail at <paramref name="index"/> the main one of <paramref name="cropset"/>. Other crop sets are left
    /// alone, so portrait and landscape can have independent mains. Thumbnails without crops use the top-level flag, and
    /// the top-level flag moves along with the crop set of the video's orientation (or the first one the clip has),
    /// because the player data carries both.
    /// </summary>
    public static JsonArray WithMain(JsonArray thumbs, int index, string cropset, string primary = "landscape")
    {
        JsonArray result = (JsonArray)thumbs.DeepClone();
        primary = UploadCropset(result, primary);
        for (int i = 0; i < result.Count; i++)
        {
            if (result[i] is not JsonObject th)
            {
                continue;
            }

            if (th["crops"] is JsonObject { Count: > 0 } crops)
            {
                // A thumbnail that is picked but has no entry for the crop set gets a full-frame one. Without it nothing in the
                // set is main after the write and SAPI hands main back to the thumbnail that has the entry (on bb.dev only the
                // first frame of a processed clip carries the video's crop set, so picking any other frame silently did nothing).
                if (i == index && cropset.Length > 0 && cropset != "default" && !crops.ContainsKey(cropset))
                {
                    crops[cropset] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1, ["main"] = true };
                }

                foreach (KeyValuePair<string, JsonNode?> crop in crops)
                {
                    if (cropset.Length > 0 && crop.Key == cropset && crop.Value is JsonObject c)
                    {
                        c["main"] = i == index;
                    }
                }

                if (cropset.Length > 0 && cropset == primary)
                {
                    th["main"] = i == index;
                }
            }
            else
            {
                th["main"] = i == index;
            }
        }

        return result;
    }

    /// <summary>Index of the thumbnail with the given src, or -1.</summary>
    public static int IndexOf(JsonArray thumbs, string src)
    {
        for (int i = 0; i < thumbs.Count; i++)
        {
            if (src.Length > 0 && Src(thumbs[i]) == src)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The main flags of a thumbnail: top-level and per crop set, as a comparable string.</summary>
    internal static string MainFlags(JsonObject th)
    {
        var flags = new SortedDictionary<string, bool>(StringComparer.Ordinal) { [""] = IsTrue(th["main"]) };
        if (th["crops"] is JsonObject crops)
        {
            foreach (KeyValuePair<string, JsonNode?> crop in crops)
            {
                flags[crop.Key] = IsTrue(crop.Value?["main"]);
            }
        }

        return string.Join(',', flags.Select(f => f.Key + "=" + (f.Value ? "1" : "0")));
    }

    internal static bool IsTrue(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue(out bool b) && b) || (v.TryGetValue(out int i) && i != 0) || (v.TryGetValue(out string? s) && s is "1" or "true"));

    private static int ReadInt(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue(out int i) || (v.TryGetValue(out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))) ? i : 0;
}

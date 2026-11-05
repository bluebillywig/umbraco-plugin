using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Sapi;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>Picks the main thumbnail of a clip the way the reference plugin's <c>ThumbnailState::main_src</c> does.</summary>
public static class ClipThumbnails
{
    /// <summary>The src of the main thumbnail, or null when there is none.</summary>
    public static string? MainSrc(JsonNode? clip)
    {
        if (clip?["thumbnails"] is not JsonArray thumbs)
        {
            return null;
        }

        int width = ReadInt(clip["width"]);
        int height = ReadInt(clip["height"]);
        string primary = height > width ? "portrait" : "landscape";

        List<JsonObject> withSrc = thumbs
            .OfType<JsonObject>()
            .Where(t => t["src"] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s))
            .ToList();

        // The crop flag of the video's own orientation, then the top-level flag, then any crop flag, then the first.
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
                return hit["src"]!.GetValue<string>();
            }
        }

        return withSrc.Count > 0 ? withSrc[0]["src"]!.GetValue<string>() : null;
    }

    /// <summary>Joins the publication's asset path and a src into an absolute http(s) URL, or null when it is not one.</summary>
    public static string? Absolute(string? assetBase, string? src)
    {
        if (string.IsNullOrEmpty(src))
        {
            return null;
        }

        string joined = (assetBase ?? "") + src;
        return Uri.TryCreate(joined, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
    }

    /// <summary><c>defaultMediaAssetPath</c> of the publication, cached by the client for an hour.</summary>
    public static async Task<string> AssetBaseAsync(IBlueBillywigClient client, CancellationToken ct)
    {
        SapiResult publication = await client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromHours(1), ct: ct);
        return publication.Ok && publication.Json?["defaultMediaAssetPath"] is JsonValue v && v.TryGetValue(out string? path) ? path ?? "" : "";
    }

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue(out bool b) && b) || (v.TryGetValue(out int i) && i != 0));

    private static int ReadInt(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int i) ? i : 0;
}

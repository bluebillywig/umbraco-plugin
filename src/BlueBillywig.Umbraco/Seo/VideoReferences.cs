using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Embedding;

namespace BlueBillywig.Umbraco.Seo;

/// <summary>
/// Finds the clips a piece of stored content embeds, from the raw (source) value of a property: the video property's JSON,
/// a block list or block grid (JSON with the video JSON inside the blocks' values), and rich text (JSON with markup, whose
/// embeds are the references <see cref="RichTextEmbedRewriter"/> turns into players). Nothing is rendered and nothing
/// is fetched; stored values are data, so every candidate goes through <see cref="BlueBillywigVideo.Parse"/>.
/// </summary>
public static class VideoReferences
{
    private const int MaxDepth = 24;
    private const int MaxNodes = 20_000;
    private const int MaxVideos = 200;

    /// <summary>The distinct clips in order of first appearance. Channels and shorts have no clip metadata and are left out.</summary>
    public static IReadOnlyList<BlueBillywigVideo> FindClips(string? sourceValue)
    {
        if (string.IsNullOrWhiteSpace(sourceValue) || sourceValue.Length > 4_000_000)
        {
            return [];
        }

        var found = new List<BlueBillywigVideo>();
        int budget = MaxNodes;
        string trimmed = sourceValue.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                Walk(JsonNode.Parse(sourceValue), found, 0, ref budget);
            }
            catch (System.Text.Json.JsonException)
            {
                Markup(sourceValue, found);
            }
        }
        else
        {
            Markup(sourceValue, found);
        }

        return found
            .Where(v => v.Type == BlueBillywigVideoType.Clip)
            .DistinctBy(v => (v.Id, v.Publication))
            .ToList();
    }

    private static void Walk(JsonNode? node, List<BlueBillywigVideo> found, int depth, ref int budget)
    {
        if (node is null || depth > MaxDepth || --budget < 0 || found.Count >= MaxVideos)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("id") && obj.ContainsKey("type") && BlueBillywigVideo.Parse(obj) is { } video)
                {
                    found.Add(video);
                    return;
                }

                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    Walk(pair.Value, found, depth + 1, ref budget);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    Walk(item, found, depth + 1, ref budget);
                }

                break;
            case JsonValue value when value.TryGetValue(out string? text) && text is not null:
                // A video property inside a block can be a JSON string, and rich text markup is a string too.
                string t = text.TrimStart();
                if (t.StartsWith('{') && t.Contains("\"id\"", StringComparison.Ordinal))
                {
                    try
                    {
                        Walk(JsonNode.Parse(text), found, depth + 1, ref budget);
                        break;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                    }
                }

                Markup(text, found);
                break;
        }
    }

    private static void Markup(string html, List<BlueBillywigVideo> found)
    {
        if (!RichTextEmbedRewriter.MayContainEmbeds(html))
        {
            return;
        }

        // The rewriter finds the references (pasted URLs and block references); "rendering" one just records it.
        RichTextEmbedRewriter.RewriteAsync(html, video =>
        {
            found.Add(video);
            return Task.FromResult("");
        }).GetAwaiter().GetResult();
    }
}

using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>
/// Playout names. The plugin checks a playout against <c>/^[a-zA-Z0-9_-]+$/</c> before it writes one, as the default in the
/// settings and per video (<c>PlayoutValidationTest</c>), because the name ends up in a path and in embed markup.
/// </summary>
public static class ClipPlayoutName
{
    public static bool IsValid(string? value) => BlueBillywigVideo.IsValidPlayout(value);

    /// <summary>The options for a playout dropdown from SAPI's <c>/sapi/playout/</c> list: the label as value, a fallback when it is empty.</summary>
    public static IReadOnlyList<PlayoutOptionResponseModel> Options(JsonNode? list)
    {
        List<PlayoutOptionResponseModel> playouts = (list?["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => new PlayoutOptionResponseModel { Label = Text(p["name"]), Value = Text(p["label"]) })
            .Where(p => IsValid(p.Value))
            .Select(p => p.Label.Length == 0 ? new PlayoutOptionResponseModel { Label = p.Value, Value = p.Value } : p)
            .ToList();
        if (playouts.Count == 0)
        {
            playouts.Add(new PlayoutOptionResponseModel { Label = "Default", Value = "default" });
        }

        return playouts;
    }

    private static string Text(JsonNode? node) => node is JsonValue v ? (v.TryGetValue(out string? s) ? s?.Trim() ?? "" : v.ToString()) : "";
}

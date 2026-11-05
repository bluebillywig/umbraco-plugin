using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Configuration;

namespace BlueBillywig.Umbraco.Embedding;

public enum BlueBillywigVideoType
{
    Clip,
    Channel,
    Shorts,
}

/// <summary>
/// What the "Blue Billywig Video" property stores. Mirrors the reference plugin's block attributes: a clip has
/// playout, transcript, chapters and lazy loading; a channel has height and lazy loading; shorts have
/// height and fullscreen. Everything is validated on parse, because the stored JSON is editable data.
/// </summary>
public sealed record BlueBillywigVideo
{
    public const string DefaultClipPlayout = "default";
    public const string DefaultChannelHeight = "600px";
    public const string DefaultShortsHeight = "700px";

    private static readonly Regex PlayoutPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex HeightPattern = new(@"^\d{1,4}(\.\d{1,2})?(px|vh|em|rem|%)$", RegexOptions.Compiled);

    public BlueBillywigVideoType Type { get; init; } = BlueBillywigVideoType.Clip;

    /// <summary>Clip, channel or shorts id. Always positive for a parsed value.</summary>
    public long Id { get; init; }

    /// <summary>Overrides the configured publication. Set for oEmbed URLs that name one.</summary>
    public string? Publication { get; init; }

    /// <summary>Clip only. Null means the configured default playout.</summary>
    public string? Playout { get; init; }

    public bool ShowTranscript { get; init; } = true;
    public bool ShowChapters { get; init; }
    public bool LazyLoad { get; init; } = true;

    /// <summary>Channel and shorts only. A CSS length such as <c>600px</c>.</summary>
    public string? Height { get; init; }

    /// <summary>Shorts only.</summary>
    public bool Fullscreen { get; init; }

    public bool IsValid => Id > 0;

    public static string TypeToString(BlueBillywigVideoType type) => type.ToString().ToLowerInvariant();

    public static bool IsValidPublication(string? value) => PublicationSlug.IsValid(value);

    public static bool IsValidPlayout(string? value) => value is not null && PlayoutPattern.IsMatch(value);

    public static bool IsValidHeight(string? value) => value is not null && HeightPattern.IsMatch(value);

    /// <summary>Parses the stored value. Returns null for empty, malformed or id-less values.</summary>
    public static BlueBillywigVideo? Parse(object? source)
    {
        JsonNode? node;
        try
        {
            node = source switch
            {
                null => null,
                string s when string.IsNullOrWhiteSpace(s) => null,
                string s => JsonNode.Parse(s),
                JsonNode n => n,
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }

        return node is JsonObject obj ? FromJson(obj) : null;
    }

    private static BlueBillywigVideo? FromJson(JsonObject obj)
    {
        BlueBillywigVideoType type = (ReadString(obj, "type") ?? "clip").ToLowerInvariant() switch
        {
            "clip" => BlueBillywigVideoType.Clip,
            "channel" => BlueBillywigVideoType.Channel,
            "shorts" => BlueBillywigVideoType.Shorts,
            _ => (BlueBillywigVideoType)(-1),
        };
        if (!Enum.IsDefined(type))
        {
            return null;
        }

        long id = ReadId(obj["id"]);
        if (id <= 0)
        {
            return null;
        }

        string? publication = ReadString(obj, "publication")?.ToLowerInvariant();
        string? playout = ReadString(obj, "playout");
        string? height = ReadString(obj, "height");

        return new BlueBillywigVideo
        {
            Type = type,
            Id = id,
            Publication = IsValidPublication(publication) ? publication : null,
            Playout = IsValidPlayout(playout) ? playout : null,
            ShowTranscript = ReadBool(obj, "showTranscript") ?? true,
            ShowChapters = ReadBool(obj, "showChapters") ?? false,
            // Channels are usually in hero position, so they default to eager; clips default to lazy.
            LazyLoad = ReadBool(obj, "lazyLoad") ?? type != BlueBillywigVideoType.Channel,
            Height = IsValidHeight(height)
                ? height
                : type == BlueBillywigVideoType.Shorts ? DefaultShortsHeight : DefaultChannelHeight,
            Fullscreen = ReadBool(obj, "fullscreen") ?? false,
        };
    }

    /// <summary>The compact JSON the property editor stores.</summary>
    public string ToJson()
    {
        var obj = new JsonObject { ["type"] = TypeToString(Type), ["id"] = Id };
        if (Publication is not null) obj["publication"] = Publication;
        switch (Type)
        {
            case BlueBillywigVideoType.Clip:
                if (Playout is not null) obj["playout"] = Playout;
                obj["showTranscript"] = ShowTranscript;
                obj["showChapters"] = ShowChapters;
                obj["lazyLoad"] = LazyLoad;
                break;
            case BlueBillywigVideoType.Channel:
                obj["height"] = Height ?? DefaultChannelHeight;
                obj["lazyLoad"] = LazyLoad;
                break;
            default:
                obj["height"] = Height ?? DefaultShortsHeight;
                obj["fullscreen"] = Fullscreen;
                break;
        }

        return obj.ToJsonString();
    }

    private static string? ReadString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue(out string? s) ? s?.Trim() : null;

    private static bool? ReadBool(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue v) return null;
        if (v.TryGetValue(out bool b)) return b;
        if (v.TryGetValue(out string? s) && bool.TryParse(s, out bool parsed)) return parsed;
        return null;
    }

    private static long ReadId(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue(out long l)) return l is > 0 and < 1_000_000_000_000 ? l : 0;
        if (v.TryGetValue(out string? s)
            && s is { Length: > 0 and <= 12 }
            && s.All(char.IsAsciiDigit)
            && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        return 0;
    }
}

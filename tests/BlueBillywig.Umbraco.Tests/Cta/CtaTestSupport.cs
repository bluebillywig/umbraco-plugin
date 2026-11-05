using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Cta;

namespace BlueBillywig.Umbraco.Tests.Cta;

/// <summary>Sequential ids (w1, w2, ...) so tests do not depend on randomness.</summary>
internal sealed class SeqIds : ICtaIdGenerator
{
    private int _n;

    public string Next(string prefix) => prefix + (++_n).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal static class T
{
    public static CtaTranslator Translator() => new(new SeqIds());

    public static JsonObject Config0(JsonNode timeline) => (JsonObject)timeline["widgets"]![0]!["statesets"]![0]!["states"]![0]!["config"]!;

    public static JsonObject Config(JsonNode timeline, int widget) => (JsonObject)timeline["widgets"]![widget]!["statesets"]![0]!["states"]![0]!["config"]!;

    /// <summary>The way a stored timeline comes back from SAPI: text, then parsed.</summary>
    public static JsonNode Reload(JsonNode timeline) => JsonNode.Parse(CtaTranslator.Serialize(timeline))!;
}

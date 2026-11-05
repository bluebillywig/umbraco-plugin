using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlueBillywig.Umbraco.Sapi;

/// <summary>
/// Reads SAPI JSON the way the reference plugin's PHP does: <c>empty()</c>, <c>(string)</c>, <c>floatval()</c> and
/// <c>intval()</c> casts on values that may come back as strings or numbers. SAPI is loose about types, so ports
/// that compare against these helpers behave like the plugin on the same payload.
/// </summary>
internal static partial class SapiJson
{
    [GeneratedRegex(@"^\s*[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPrefix();

    /// <summary>PHP <c>empty()</c>: null, false, 0, "", "0", [] and {} are empty.</summary>
    public static bool PhpEmpty(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return true;
            case JsonArray a:
                return a.Count == 0;
            case JsonObject o:
                return o.Count == 0;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined:
                        return true;
                    case JsonValueKind.True:
                        return false;
                    case JsonValueKind.Number:
                        return Num(v, 0) == 0;
                    case JsonValueKind.String:
                        return PhpEmpty(v.GetValue<string>());
                }

                return false;
            default:
                return false;
        }
    }

    public static bool PhpEmpty(string? text) => string.IsNullOrEmpty(text) || text == "0";

    /// <summary>PHP <c>(string)</c> for scalars. Arrays and objects give "".</summary>
    public static string Str(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return "";
        }

        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.True => "1",
            _ => "",
        };
    }

    /// <summary>PHP <c>floatval()</c>: numbers, or the numeric prefix of a string. <paramref name="fallback"/> when absent.</summary>
    public static double Num(JsonNode? node, double fallback)
    {
        if (node is not JsonValue v)
        {
            return fallback;
        }

        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                // Through the JSON text: TryGetValue<double> refuses a value that was created from an int or long.
                return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : fallback;
            case JsonValueKind.String:
                return ParsePrefix(v.GetValue<string>());
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return fallback;
        }
    }

    /// <summary>The numeric prefix of a string as PHP's <c>floatval</c> reads it ("10%" is 10, "abc" is 0).</summary>
    public static double ParsePrefix(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        Match m = NumericPrefix().Match(text);
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : 0;
    }

    /// <summary>PHP <c>intval()</c> on a JSON scalar; 0 when it is not numeric.</summary>
    public static long Int(JsonNode? node)
    {
        double d = Num(node, 0);
        return d is > long.MinValue and < long.MaxValue ? (long)Math.Truncate(d) : 0;
    }

    /// <summary>Safe path walk: string keys for objects, ints for arrays. Null at the first miss or type mismatch.</summary>
    public static JsonNode? At(JsonNode? node, params object[] path)
    {
        JsonNode? cursor = node;
        foreach (object key in path)
        {
            switch (cursor)
            {
                case JsonObject o when key is string s:
                    cursor = o.TryGetPropertyValue(s, out JsonNode? next) ? next : null;
                    break;
                case JsonArray a when key is int i:
                    cursor = i >= 0 && i < a.Count ? a[i] : null;
                    break;
                default:
                    return null;
            }

            if (cursor is null)
            {
                return null;
            }
        }

        return cursor;
    }
}

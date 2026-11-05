using System.Globalization;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Analytics;

/// <summary>What the widget's four SAPI calls came to. <see cref="Errors"/> maps each failed section to a message.</summary>
internal sealed record WidgetAnalysis(
    long TotalViews,
    long TotalInits,
    IReadOnlyList<AnalyticsFacetResponseModel> TopVideos,
    IReadOnlyList<AnalyticsFacetResponseModel> Devices,
    IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// Reads SAPI's analytics envelope <c>{ type, body: { total, facets: { name: [ { value, count } ] } } }</c>. A port of
/// the plugin's <c>Admin::analyze_widget_data</c>, kept pure so the envelope contract can be locked down in tests: reading
/// the wrong level silently renders zeros, which is the bug the plugin shipped in 2.0.1.
/// </summary>
internal static class AnalyticsParser
{
    public const string Views = "views";
    public const string Inits = "inits";
    public const string TopVideos = "top_videos";
    public const string Devices = "devices";

    /// <summary>
    /// A failed call is an entry in <c>Errors</c>, distinct from a genuine zero. A call that was not made (null) or
    /// answered without the expected keys counts as zero and is not an error, as in the plugin.
    /// </summary>
    public static WidgetAnalysis AnalyzeWidgetData(SapiResult? views, SapiResult? topVideos, SapiResult? inits, SapiResult? devices)
    {
        var errors = new Dictionary<string, string>();
        long totalViews = Total(views, Views, errors);
        long totalInits = Total(inits, Inits, errors);
        IReadOnlyList<AnalyticsFacetResponseModel> top = Facet(topVideos, TopVideos, "title", errors);
        IReadOnlyList<AnalyticsFacetResponseModel> device = Facet(devices, Devices, "deviceType", errors);
        return new WidgetAnalysis(totalViews, totalInits, top, device, errors);
    }

    /// <summary>Views as a percentage of player loads, one decimal; 0 without player loads (the widget's rule).</summary>
    public static double PlayRatePercent(long views, long inits) =>
        inits > 0 ? Math.Round((double)views / inits * 100, 1, MidpointRounding.AwayFromZero) : 0;

    public static IReadOnlyList<DeviceShareResponseModel> DeviceShares(IReadOnlyList<AnalyticsFacetResponseModel> devices)
    {
        // The plugin's bar and legend show the first five and divide by the sum of all rows it was given.
        long total = devices.Sum(d => d.Count);
        return devices
            .Take(5)
            .Select(d => new DeviceShareResponseModel
            {
                Name = d.Value,
                Count = d.Count,
                Percent = total > 0 ? Math.Round((double)d.Count / total * 100, 1, MidpointRounding.AwayFromZero) : 0,
            })
            .ToList();
    }

    /// <summary>The count under <c>body.total</c>, or 0.</summary>
    public static long BodyTotal(SapiResult? result) => result is { Ok: true } ? LibraryMapper.Int(Body(result)?["total"]) : 0;

    public static JsonObject? Body(SapiResult? result) => result is { Ok: true } ? result.Json?["body"] as JsonObject : null;

    /// <summary>Rows <c>{ value, count }</c> of <c>body.facets.{name}</c>. A row without a value is dropped; nothing is ever read from another level.</summary>
    public static IReadOnlyList<AnalyticsFacetResponseModel> FacetRows(SapiResult? result, string facet)
    {
        if (Body(result)?["facets"]?[facet] is not JsonArray rows)
        {
            return [];
        }

        var list = new List<AnalyticsFacetResponseModel>();
        foreach (JsonObject row in rows.OfType<JsonObject>())
        {
            string value = Text(row["value"]);
            if (value.Length == 0)
            {
                continue;
            }

            list.Add(new AnalyticsFacetResponseModel { Value = value, Count = LibraryMapper.Int(row["count"]) });
        }

        return list;
    }

    /// <summary>
    /// <c>avgViewTime</c> is a scalar in practice, but the plugin defends against it arriving as a <c>{ value }</c> bucket.
    /// </summary>
    public static double? AvgViewTime(SapiResult? views)
    {
        JsonNode? node = Body(views)?["facets"]?["avgViewTime"];
        if (node is JsonObject bucket)
        {
            node = bucket["value"];
        }

        return node is JsonValue v && (v.TryGetValue(out double d) || (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))) && double.IsFinite(d) ? d : null;
    }

    /// <summary>
    /// SAPI's <c>exactPercentageViewed</c> says how many viewers stopped at each percentage. The retention curve is the
    /// cumulative sum from the right: viewers at X is the sum of the counts at X and above. Malformed rows are skipped, a
    /// percentage is rounded and clamped to 0..100.
    /// </summary>
    public static IReadOnlyList<long> Retention(SapiResult? views)
    {
        var counts = new long[101];
        if (Body(views)?["facets"]?["exactPercentageViewed"] is JsonArray rows)
        {
            foreach (JsonObject row in rows.OfType<JsonObject>())
            {
                if (!TryNumber(row["value"], out double pct) || !TryNumber(row["count"], out double count) || count < 0)
                {
                    continue;
                }

                counts[(int)Math.Clamp(Math.Round(pct, MidpointRounding.AwayFromZero), 0, 100)] += (long)count;
            }
        }

        var cumulative = new long[101];
        long running = 0;
        for (int i = 100; i >= 0; i--)
        {
            running += counts[i];
            cumulative[i] = running;
        }

        return cumulative;
    }

    private static long Total(SapiResult? result, string section, Dictionary<string, string> errors)
    {
        if (result is { Ok: false })
        {
            errors[section] = result.Error ?? "failed";
            return 0;
        }

        return BodyTotal(result);
    }

    private static IReadOnlyList<AnalyticsFacetResponseModel> Facet(SapiResult? result, string section, string facet, Dictionary<string, string> errors)
    {
        if (result is { Ok: false })
        {
            errors[section] = result.Error ?? "failed";
            return [];
        }

        return FacetRows(result, facet);
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && (v.TryGetValue(out value) || (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))) && double.IsFinite(value);
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue v ? (v.TryGetValue(out string? s) ? s?.Trim() ?? "" : v.ToString()) : "";
}

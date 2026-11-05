using System.Globalization;
using System.Text.Json;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Analytics;

/// <summary>A value, or the SAPI sections that stopped it. A failed section is never turned into zero views.</summary>
public sealed record AnalyticsOutcome<T>(T? Value, IReadOnlyList<string> FailedSections) where T : class
{
    public bool Ok => Value is not null;
}

public interface IAnalyticsService
{
    /// <summary>Views, player loads, play rate, the five most viewed videos and the device split of the last seven days.</summary>
    Task<AnalyticsOutcome<DashboardAnalyticsResponseModel>> GetDashboardAsync(CancellationToken ct);

    /// <summary>Engagement for one clip over the last <paramref name="days"/> days (1 to 365).</summary>
    Task<AnalyticsOutcome<ClipAnalyticsResponseModel>> GetClipAsync(long clipId, int days, CancellationToken ct);
}

/// <summary>
/// Port of the plugin's dashboard widget data (<c>render_dashboard_widget</c>) and <c>SapiClient::getClipAnalytics</c>.
/// <para>
/// Every call goes through <see cref="IBlueBillywigClient.GetAsync"/> with a cache time, which stores only successful
/// answers: a failure is shown as a failure and retried on the next request, never cached (the plugin's rule that a
/// widget with any failed call is not stored). The cache lasts five minutes, the plugin's transient.
/// </para>
/// <para>
/// Deviation from the plugin: dates are sent as <c>yyyy-MM-ddT00:00:00Z</c>. The plugin sends a bare date, and the dev
/// publication's SAPI answers 400 to that and to PHP-style <c>facet[0]=</c> arrays, but accepts this form together with
/// JSON text for <c>facet</c> and <c>facetconfig</c>.
/// </para>
/// </summary>
internal sealed class AnalyticsService : IAnalyticsService
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    internal const int MaxDays = 365;

    private readonly IBlueBillywigClient _client;
    private readonly TimeProvider _time;

    public AnalyticsService(IBlueBillywigClient client, TimeProvider time)
    {
        _client = client;
        _time = time;
    }

    public async Task<AnalyticsOutcome<DashboardAnalyticsResponseModel>> GetDashboardAsync(CancellationToken ct)
    {
        (string from, string to) = Window(7);
        var plain = new Dictionary<string, string> { ["fromDate"] = from, ["toDate"] = to };

        Task<SapiResult> views = _client.GetAsync("/sapi/analytics/views/mediaclip", plain, CacheFor, ct);
        Task<SapiResult> top = _client.GetAsync("/sapi/analytics/views/mediaclip", With(plain, "title", 5), CacheFor, ct);
        Task<SapiResult> inits = _client.GetAsync("/sapi/analytics/inits/mediaclip", plain, CacheFor, ct);
        Task<SapiResult> devices = _client.GetAsync("/sapi/analytics/views/mediaclip", With(plain, "deviceType", 5), CacheFor, ct);
        await Task.WhenAll(views, top, inits, devices);

        WidgetAnalysis analysis = AnalyticsParser.AnalyzeWidgetData(views.Result, top.Result, inits.Result, devices.Result);
        if (analysis.Errors.Count > 0)
        {
            return new(null, analysis.Errors.Keys.ToList());
        }

        return new(new DashboardAnalyticsResponseModel
        {
            TotalViews = analysis.TotalViews,
            TotalInits = analysis.TotalInits,
            PlayRate = AnalyticsParser.PlayRatePercent(analysis.TotalViews, analysis.TotalInits),
            TopVideos = analysis.TopVideos.Take(5).ToList(),
            Devices = AnalyticsParser.DeviceShares(analysis.Devices),
            From = from[..10],
            To = to[..10],
        }, []);
    }

    public async Task<AnalyticsOutcome<ClipAnalyticsResponseModel>> GetClipAsync(long clipId, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, MaxDays);
        (string from, string to) = Window(days);

        var viewQuery = new Dictionary<string, string>
        {
            ["fromDate"] = from,
            ["toDate"] = to,
            ["facet"] = JsonSerializer.Serialize(new[] { "exactPercentageViewed", "avgViewTime", "country", "deviceType" }),
            ["facetconfig"] = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["exactPercentageViewed"] = new { limit = 101 },
                ["country"] = new { limit = 10 },
                ["deviceType"] = new { limit = 10 },
            }),
        };
        var initQuery = new Dictionary<string, string> { ["fromDate"] = from, ["toDate"] = to };

        string id = clipId.ToString(CultureInfo.InvariantCulture);
        Task<SapiResult> viewsTask = _client.GetAsync($"/sapi/analytics/views/mediaclip/{id}", viewQuery, CacheFor, ct);
        Task<SapiResult> initsTask = _client.GetAsync($"/sapi/analytics/inits/mediaclip/{id}", initQuery, CacheFor, ct);
        await Task.WhenAll(viewsTask, initsTask);
        SapiResult views = viewsTask.Result;
        SapiResult inits = initsTask.Result;

        if (!views.Ok)
        {
            return new(null, [AnalyticsParser.Views]);
        }

        // A failed player-load call leaves the play rate unknown, and the response says so; the rest is still good.
        long viewTotal = AnalyticsParser.BodyTotal(views);
        long initTotal = AnalyticsParser.BodyTotal(inits);
        return new(new ClipAnalyticsResponseModel
        {
            Views = viewTotal,
            Inits = initTotal,
            PlayRate = initTotal > 0 ? (double)viewTotal / initTotal : null,
            AvgViewTime = AnalyticsParser.AvgViewTime(views),
            Retention = AnalyticsParser.Retention(views),
            Countries = AnalyticsParser.FacetRows(views, "country"),
            Devices = AnalyticsParser.FacetRows(views, "deviceType"),
            InitsFailed = !inits.Ok,
            From = from[..10],
            To = to[..10],
            Days = days,
        }, []);
    }

    /// <summary>The last <paramref name="days"/> days ending today, both at midnight UTC.</summary>
    private (string From, string To) Window(int days)
    {
        DateTime today = _time.GetUtcNow().UtcDateTime.Date;
        return (Format(today.AddDays(-days)), Format(today));
    }

    private static string Format(DateTime day) => day.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);

    private static Dictionary<string, string> With(Dictionary<string, string> plain, string facet, int limit) =>
        new(plain)
        {
            ["facet"] = JsonSerializer.Serialize(new[] { facet }),
            ["facetconfig"] = JsonSerializer.Serialize(new Dictionary<string, object> { [facet] = new { limit } }),
        };
}

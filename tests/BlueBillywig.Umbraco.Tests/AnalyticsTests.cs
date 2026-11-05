using System.Net;
using System.Text;
using BlueBillywig.Umbraco.Analytics;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// Ports of the plugin's <c>AnalyzeWidgetDataTest</c>: the SAPI analytics envelope is <c>{ type, body: { total, facets } }</c>
/// and a facet row is <c>{ value, count }</c>. Reading any other level silently renders zeros, which is what the plugin
/// shipped in 2.0.1. The last test of the first group replays an envelope recorded from bb.dev on 2026-11-04.
/// </summary>
public class AnalyticsParserTests
{
    private static SapiResult Views(long total, string? facetsJson = null)
    {
        string facets = facetsJson is null ? "" : ",\"facets\":" + facetsJson;
        return FakeClient.Ok("{\"type\":\"AnalyticsResponse\",\"subtype\":\"views\",\"body\":{\"total\":" + total + ",\"otype\":\"mediaclip\"" + facets + "}}");
    }

    private static SapiResult Boom(string message = "SAPI timeout") => SapiResult.Failure(0, message);

    [Fact]
    public void All_four_successful_calls_fill_every_field()
    {
        WidgetAnalysis result = AnalyticsParser.AnalyzeWidgetData(
            Views(247),
            Views(247, """{"title":[{"value":"Clip A","count":32},{"value":"Clip B","count":17}]}"""),
            Views(307),
            Views(247, """{"deviceType":[{"value":"Desktop","count":157},{"value":"Smartphone","count":88}]}"""));

        Assert.Empty(result.Errors);
        Assert.Equal(247, result.TotalViews);
        Assert.Equal(307, result.TotalInits);
        Assert.Equal(2, result.TopVideos.Count);
        Assert.Equal("Clip A", result.TopVideos[0].Value);
        Assert.Equal(32, result.TopVideos[0].Count);
        Assert.Equal("Desktop", result.Devices[0].Value);
    }

    [Fact]
    public void The_old_broken_shape_reads_as_zeros_without_errors()
    {
        // 2.0.1 read count/facets at the top level and 'val' for the row value. A 200 in that shape is zeros, not an error.
        WidgetAnalysis result = AnalyticsParser.AnalyzeWidgetData(
            FakeClient.Ok("""{"count":9999}"""),
            FakeClient.Ok("""{"facets":{"title":[{"val":"X","count":1}]}}"""),
            FakeClient.Ok("""{"count":9999}"""),
            FakeClient.Ok("""{"facets":{"deviceType":[{"val":"Desktop","count":1}]}}"""));

        Assert.Empty(result.Errors);
        Assert.Equal(0, result.TotalViews);
        Assert.Equal(0, result.TotalInits);
        Assert.Empty(result.TopVideos);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void A_failed_views_call_is_an_error_distinct_from_zero_traffic()
    {
        WidgetAnalysis result = AnalyticsParser.AnalyzeWidgetData(Boom(), Views(0), Views(0), Views(0));

        Assert.Equal("SAPI timeout", result.Errors["views"]);
        Assert.False(result.Errors.ContainsKey("inits"));
        Assert.False(result.Errors.ContainsKey("top_videos"));
        Assert.False(result.Errors.ContainsKey("devices"));
        Assert.Equal(0, result.TotalViews);
    }

    [Fact]
    public void A_partial_failure_records_each_failed_section()
    {
        WidgetAnalysis result = AnalyticsParser.AnalyzeWidgetData(
            Views(100),
            Views(100, """{"title":[{"value":"OnlyOne","count":100}]}"""),
            Boom("inits 500"),
            Boom("devices 500"));

        Assert.Equal(2, result.Errors.Count);
        Assert.True(result.Errors.ContainsKey("inits"));
        Assert.True(result.Errors.ContainsKey("devices"));
        Assert.Equal(100, result.TotalViews);
        Assert.Equal(0, result.TotalInits);
        Assert.NotEmpty(result.TopVideos);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void Missing_calls_default_to_zero_without_errors()
    {
        WidgetAnalysis result = AnalyticsParser.AnalyzeWidgetData(null, null, null, null);

        Assert.Equal(0, result.TotalViews);
        Assert.Equal(0, result.TotalInits);
        Assert.Empty(result.TopVideos);
        Assert.Empty(result.Devices);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Facet_rows_use_value_and_count()
    {
        WidgetAnalysis result = AnalyticsParser.AnalyzeWidgetData(
            Views(10),
            Views(10, """{"title":[{"value":"Real Title","count":10}]}"""),
            Views(10),
            Views(10));

        Assert.Equal("Real Title", result.TopVideos[0].Value);
        Assert.Equal(10, result.TopVideos[0].Count);
    }

    [Fact]
    public void The_envelope_recorded_from_bb_dev_is_read_as_numbers()
    {
        // Verbatim shape of GET /sapi/analytics/views/mediaclip with facet=["deviceType"], 2026-11-04 (values trimmed).
        SapiResult devices = FakeClient.Ok("""
            {"type":"AnalyticsResponse","subtype":"views","subject":{"otype":"mediaclip","oid":null},
             "period":{"fromDate":"2026-10-05T00:00:00Z","toDate":"2026-11-04T00:00:00Z"},
             "body":{"total":79,"otype":"mediaclip","items":[],
                     "facets":{"deviceType":[{"count":42,"value":"Smartphone"},{"count":37,"value":"Desktop"}]},
                     "updatedtime":"2026-11-04T15:05:39Z"}}
            """);

        WidgetAnalysis result = AnalyticsParser.AnalyzeWidgetData(devices, null, null, devices);

        Assert.Equal(79, result.TotalViews);
        Assert.Equal(["Smartphone", "Desktop"], result.Devices.Select(d => d.Value));
        IReadOnlyList<DeviceShareResponseModel> shares = AnalyticsParser.DeviceShares(result.Devices);
        Assert.Equal(53.2, shares[0].Percent);
        Assert.Equal(46.8, shares[1].Percent);
    }

    [Theory]
    [InlineData(41, 7, 585.7)]
    [InlineData(5, 3, 166.7)]
    [InlineData(0, 0, 0)]
    [InlineData(10, 0, 0)]
    public void Play_rate_is_a_one_decimal_percentage_and_zero_without_player_loads(long views, long inits, double expected) =>
        Assert.Equal(expected, AnalyticsParser.PlayRatePercent(views, inits));

    [Fact]
    public void Device_shares_show_five_devices_and_divide_by_all_rows()
    {
        var rows = Enumerable.Range(1, 7).Select(i => new AnalyticsFacetResponseModel { Value = "d" + i, Count = 10 }).ToList();

        IReadOnlyList<DeviceShareResponseModel> shares = AnalyticsParser.DeviceShares(rows);

        Assert.Equal(5, shares.Count);
        Assert.All(shares, s => Assert.Equal(14.3, s.Percent));
    }

    [Fact]
    public void Retention_is_the_cumulative_sum_from_the_right()
    {
        // The same four viewers bb.dev returned for clip 11224 (stopped at 1, 16, 31 and 87 percent).
        SapiResult views = Views(4, """{"exactPercentageViewed":[{"count":1,"value":1},{"count":1,"value":16},{"count":1,"value":31},{"count":1,"value":87}]}""");

        IReadOnlyList<long> retention = AnalyticsParser.Retention(views);

        Assert.Equal(101, retention.Count);
        Assert.Equal(4, retention[0]);
        Assert.Equal(4, retention[1]);
        Assert.Equal(3, retention[2]);
        Assert.Equal(3, retention[16]);
        Assert.Equal(2, retention[17]);
        Assert.Equal(2, retention[31]);
        Assert.Equal(1, retention[32]);
        Assert.Equal(1, retention[87]);
        Assert.Equal(0, retention[88]);
        Assert.Equal(0, retention[100]);
    }

    [Fact]
    public void Retention_clamps_rounds_and_skips_malformed_rows()
    {
        SapiResult views = Views(9, """
            {"exactPercentageViewed":[
              {"count":2,"value":100},{"count":1,"value":150},{"count":1,"value":-5},{"count":1,"value":"49.6"},
              {"count":-3,"value":10},{"count":"x","value":20},{"count":4},"junk",{"count":1,"value":"abc"}]}
            """);

        IReadOnlyList<long> retention = AnalyticsParser.Retention(views);

        Assert.Equal(3, retention[100]); // 2 at 100 and 1 at 150, clamped
        Assert.Equal(4, retention[50]); // 49.6 rounds to 50
        Assert.Equal(5, retention[0]); // plus the -5, clamped to 0
        Assert.Equal(4, retention[50]);
    }

    [Theory]
    [InlineData("""{"avgViewTime":86.5}""", 86.5)]
    [InlineData("""{"avgViewTime":{"value":12.25,"count":3}}""", 12.25)]
    [InlineData("""{"avgViewTime":"7.5"}""", 7.5)]
    [InlineData("""{"avgViewTime":{"count":3}}""", null)]
    [InlineData("""{}""", null)]
    public void Average_view_time_accepts_a_scalar_and_the_bucket_shape(string facets, double? expected) =>
        Assert.Equal(expected, AnalyticsParser.AvgViewTime(Views(1, facets)));
}

public class AnalyticsServiceTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 15, 30, 12, TimeSpan.Zero);

    private static (AnalyticsService Service, FakeClient Client) Rig()
    {
        var client = new FakeClient();
        return (new AnalyticsService(client, new FixedTime(Now)), client);
    }

    private static string Query(FakeClient client, string path, string key, int index = 0) =>
        client.Calls.Where(c => c.Path == path).ElementAt(index).Request!.Query![key];

    /// <summary>The widget's four queries, answered with the numbers bb.dev gave on 2026-11-04.</summary>
    private static void AnswerDashboard(FakeClient client)
    {
        client.Handler = (_, path, request) =>
        {
            string facet = request?.Query?.GetValueOrDefault("facet") ?? "";
            if (path.Contains("/inits/")) return FakeClient.Ok("""{"body":{"total":7}}""");
            if (facet.Contains("title")) return FakeClient.Ok("""{"body":{"total":41,"facets":{"title":[{"count":31,"value":"A"},{"count":10,"value":"B"},{"count":5,"value":"C"},{"count":4,"value":"D"},{"count":2,"value":"E"},{"count":1,"value":"F"}]}}}""");
            if (facet.Contains("deviceType")) return FakeClient.Ok("""{"body":{"total":41,"facets":{"deviceType":[{"count":25,"value":"Smartphone"},{"count":16,"value":"Desktop"}]}}}""");
            return FakeClient.Ok("""{"body":{"total":41}}""");
        };
    }

    [Fact]
    public async Task The_dashboard_asks_for_seven_days_in_the_form_bb_dev_accepts_and_caches_five_minutes()
    {
        (AnalyticsService service, FakeClient client) = Rig();
        AnswerDashboard(client);

        AnalyticsOutcome<DashboardAnalyticsResponseModel> outcome = await service.GetDashboardAsync(default);

        Assert.True(outcome.Ok);
        Assert.Equal(4, client.Calls.Count);
        Assert.All(client.Calls, c => Assert.Equal("GET", c.Method));
        Assert.Equal("2026-10-28T00:00:00Z", Query(client, "/sapi/analytics/views/mediaclip", "fromDate"));
        Assert.Equal("2026-11-04T00:00:00Z", Query(client, "/sapi/analytics/views/mediaclip", "toDate"));
        Assert.Equal("""["title"]""", Query(client, "/sapi/analytics/views/mediaclip", "facet", 1));
        Assert.Equal("""{"title":{"limit":5}}""", Query(client, "/sapi/analytics/views/mediaclip", "facetconfig", 1));
        Assert.Equal("""["deviceType"]""", Query(client, "/sapi/analytics/views/mediaclip", "facet", 2));
        Assert.Equal(TimeSpan.FromMinutes(5), AnalyticsService.CacheFor);

        DashboardAnalyticsResponseModel d = outcome.Value!;
        Assert.Equal(41, d.TotalViews);
        Assert.Equal(7, d.TotalInits);
        Assert.Equal(585.7, d.PlayRate);
        Assert.Equal(5, d.TopVideos.Count); // the sixth row is not shown
        Assert.Equal("A", d.TopVideos[0].Value);
        Assert.Equal(["Smartphone", "Desktop"], d.Devices.Select(x => x.Name));
        Assert.Equal(61.0, d.Devices[0].Percent);
        Assert.Equal("2026-10-28", d.From);
        Assert.Equal("2026-11-04", d.To);
    }

    [Fact]
    public async Task A_failed_call_fails_the_widget_with_the_section_names_and_never_shows_zeros()
    {
        (AnalyticsService service, FakeClient client) = Rig();
        client.Handler = (_, path, _) => path.Contains("/inits/") ? SapiResult.Failure(400, "SAPI request failed (400)") : FakeClient.Ok("""{"body":{"total":3}}""");

        AnalyticsOutcome<DashboardAnalyticsResponseModel> outcome = await service.GetDashboardAsync(default);

        Assert.False(outcome.Ok);
        Assert.Null(outcome.Value);
        Assert.Equal(["inits"], outcome.FailedSections);
    }

    [Fact]
    public async Task Clip_analytics_combine_views_and_player_loads_like_the_plugin()
    {
        (AnalyticsService service, FakeClient client) = Rig();
        client.Handler = (_, path, _) => path.Contains("/inits/")
            ? FakeClient.Ok("""{"body":{"total":7,"facets":{"inits":7}}}""")
            : FakeClient.Ok("""{"body":{"total":4,"facets":{"deviceType":[{"count":3,"value":"Desktop"},{"count":1,"value":"Smartphone"}],"country":[{"count":4,"value":"Netherlands"}],"avgViewTime":86.5,"exactPercentageViewed":[{"count":1,"value":1},{"count":1,"value":16},{"count":1,"value":31},{"count":1,"value":87}]}}}""");

        AnalyticsOutcome<ClipAnalyticsResponseModel> outcome = await service.GetClipAsync(11224, 30, default);

        ClipAnalyticsResponseModel c = outcome.Value!;
        Assert.Equal(4, c.Views);
        Assert.Equal(7, c.Inits);
        Assert.Equal(4.0 / 7.0, c.PlayRate);
        Assert.Equal(86.5, c.AvgViewTime);
        Assert.Equal("Netherlands", c.Countries[0].Value);
        Assert.Equal("Desktop", c.Devices[0].Value);
        Assert.Equal(101, c.Retention.Count);
        Assert.False(c.InitsFailed);
        Assert.Equal("2026-10-05", c.From);
        Assert.Contains("/sapi/analytics/views/mediaclip/11224", client.Requests);
        Assert.Contains("/sapi/analytics/inits/mediaclip/11224", client.Requests);
        Assert.Equal("""["exactPercentageViewed","avgViewTime","country","deviceType"]""", Query(client, "/sapi/analytics/views/mediaclip/11224", "facet"));
        Assert.Equal("""{"exactPercentageViewed":{"limit":101},"country":{"limit":10},"deviceType":{"limit":10}}""", Query(client, "/sapi/analytics/views/mediaclip/11224", "facetconfig"));
    }

    [Fact]
    public async Task A_failed_player_load_call_leaves_the_play_rate_unknown_not_zero()
    {
        (AnalyticsService service, FakeClient client) = Rig();
        client.Handler = (_, path, _) => path.Contains("/inits/") ? SapiResult.Failure(502, "x") : FakeClient.Ok("""{"body":{"total":4}}""");

        ClipAnalyticsResponseModel c = (await service.GetClipAsync(1, 30, default)).Value!;

        Assert.True(c.InitsFailed);
        Assert.Null(c.PlayRate);
        Assert.Equal(4, c.Views);
    }

    [Fact]
    public async Task A_failed_views_call_fails_the_clip_analytics()
    {
        (AnalyticsService service, FakeClient client) = Rig();
        client.Handler = (_, _, _) => SapiResult.Failure(500, "boom");

        AnalyticsOutcome<ClipAnalyticsResponseModel> outcome = await service.GetClipAsync(1, 30, default);

        Assert.False(outcome.Ok);
        Assert.Equal(["views"], outcome.FailedSections);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(9999, 365)]
    public async Task Days_are_clamped_to_the_plugins_range(int asked, int used)
    {
        (AnalyticsService service, FakeClient client) = Rig();
        client.Handler = (_, _, _) => FakeClient.Ok("""{"body":{"total":0}}""");

        ClipAnalyticsResponseModel c = (await service.GetClipAsync(1, asked, default)).Value!;

        Assert.Equal(used, c.Days);
        Assert.Equal(Now.UtcDateTime.Date.AddDays(-used).ToString("yyyy-MM-dd") + "T00:00:00Z", Query(client, "/sapi/analytics/views/mediaclip/1", "fromDate"));
    }
}

/// <summary>
/// The real client, with a counting HTTP handler: a success is cached for five minutes, a failure is not cached and the
/// next call goes back to SAPI. This is the plugin's rule that a widget with a failed call is never stored.
/// </summary>
public class AnalyticsCachingTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Fail
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Analytics request failed with message: SECRET-INTERNAL", Encoding.UTF8, "text/plain") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"body":{"total":41}}""", Encoding.UTF8, "application/json") });
        }
    }

    private static (AnalyticsService Service, Handler Http, BlueBillywigClient Client) Rig()
    {
        var http = new Handler();
        var settings = new FakeSettings { Value = new BlueBillywigSettings { Publication = "demo", ApiId = "7", ApiSecret = "secret" } };
        var client = new BlueBillywigClient(settings, new MemoryCache(new MemoryCacheOptions()), NullLogger<BlueBillywigClient>.Instance, new HttpClient(http));
        return (new AnalyticsService(client, TimeProvider.System), http, client);
    }

    [Fact]
    public async Task A_success_is_served_from_the_cache_for_the_next_request()
    {
        (AnalyticsService service, Handler http, _) = Rig();

        await service.GetDashboardAsync(default);
        Assert.Equal(4, http.Calls);
        await service.GetDashboardAsync(default);
        Assert.Equal(4, http.Calls);
    }

    [Fact]
    public async Task Clearing_the_cache_makes_the_next_request_ask_again()
    {
        (AnalyticsService service, Handler http, BlueBillywigClient client) = Rig();

        await service.GetDashboardAsync(default);
        client.ClearCache();
        await service.GetDashboardAsync(default);

        Assert.Equal(8, http.Calls);
    }

    [Fact]
    public async Task A_failure_is_not_cached_and_recovery_is_seen_at_once()
    {
        (AnalyticsService service, Handler http, _) = Rig();
        http.Fail = true;

        AnalyticsOutcome<DashboardAnalyticsResponseModel> failed = await service.GetDashboardAsync(default);
        Assert.False(failed.Ok);
        Assert.Equal(4, http.Calls);

        http.Fail = false;
        AnalyticsOutcome<DashboardAnalyticsResponseModel> recovered = await service.GetDashboardAsync(default);

        Assert.True(recovered.Ok);
        Assert.Equal(41, recovered.Value!.TotalViews);
        Assert.Equal(8, http.Calls); // asked again for all four, nothing came from the cache
    }

    [Fact]
    public async Task The_error_response_names_sections_and_never_the_upstream_text()
    {
        (AnalyticsService service, Handler http, _) = Rig();
        http.Fail = true;
        var controller = new AnalyticsController(service);

        IActionResult result = await controller.Dashboard(default);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        var body = Assert.IsType<ProblemDetails>(status.Value);
        Assert.Equal("Unable to load analytics", body.Title);
        var sections = (IReadOnlyList<string>)body.Extensions["sections"]!;
        Assert.Equal(["views", "inits", "top_videos", "devices"], sections.OrderBy(s => s == "views" ? 0 : s == "inits" ? 1 : s == "top_videos" ? 2 : 3));
        Assert.DoesNotContain("SECRET-INTERNAL", System.Text.Json.JsonSerializer.Serialize(body));
    }

    [Fact]
    public async Task The_clip_endpoint_refuses_a_bad_id_without_asking_sapi()
    {
        (AnalyticsService service, Handler http, _) = Rig();
        var controller = new AnalyticsController(service);

        Assert.IsType<BadRequestObjectResult>(await controller.Clip(0, 30, default));
        Assert.IsType<BadRequestObjectResult>(await controller.Clip(1_000_000_000_000, 30, default));
        Assert.Equal(0, http.Calls);
    }
}

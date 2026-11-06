using System.Xml.Linq;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.Seo;
using BlueBillywig.Umbraco.Tests.Gate;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests.Seo;

internal sealed class FakePages : ISitemapPageSource
{
    public List<SitemapPageCandidate> Pages { get; } = [];

    public int Calls { get; private set; }

    /// <summary>The sites the pages were asked for, in order.</summary>
    public List<SitemapSite> Sites { get; } = [];

    /// <summary>When set, a build waits for it: lets a test hold a build open while other requests arrive.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<IReadOnlyList<SitemapPageCandidate>> GetPagesAsync(SitemapSite site, CancellationToken ct)
    {
        Calls++;
        Sites.Add(site);
        if (Hold is { } hold)
        {
            await hold.Task;
        }

        return Pages.ToList();
    }
}

/// <summary>Q3: the sitemap route's behaviour. Pages come from a fake source; the rules about what is left out are the service's.</summary>
public class VideoSitemapServiceTests
{
    private const string Cpp = "545179f0-1503-46d6-9cc7-041fab63fc48";
    private static readonly XNamespace Sm = VideoSitemap.SitemapNamespace;
    private static readonly XNamespace Vid = VideoSitemap.VideoNamespace;

    private readonly FakePages _pages = new();
    private readonly FakeClient _client = new();
    private readonly FakeSettings _settings = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly VideoSitemapService _service;

    public VideoSitemapServiceTests()
    {
        _client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        _service = new VideoSitemapService(_pages, _client, _settings, _cache, NullLogger<VideoSitemapService>.Instance);
    }

    private static SitemapSite Site(string host) => new(new Uri("https://" + host));

    private static BlueBillywigVideo Clip(long id, string? publication = null) => new() { Type = BlueBillywigVideoType.Clip, Id = id, Publication = publication };

    private void Page(string url, params BlueBillywigVideo[] videos) => _pages.Pages.Add(new SitemapPageCandidate(url, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), videos));

    private void SapiClip(long id, string extra = "", string status = "published", string thumbs = "\"thumbnails\":[{\"src\":\"/t/{id}.jpg\",\"main\":true}]")
    {
        _client.Json($"/sapi/mediaclip/{id}", $$"""{"id":"{{id}}","title":"Film {{id}}","description":"About {{id}}","status":"{{status}}","length":125,"createddate":"2026-10-01T08:00:00Z",{{thumbs.Replace("{id}", id.ToString())}}{{extra}}}""");
    }

    private static XDocument Parse(SitemapResult result) => XDocument.Parse(result.Xml!);

    private static string[] Locs(XDocument doc) => doc.Descendants(Sm + "loc").Select(e => e.Value).ToArray();

    [Fact]
    public async Task A_public_published_clip_is_listed_with_its_page_player_thumbnail_and_duration()
    {
        SapiClip(1);
        Page("https://site.example/films/one", Clip(1));

        SitemapResult result = await _service.GetAsync(Site("site.example"), default);

        Assert.True(result.Ok);
        XDocument doc = Parse(result);
        Assert.Equal(["https://site.example/films/one"], Locs(doc));
        XElement video = doc.Descendants(Vid + "video").Single();
        Assert.Equal("Film 1", video.Element(Vid + "title")!.Value);
        Assert.Equal("https://cdn.example/t/1.jpg", video.Element(Vid + "thumbnail_loc")!.Value);
        Assert.Equal("https://demo.bbvms.com/p/default/c/1.html", video.Element(Vid + "player_loc")!.Value);
        Assert.Equal("125", video.Element(Vid + "duration")!.Value);
    }

    [Fact]
    public async Task A_clip_with_a_content_protection_policy_is_left_out_and_so_is_a_page_with_only_such_clips()
    {
        SapiClip(1, $",\"cpp\":\"{Cpp}\"");
        SapiClip(2);
        SapiClip(3, $",\"cpp\":\"{Cpp}\"");
        Page("https://site.example/gated", Clip(1));
        Page("https://site.example/mixed", Clip(2), Clip(3));

        SitemapResult result = await _service.GetAsync(Site("site.example"), default);

        XDocument doc = Parse(result);
        Assert.Equal(["https://site.example/mixed"], Locs(doc));
        Assert.Equal(["Film 2"], doc.Descendants(Vid + "title").Select(e => e.Value).ToArray());
        Assert.DoesNotContain("Film 1", result.Xml);
        Assert.DoesNotContain("Film 3", result.Xml);
        Assert.DoesNotContain(Cpp, result.Xml);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("archived")]
    [InlineData("")]
    public async Task A_clip_that_is_not_published_is_left_out(string status)
    {
        SapiClip(1, status: status);
        Page("https://site.example/a", Clip(1));

        Assert.Empty(Locs(Parse(await _service.GetAsync(Site("site.example"), default))));
    }

    [Fact]
    public async Task A_clip_without_a_thumbnail_a_deleted_clip_and_another_publications_clip_are_left_out_without_failing()
    {
        SapiClip(1, thumbs: "\"thumbnails\":[]");
        _client.Responses["/sapi/mediaclip/2"] = SapiResult.Failure(404, "SAPI request failed (404)");
        SapiClip(3);
        SapiClip(4);
        Page("https://site.example/a", Clip(1));
        Page("https://site.example/b", Clip(2));
        Page("https://site.example/c", Clip(3, "other-publication"));
        Page("https://site.example/d", Clip(4, "DEMO"));

        SitemapResult result = await _service.GetAsync(Site("site.example"), default);

        Assert.True(result.Ok);
        Assert.Equal(["https://site.example/d"], Locs(Parse(result)));
        Assert.DoesNotContain("/sapi/mediaclip/3", _client.Requests);
    }

    [Fact]
    public async Task Channels_and_shorts_are_never_listed()
    {
        Page("https://site.example/a", new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 5 }, new BlueBillywigVideo { Type = BlueBillywigVideoType.Shorts, Id = 6 });

        Assert.Empty(Locs(Parse(await _service.GetAsync(Site("site.example"), default))));
        Assert.DoesNotContain(_client.Requests, r => r.Contains("/5") || r.Contains("/6"));
    }

    [Fact]
    public async Task A_sapi_failure_is_incomplete_so_the_route_can_answer_503_and_nothing_is_cached()
    {
        _client.Responses["/sapi/mediaclip/1"] = SapiResult.Failure(500, "SAPI request failed (500)");
        Page("https://site.example/a", Clip(1));

        var time = new FakeTime();
        var service = new VideoSitemapService(_pages, _client, _settings, _cache, NullLogger<VideoSitemapService>.Instance, time);
        SitemapResult failed = await service.GetAsync(Site("site.example"), default);
        Assert.False(failed.Ok);

        // Recovery: once the minimum interval has passed the next call builds again instead of serving the outage.
        SapiClip(1);
        time.Advance(VideoSitemapService.MinRebuildInterval + TimeSpan.FromSeconds(1));
        SitemapResult recovered = await service.GetAsync(Site("site.example"), default);
        Assert.True(recovered.Ok);
        Assert.Equal(["https://site.example/a"], Locs(Parse(recovered)));
    }

    [Fact]
    public async Task Without_credentials_the_sitemap_is_an_empty_valid_urlset_and_is_not_cached()
    {
        _client.IsConnected = false;
        Page("https://site.example/a", Clip(1));

        SitemapResult result = await _service.GetAsync(Site("site.example"), default);

        Assert.True(result.Ok);
        Assert.Empty(Locs(Parse(result)));
        Assert.Equal("urlset", Parse(result).Root!.Name.LocalName);
        Assert.Equal(0, _pages.Calls);
        Assert.Empty(_client.Requests);

        // Connecting is visible at once: nothing about the unconfigured answer was stored.
        _client.IsConnected = true;
        SapiClip(1);
        Assert.Equal(["https://site.example/a"], Locs(Parse(await _service.GetAsync(Site("site.example"), default))));
    }

    [Fact]
    public async Task Without_credentials_it_logs_once_below_warning()
    {
        _client.IsConnected = false;
        var log = new ListLogger();
        var factory = new LoggerAdapter(log);
        var service = new VideoSitemapService(_pages, _client, _settings, _cache, factory);

        await service.GetAsync(Site("site.example"), default);
        await service.GetAsync(Site("site.example"), default);

        Assert.Single(log.Entries);
        Assert.True(log.Entries[0].Level < Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    private sealed class LoggerAdapter(Microsoft.Extensions.Logging.ILogger inner) : Microsoft.Extensions.Logging.ILogger<VideoSitemapService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    [Fact]
    public async Task A_configured_site_whose_sapi_cannot_be_reached_is_still_unavailable()
    {
        _client.Responses["/sapi/mediaclip/1"] = SapiResult.Failure(500, "SAPI request failed (500)");
        Page("https://site.example/a", Clip(1));

        Assert.False((await _service.GetAsync(Site("site.example"), default)).Ok);
    }

    [Fact]
    public async Task A_complete_sitemap_is_cached_per_host()
    {
        SapiClip(1);
        Page("https://site.example/a", Clip(1));

        await _service.GetAsync(Site("site.example"), default);
        await _service.GetAsync(Site("site.example"), default);
        Assert.Equal(1, _pages.Calls);

        await _service.GetAsync(Site("other.example"), default);
        Assert.Equal(2, _pages.Calls);
    }

    [Fact]
    public async Task Pages_are_built_for_the_canonical_site_that_was_asked_for()
    {
        SapiClip(1);
        Page("https://site.example/a", Clip(1));

        await _service.GetAsync(Site("site.example"), default);

        Assert.Equal("https://site.example", Assert.Single(_pages.Sites).Key);
    }

    [Fact]
    public async Task Requests_that_arrive_during_a_build_share_it()
    {
        SapiClip(1);
        Page("https://site.example/a", Clip(1));
        _pages.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<SitemapResult>[] requests = Enumerable.Range(0, 20).Select(_ => _service.GetAsync(Site("site.example"), default)).ToArray();
        await WaitUntil(() => _pages.Calls == 1);
        _pages.Hold.SetResult();
        SitemapResult[] results = await Task.WhenAll(requests);

        Assert.Equal(1, _pages.Calls);
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(1, _client.Requests.Count(r => r == "/sapi/mediaclip/1"));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_build_for_the_others()
    {
        SapiClip(1);
        Page("https://site.example/a", Clip(1));
        _pages.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var impatient = new CancellationTokenSource();

        Task<SitemapResult> first = _service.GetAsync(Site("site.example"), impatient.Token);
        Task<SitemapResult> second = _service.GetAsync(Site("site.example"), default);
        await WaitUntil(() => _pages.Calls == 1);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _pages.Hold.SetResult();

        Assert.True((await second).Ok);
    }

    [Fact]
    public async Task A_failed_build_is_not_repeated_for_every_request_but_only_after_the_minimum_interval()
    {
        var time = new FakeTime();
        var service = new VideoSitemapService(_pages, _client, _settings, _cache, NullLogger<VideoSitemapService>.Instance, time);
        _client.Responses["/sapi/mediaclip/1"] = SapiResult.Failure(500, "SAPI request failed (500)");
        Page("https://site.example/a", Clip(1));

        for (int i = 0; i < 25; i++)
        {
            Assert.False((await service.GetAsync(Site("site.example"), default)).Ok);
        }

        Assert.Equal(1, _pages.Calls);

        time.Advance(VideoSitemapService.MinRebuildInterval + TimeSpan.FromSeconds(1));
        await service.GetAsync(Site("site.example"), default);
        Assert.Equal(2, _pages.Calls);
    }

    [Fact]
    public async Task Each_canonical_site_has_one_cache_entry_and_one_build()
    {
        SapiClip(1);
        Page("https://site.example/a", Clip(1));

        for (int i = 0; i < 10; i++)
        {
            await _service.GetAsync(Site("site.example"), default);
            await _service.GetAsync(Site("other.example"), default);
        }

        Assert.Equal(2, _pages.Calls);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition was not reached in time");
    }

    [Fact]
    public async Task Hostile_clip_text_is_escaped_in_the_xml()
    {
        _client.Json("/sapi/mediaclip/1", """{"id":"1","title":"A </video:title><x/> & B","description":"<![CDATA[x]]>","status":"published","thumbnails":[{"src":"/t.jpg","main":true}]}""");
        Page("https://site.example/a", Clip(1));

        SitemapResult result = await _service.GetAsync(Site("site.example"), default);

        XDocument doc = Parse(result);
        Assert.Equal("A </video:title><x/> & B", doc.Descendants(Vid + "title").Single().Value);
        Assert.DoesNotContain("<x/>", result.Xml);
    }
}

public class VideoSitemapControllerTests
{
    private sealed class Fixed(SitemapResult result) : IVideoSitemapService
    {
        public SitemapSite? Site { get; private set; }

        public int Calls { get; private set; }

        public Task<SitemapResult> GetAsync(SitemapSite site, CancellationToken ct)
        {
            Site = site;
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Knows one site, whatever the request says about itself.</summary>
    private sealed class OneSite(string origin = "https://site.example", string known = "site.example") : ISitemapHostPolicy
    {
        public Task<SitemapSite?> ResolveAsync(HostString host, string scheme, CancellationToken ct) =>
            Task.FromResult<SitemapSite?>(host.Host.Equals(known, StringComparison.OrdinalIgnoreCase) ? new SitemapSite(new Uri(origin)) : null);
    }

    private static (VideoSitemapController Controller, DefaultHttpContext Http) Create(IVideoSitemapService service, string host = "Site.Example", ISitemapHostPolicy? hosts = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString(host);
        return (new VideoSitemapController(service, hosts ?? new OneSite()) { ControllerContext = new ControllerContext { HttpContext = http } }, http);
    }

    [Fact]
    public async Task A_good_sitemap_is_xml_with_a_public_cache_header_for_the_canonical_site()
    {
        var service = new Fixed(new SitemapResult("<urlset/>", true));
        var (controller, http) = Create(service);

        IActionResult result = await controller.ServeSitemap(default);

        ContentResult content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml; charset=utf-8", content.ContentType);
        Assert.Equal("<urlset/>", content.Content);
        Assert.Equal("public, max-age=600", http.Response.Headers.CacheControl.ToString());
        Assert.Equal("https://site.example", service.Site!.Key);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:8080")]
    [InlineData("site.example.evil.example")]
    [InlineData("203.0.113.9")]
    public async Task A_host_the_site_does_not_own_is_a_404_that_builds_nothing_and_is_not_cacheable(string host)
    {
        var service = new Fixed(new SitemapResult("<urlset/>", true));
        var (controller, http) = Create(service, host);

        IActionResult result = await controller.ServeSitemap(default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, service.Calls);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task An_unavailable_sitemap_is_503_with_retry_after_and_is_never_stored()
    {
        var (controller, http) = Create(new Fixed(new SitemapResult(null, false)));

        IActionResult result = await controller.ServeSitemap(default);

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal("300", http.Response.Headers.RetryAfter.ToString());
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task An_incomplete_sitemap_is_never_served_even_if_it_has_text()
    {
        var (controller, _) = Create(new Fixed(new SitemapResult("<urlset/>", false)));

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await controller.ServeSitemap(default)).StatusCode);
    }

    [Fact]
    public async Task An_absurd_host_header_is_a_bad_request()
    {
        var (controller, _) = Create(new Fixed(new SitemapResult("<urlset/>", true)), new string('a', 300));

        Assert.IsType<BadRequestResult>(await controller.ServeSitemap(default));
    }

    [Fact]
    public void The_route_is_the_documented_one_and_the_action_is_anonymous_on_the_action_not_the_class()
    {
        var action = typeof(VideoSitemapController).GetMethod(nameof(VideoSitemapController.ServeSitemap))!;

        Assert.Equal("/video-sitemap.xml", action.GetCustomAttributes(typeof(HttpGetAttribute), true).Cast<HttpGetAttribute>().Single().Template);
        Assert.NotEmpty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
        Assert.Empty(typeof(VideoSitemapController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
    }
}

/// <summary>Finds the clips in stored property values: the video property, block JSON and rich text.</summary>
public class VideoReferencesTests
{
    [Fact]
    public void The_video_property_json_is_found()
    {
        var found = VideoReferences.FindClips("""{"type":"clip","id":11224,"publication":"bb","playout":"default"}""");

        Assert.Equal(11224, Assert.Single(found).Id);
    }

    [Fact]
    public void Channels_and_shorts_are_not_clips()
    {
        Assert.Empty(VideoReferences.FindClips("""{"type":"channel","id":5}"""));
        Assert.Empty(VideoReferences.FindClips("""{"type":"shorts","id":5}"""));
    }

    [Fact]
    public void A_block_list_value_with_the_video_inside_a_block_is_found()
    {
        string blockList = """
            {"layout":{"Umbraco.BlockList":[{"contentKey":"a"}]},
             "contentData":[{"key":"a","contentTypeKey":"b","values":[{"alias":"video","value":{"type":"clip","id":77,"lazyLoad":false}},{"alias":"title","value":"x"}]}]}
            """;

        Assert.Equal(77, Assert.Single(VideoReferences.FindClips(blockList)).Id);
    }

    [Fact]
    public void A_pasted_url_in_rich_text_is_found_and_a_block_reference_too()
    {
        string rte = System.Text.Json.JsonSerializer.Serialize(new
        {
            markup = "<p>Hi</p><div class=\"umb-embed-holder\" data-embed-url=\"https://bb.bbvms.com/p/default/c/501.html\"><span class=\"bb-oembed-ref\">x</span></div>"
                + "<div class=\"bb-rte-video-ref\" data-bb-video='{\"type\":\"clip\",\"id\":502}'></div>",
            blocks = new { },
        });

        Assert.Equal([501L, 502L], VideoReferences.FindClips(rte).Select(v => v.Id).Order().ToArray());
    }

    [Fact]
    public void A_plain_markup_value_is_scanned_too()
    {
        Assert.Equal(9, Assert.Single(VideoReferences.FindClips("<div class=\"umb-embed-holder\" data-embed-url=\"https://bb.bbvms.com/view/default/9.html\"></div>")).Id);
    }

    [Fact]
    public void The_same_clip_twice_is_listed_once()
    {
        string json = """[{"type":"clip","id":1},{"type":"clip","id":1},{"type":"clip","id":2}]""";

        Assert.Equal([1L, 2L], VideoReferences.FindClips(json).Select(v => v.Id).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{"type":"clip","id":-1}""")]
    [InlineData("""{"type":"clip","id":"abc"}""")]
    [InlineData("plain text with no embeds")]
    public void Garbage_finds_nothing_and_does_not_throw(string? value)
    {
        Assert.Empty(VideoReferences.FindClips(value));
    }

    [Fact]
    public void Deeply_nested_json_is_cut_off_instead_of_overflowing()
    {
        string nested = new string('[', 5000) + new string(']', 5000);

        Assert.Empty(VideoReferences.FindClips(nested));
    }
}

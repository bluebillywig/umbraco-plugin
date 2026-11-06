using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BlueBillywig.Umbraco.Seo;

/// <summary>A published, publicly visible page and the clips it embeds, as found in its stored content.</summary>
public sealed record SitemapPageCandidate(string PageUrl, DateTimeOffset? LastModified, IReadOnlyList<BlueBillywigVideo> Videos);

/// <summary>
/// One site the sitemap is built for: a canonical origin (scheme and host, and port when the configuration names one) that
/// comes from the site's own configuration, never from the request. Its <see cref="Key"/> is the cache key.
/// </summary>
public sealed record SitemapSite(Uri Origin)
{
    public string Key => Origin.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
}

/// <summary>Where the pages come from. The Umbraco implementation reads the published content; tests supply their own.</summary>
public interface ISitemapPageSource
{
    /// <summary>
    /// Published pages with at least one clip embed, with absolute URLs built on <paramref name="site"/> (or on the domain
    /// Umbraco has bound to the page). Pages behind Public Access (member protection) are never returned.
    /// </summary>
    Task<IReadOnlyList<SitemapPageCandidate>> GetPagesAsync(SitemapSite site, CancellationToken ct);
}

/// <summary>The built sitemap, or the reason it is not available (the route answers 503, and nothing is cached).</summary>
public sealed record SitemapResult(string? Xml, bool Complete)
{
    public bool Ok => Xml is not null && Complete;
}

public interface IVideoSitemapService
{
    Task<SitemapResult> GetAsync(SitemapSite site, CancellationToken ct);
}

/// <summary>
/// Builds the video sitemap (Q3; plugin: <c>add_video_to_sitemap_entry</c>) from the pages of <see cref="ISitemapPageSource"/>
/// and SAPI. Rules, all of which leave something out rather than risk exposing it:
/// <list type="bullet">
/// <item>A clip with a content protection policy is left out, whatever the gate settings: its title, description and
/// thumbnail would be advertised to crawlers while the player is not. A page whose clips are all left out is left out.</item>
/// <item>A clip that SAPI does not report as published, has no title or thumbnail, or belongs to another publication than
/// the configured one (no metadata is available for it) is left out.</item>
/// <item>A site without credentials answers 200 with an empty, valid urlset (logged once at Information, never cached).</item>
/// <item>If SAPI is configured but cannot be asked (an outage), the result is incomplete: the route answers 503 and nothing
/// is cached, so a crawler keeps its last good copy instead of reading a sitemap emptied by an outage.</item>
/// </list>
/// A complete sitemap is cached for ten minutes, one entry per canonical site (see <see cref="ConfiguredSitemapHosts"/>: the
/// request's Host header never reaches a cache key or a URL). Building is expensive (every page is scanned, every clip asked
/// from SAPI), so it is single-flight: requests that arrive while a build runs share it, and after a build, successful or
/// not, the next one waits at least <see cref="MinRebuildInterval"/>; in between, the last result is served.
/// </summary>
public sealed class VideoSitemapService : IVideoSitemapService
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    /// <summary>The shortest time between the start of two builds for one site, whether the first succeeded or failed.</summary>
    public static readonly TimeSpan MinRebuildInterval = TimeSpan.FromSeconds(30);

    /// <summary>The longest one build may take. It is not tied to the request that started it.</summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);

    public const int MaxClips = 2000;
    private const int Parallelism = 8;

    private readonly ISitemapPageSource _pages;
    private readonly IBlueBillywigClient _client;
    private readonly IBlueBillywigSettingsStore _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<VideoSitemapService> _logger;
    private readonly TimeProvider _time;
    private readonly IClipPlayoutStore? _playouts;
    private int _loggedUnconfigured;
    private readonly object _flightLock = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _oneBuildAtATime = new(1, 1);

    /// <summary>What is known about one site's builds. Guarded by <see cref="_flightLock"/>.</summary>
    private sealed class Flight
    {
        public Task<SitemapResult>? Running { get; set; }

        public DateTimeOffset LastStarted { get; set; } = DateTimeOffset.MinValue;

        public SitemapResult? Last { get; set; }
    }

    public VideoSitemapService(ISitemapPageSource pages, IBlueBillywigClient client, IBlueBillywigSettingsStore settings, IMemoryCache cache, ILogger<VideoSitemapService> logger, TimeProvider? time = null, IClipPlayoutStore? playouts = null)
    {
        _time = time ?? TimeProvider.System;
        _playouts = playouts;
        _pages = pages;
        _client = client;
        _settings = settings;
        _cache = cache;
        _logger = logger;
    }

    public async Task<SitemapResult> GetAsync(SitemapSite site, CancellationToken ct)
    {
        // Not configured is not an outage: there is no Blue Billywig content to list, so the answer is a valid, empty urlset.
        // It is built per request (cheap) and never cached, so connecting is visible at once.
        if (!_client.IsConnected)
        {
            if (Interlocked.Exchange(ref _loggedUnconfigured, 1) == 0)
            {
                _logger.LogInformation("Blue Billywig video sitemap: SAPI is not configured, so /video-sitemap.xml answers an empty sitemap until it is.");
            }

            return new SitemapResult(VideoSitemap.Write([]), true);
        }

        string key = "BlueBillywig.VideoSitemap:" + site.Key;
        if (_cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            return new SitemapResult(cached, true);
        }

        Task<SitemapResult> work;
        lock (_flightLock)
        {
            if (!_flights.TryGetValue(site.Key, out Flight? flight))
            {
                _flights[site.Key] = flight = new Flight();
            }

            DateTimeOffset now = _time.GetUtcNow();
            if (flight.Running is { } running)
            {
                work = running; // join the build that is already under way
            }
            else if (flight.Last is { } last && now - flight.LastStarted < MinRebuildInterval)
            {
                return last; // too soon after the last build: what it found, a failure included
            }
            else
            {
                flight.LastStarted = now;
                // On the thread pool, not inline: the build must not run inside this lock, and it takes the lock again when done.
                work = flight.Running = Task.Run(() => RunBuildAsync(site, key, flight), CancellationToken.None);
            }
        }

        // The build belongs to nobody in particular: a caller that gives up only stops waiting.
        return await work.WaitAsync(ct);
    }

    private async Task<SitemapResult> RunBuildAsync(SitemapSite site, string key, Flight flight)
    {
        SitemapResult result;
        try
        {
            using var timeout = new CancellationTokenSource(BuildTimeout);
            await _oneBuildAtATime.WaitAsync(timeout.Token);
            try
            {
                result = await BuildAsync(site, timeout.Token);
            }
            finally
            {
                _oneBuildAtATime.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blue Billywig video sitemap: the build for {Site} did not finish.", site.Key);
            result = new SitemapResult(null, false);
        }

        if (result.Ok)
        {
            _cache.Set(key, result.Xml, CacheFor);
        }

        lock (_flightLock)
        {
            flight.Last = result;
            flight.Running = null;
        }

        return result;
    }

    internal async Task<SitemapResult> BuildAsync(SitemapSite site, CancellationToken ct)
    {
        BlueBillywigSettings settings = _settings.Get();
        IReadOnlyList<SitemapPageCandidate> pages = await _pages.GetPagesAsync(site, ct);

        // Only the configured publication has data this package can read.
        var wanted = new Dictionary<long, BlueBillywigVideo>();
        foreach (BlueBillywigVideo video in pages.SelectMany(p => p.Videos))
        {
            if (video.Type == BlueBillywigVideoType.Clip && SamePublication(video, settings) && wanted.Count < MaxClips)
            {
                wanted.TryAdd(video.Id, video);
            }
        }

        string assetBase = await ClipThumbnails.AssetBaseAsync(_client, ct);
        bool complete = true;
        var clips = new Dictionary<long, SitemapVideo?>();
        using var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(wanted.Values.Select(async video =>
        {
            await gate.WaitAsync(ct);
            try
            {
                (SitemapVideo? entry, bool ok) = await LoadAsync(video, settings, assetBase, ct);
                lock (clips)
                {
                    clips[video.Id] = entry;
                    complete &= ok;
                }
            }
            finally
            {
                gate.Release();
            }
        }));

        var result = new List<SitemapPage>();
        foreach (SitemapPageCandidate page in pages)
        {
            SitemapVideo[] videos = page.Videos
                .Where(v => clips.TryGetValue(v.Id, out SitemapVideo? e) && e is not null && SamePublication(v, settings))
                .Select(v => clips[v.Id]!)
                .ToArray();
            if (videos.Length > 0)
            {
                result.Add(new SitemapPage(page.PageUrl, videos, page.LastModified));
            }
        }

        return new SitemapResult(VideoSitemap.Write(result), complete);
    }

    private async Task<(SitemapVideo? Entry, bool Ok)> LoadAsync(BlueBillywigVideo video, BlueBillywigSettings settings, string assetBase, CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync($"/sapi/mediaclip/{video.Id}", cacheFor: TimeSpan.FromMinutes(5), ct: ct);
        if (!result.Ok)
        {
            // A clip that is gone is just not listed; anything else means the answer is not trustworthy this time.
            return (null, result.StatusCode == 404);
        }

        if (result.Json is not JsonObject clip || SapiJson.PhpEmpty(clip["id"]))
        {
            return (null, true);
        }

        // Fail closed on protection and on anything that is not plainly public.
        if (!SapiJson.PhpEmpty(clip["cpp"]) || !string.Equals(SapiJson.Str(clip["status"]), "published", StringComparison.OrdinalIgnoreCase))
        {
            return (null, true);
        }

        video = EmbedDefaults.WithClipPlayout(_playouts, video, true);
        string playout = video.Playout ?? (BlueBillywigVideo.IsValidPlayout(settings.Playout) ? settings.Playout : BlueBillywigVideo.DefaultClipPlayout);
        string playerUrl = BlueBillywigUrls.ClipPage(settings.ResolveBaseUrl(), playout, video.Id);
        string? src = ClipThumbnails.MainSrc(clip);
        string? thumbnail = src is null ? null : ClipThumbnails.Absolute(assetBase, src);
        return (VideoSitemap.FromClip(clip, playerUrl, thumbnail), true);
    }

    private static bool SamePublication(BlueBillywigVideo video, BlueBillywigSettings settings) =>
        string.IsNullOrEmpty(video.Publication) || video.Publication.Equals(settings.Publication, StringComparison.OrdinalIgnoreCase);
}

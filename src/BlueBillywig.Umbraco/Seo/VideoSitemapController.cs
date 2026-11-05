using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Seo;

/// <summary>
/// The video sitemap at <c>/video-sitemap.xml</c> (plugin: the video data it adds to the site's XML sitemap entries). It
/// is a separate file because Umbraco has no built-in sitemap to extend: list it in <c>robots.txt</c> or submit it in
/// Search Console. Deliberately anonymous, like the sitemap it is: crawlers cannot sign in. It carries only what a
/// public page already shows; the rules for what is left out are on <see cref="VideoSitemapService"/>.
/// </summary>
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class VideoSitemapController : ControllerBase
{
    public const string Route = "/video-sitemap.xml";

    private readonly IVideoSitemapService _sitemap;
    private readonly ISitemapHostPolicy _hosts;

    public VideoSitemapController(IVideoSitemapService sitemap, ISitemapHostPolicy hosts)
    {
        _sitemap = sitemap;
        _hosts = hosts;
    }

    // [AllowAnonymous] is on the action on purpose: the authorization sweep test lists this exact action, with the reason.
    [HttpGet(Route)]
    [AllowAnonymous]
    public async Task<IActionResult> ServeSitemap(CancellationToken ct)
    {
        string host = Request.Host.Value ?? "";
        if (host.Length is 0 or > 255)
        {
            return BadRequest();
        }

        // Only a host the site has configured gets an answer. The Host header is compared, never copied into a URL or a
        // cache key; a name the site does not own is a plain 404, which no cache is asked to keep.
        SitemapSite? site = await _hosts.ResolveAsync(Request.Host, Request.Scheme, ct);
        if (site is null)
        {
            Response.Headers.CacheControl = "no-store";
            return NotFound();
        }

        SitemapResult result = await _sitemap.GetAsync(site, ct);
        if (!result.Ok)
        {
            // Not an empty sitemap: a crawler would read that as "all videos removed". Come back later.
            Response.Headers.RetryAfter = "300";
            Response.Headers.CacheControl = "no-store";
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        Response.Headers.CacheControl = "public, max-age=600";
        return Content(result.Xml!, "application/xml; charset=utf-8");
    }
}

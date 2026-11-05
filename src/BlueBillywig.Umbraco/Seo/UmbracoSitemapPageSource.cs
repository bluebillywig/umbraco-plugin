using BlueBillywig.Umbraco.Embedding;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace BlueBillywig.Umbraco.Seo;

/// <summary>
/// The published pages that embed a clip, from Umbraco's published content. A page is left out when it has no template (it
/// cannot be viewed), has no URL, or is protected by Public Access: a member-only page must not be advertised to crawlers.
/// The content is read as stored, with the same detection as <see cref="VideoReferences"/>: the video property, blocks and
/// rich text embeds.
/// </summary>
internal sealed class UmbracoSitemapPageSource : ISitemapPageSource
{
    private const int MaxPagesScanned = 20_000;

    private readonly IDocumentNavigationQueryService _navigation;
    private readonly IPublishedContentCache _content;
    private readonly IPublishedUrlProvider _urls;
    private readonly IPublicAccessService _publicAccess;
    private readonly IUmbracoContextFactory _contextFactory;
    private readonly ILogger<UmbracoSitemapPageSource> _logger;

    public UmbracoSitemapPageSource(
        IDocumentNavigationQueryService navigation,
        IPublishedContentCache content,
        IPublishedUrlProvider urls,
        IPublicAccessService publicAccess,
        IUmbracoContextFactory contextFactory,
        ILogger<UmbracoSitemapPageSource> logger)
    {
        _navigation = navigation;
        _content = content;
        _urls = urls;
        _publicAccess = publicAccess;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SitemapPageCandidate>> GetPagesAsync(SitemapSite site, CancellationToken ct)
    {
        // The route is a plain controller, not an Umbraco route: the URL provider needs a context to exist.
        using var reference = _contextFactory.EnsureUmbracoContext();

        var keys = new List<Guid>();
        if (_navigation.TryGetRootKeys(out IEnumerable<Guid> roots))
        {
            foreach (Guid root in roots)
            {
                keys.Add(root);
                if (_navigation.TryGetDescendantsKeys(root, out IEnumerable<Guid> descendants))
                {
                    keys.AddRange(descendants);
                }
            }
        }

        var pages = new List<SitemapPageCandidate>();
        foreach (Guid key in keys.Take(MaxPagesScanned))
        {
            ct.ThrowIfCancellationRequested();
            IPublishedContent? content = await _content.GetByIdAsync(key, false);
            if (content is null || content.TemplateId is not > 0 || _publicAccess.IsProtected(content.Path).Success)
            {
                continue;
            }

            IReadOnlyList<BlueBillywigVideo> clips = content.Properties
                .SelectMany(p => VideoReferences.FindClips(p.GetSourceValue() as string))
                .DistinctBy(v => (v.Id, v.Publication))
                .ToList();
            if (clips.Count == 0)
            {
                continue;
            }

            // The canonical site stands in for "the current request": without it Umbraco would take the host from the Host
            // header of whoever asked. A page with a domain of its own keeps that domain.
            string url = _urls.GetUrl(content, UrlMode.Absolute, null, site.Origin);
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                _logger.LogDebug("Blue Billywig video sitemap: page {Key} has no absolute URL; left out.", key);
                continue;
            }

            pages.Add(new SitemapPageCandidate(url, content.UpdateDate, clips));
        }

        return pages;
    }
}

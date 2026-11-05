using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using BlueBillywig.Umbraco.Sapi;

namespace BlueBillywig.Umbraco.Seo;

/// <summary>One video of the Google video sitemap schema. Build it with <see cref="VideoSitemap.FromClip"/>.</summary>
public sealed record SitemapVideo(string Title, string Description, string ThumbnailUrl, string PlayerUrl, int? DurationSeconds, DateTimeOffset? PublicationDate);

/// <summary>A page of the site and the videos embedded in it.</summary>
public sealed record SitemapPage(string PageUrl, IReadOnlyList<SitemapVideo> Videos, DateTimeOffset? LastModified = null);

/// <summary>
/// The video XML sitemap (plugin: <c>add_video_to_sitemap_entry</c> and the USER-GUIDE "Video XML Sitemap"): a
/// <c>&lt;urlset&gt;</c> of pages, each with <c>&lt;video:video&gt;</c> entries from the Google video sitemap schema.
/// <para>
/// Everything is written through <see cref="XmlWriter"/>, which escapes text; characters XML 1.0 cannot carry (control
/// characters, lone surrogates) are removed first, because the writer would otherwise throw on them. Limits follow Google's
/// documentation: title 100 characters, description 2048, duration 1 to 28800 seconds, 1000 videos per page, 50000 pages.
/// A video without a usable thumbnail URL, player URL or title is left out (Google requires them; the plugin wrote an
/// empty <c>thumbnail_loc</c>), and a page with no valid video is left out.
/// </para>
/// <para>
/// Seam for the wiring: the route finds the published pages with embeds (the plugin used the first video per page,
/// <see cref="FromClip"/> can be called for more), looks the clips up, and passes absolute page URLs. Only published,
/// publicly visible, non-protected pages and clips belong here: a sitemap lists URLs for crawlers, so a page behind the
/// content gate must be left out by the caller.
/// </para>
/// </summary>
public static class VideoSitemap
{
    public const string SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    public const string VideoNamespace = "http://www.google.com/schemas/sitemap-video/1.1";
    public const int MaxTitle = 100;
    public const int MaxDescription = 2048;
    public const int MaxVideosPerPage = 1000;
    public const int MaxPages = 50_000;

    /// <summary>From a SAPI mediaclip: <c>title, description (falls back to the title), length, createddate</c>. Null when unusable.</summary>
    public static SitemapVideo? FromClip(JsonNode clip, string playerUrl, string? thumbnailUrl)
    {
        string title = SapiJson.Str(clip["title"]);
        string description = SapiJson.Str(clip["description"]);
        int? duration = SapiJson.PhpEmpty(clip["length"]) ? null : (int)Math.Clamp(Math.Truncate(SapiJson.Num(clip["length"], 0)), 0, int.MaxValue);
        DateTimeOffset? published = DateTimeOffset.TryParse(SapiJson.Str(clip["createddate"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d) ? d : null;
        return Normalize(new SitemapVideo(title, SapiJson.PhpEmpty(description) ? title : description, thumbnailUrl ?? "", playerUrl, duration, published));
    }

    /// <summary>The sitemap document as UTF-8 text with an XML declaration.</summary>
    public static string Write(IEnumerable<SitemapPage> pages)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = false,
            CheckCharacters = true,
        };

        using (XmlWriter xml = XmlWriter.Create(stream, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("urlset", SitemapNamespace);
            xml.WriteAttributeString("xmlns", "video", "http://www.w3.org/2000/xmlns/", VideoNamespace);

            int written = 0;
            foreach (SitemapPage page in pages)
            {
                if (written >= MaxPages)
                {
                    break;
                }

                string? loc = AbsoluteUrl(page.PageUrl);
                SitemapVideo[] videos = page.Videos.Select(Normalize).OfType<SitemapVideo>().Take(MaxVideosPerPage).ToArray();
                if (loc is null || videos.Length == 0)
                {
                    continue;
                }

                written++;
                xml.WriteStartElement("url", SitemapNamespace);
                xml.WriteElementString("loc", SitemapNamespace, loc);
                if (page.LastModified is { } modified)
                {
                    xml.WriteElementString("lastmod", SitemapNamespace, Iso(modified));
                }

                foreach (SitemapVideo v in videos)
                {
                    xml.WriteStartElement("video", "video", VideoNamespace);
                    xml.WriteElementString("thumbnail_loc", VideoNamespace, v.ThumbnailUrl);
                    xml.WriteElementString("title", VideoNamespace, v.Title);
                    xml.WriteElementString("description", VideoNamespace, v.Description);
                    xml.WriteElementString("player_loc", VideoNamespace, v.PlayerUrl);
                    if (v.DurationSeconds is int seconds)
                    {
                        xml.WriteElementString("duration", VideoNamespace, seconds.ToString(CultureInfo.InvariantCulture));
                    }

                    if (v.PublicationDate is { } date)
                    {
                        xml.WriteElementString("publication_date", VideoNamespace, Iso(date));
                    }

                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    /// <summary>Cleans a video to what the writer may emit. Null when a required part is missing or unusable.</summary>
    internal static SitemapVideo? Normalize(SitemapVideo video)
    {
        string title = Truncate(XmlClean(video.Title).Trim(), MaxTitle);
        string description = Truncate(XmlClean(video.Description).Trim(), MaxDescription);
        string? thumbnail = AbsoluteUrl(video.ThumbnailUrl);
        string? player = AbsoluteUrl(video.PlayerUrl);
        if (title.Length == 0 || thumbnail is null || player is null)
        {
            return null;
        }

        int? duration = video.DurationSeconds is int s && s is >= 1 and <= 28800 ? s : null;
        return new SitemapVideo(title, description.Length == 0 ? title : description, thumbnail, player, duration, video.PublicationDate);
    }

    /// <summary>The URL when it is absolute http(s) and has no whitespace or control characters, otherwise null.</summary>
    internal static string? AbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url.Any(c => c <= ' ' || c == '\u007f' || char.IsSurrogate(c)))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0 ? url : null;
    }

    /// <summary>Drops what XML 1.0 cannot carry: control characters other than tab, LF, CR, lone surrogates, U+FFFE and U+FFFF.</summary>
    internal static string XmlClean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
            }
            else if (!char.IsSurrogate(c) && XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Cuts at a limit without splitting a surrogate pair.</summary>
    internal static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        int cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut].TrimEnd();
    }

    private static string Iso(DateTimeOffset date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

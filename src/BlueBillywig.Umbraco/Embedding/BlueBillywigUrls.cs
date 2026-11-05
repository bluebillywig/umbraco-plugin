using System.Text.RegularExpressions;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>Player URLs and the pasted-URL shapes. The patterns are the reference plugin's <c>OEMBED_URL_PATTERNS</c>.</summary>
public static partial class BlueBillywigUrls
{
    /// <summary>Optional query string or fragment, which the reference plugin also keeps on a pasted URL.</summary>
    private const string Tail = @"(?:[?#]\S*)?$";

    private const string ClipPattern = @"^https?://([a-zA-Z0-9-]+)\.bbvms\.com/p/([a-zA-Z0-9_-]+)/c/(\d+)\.html" + Tail;
    private const string ViewPattern = @"^https?://([a-zA-Z0-9-]+)\.bbvms\.com/view/([a-zA-Z0-9_-]+)/(\d+)\.html" + Tail;
    private const string ChannelPattern = @"^https?://([a-zA-Z0-9-]+)\.bbvms\.com/ch/(\d+)\.html" + Tail;

    /// <summary>The regexes handed to Umbraco's embed provider.</summary>
    public static readonly string[] OEmbedPatterns = [ClipPattern, ViewPattern, ChannelPattern];

    private static readonly Regex Clip = new(ClipPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex View = new(ViewPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Channel = new(ChannelPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Escapes one path segment, so a value with slashes or dots cannot reach a different endpoint.</summary>
    public static string Segment(string value) => Uri.EscapeDataString(value);

    public static string ClipScript(string baseUrl, string playout, long id) => $"{baseUrl.TrimEnd('/')}/p/{Segment(playout)}/c/{id}.js";

    public static string ClipPage(string baseUrl, string playout, long id) => $"{baseUrl.TrimEnd('/')}/p/{Segment(playout)}/c/{id}.html";

    public static string PlaylistScript(string baseUrl, string playout, long id) => $"{baseUrl.TrimEnd('/')}/p/{Segment(playout)}/l/{id}.js";

    public static string PlaylistPage(string baseUrl, string playout, long id) => $"{baseUrl.TrimEnd('/')}/p/{Segment(playout)}/l/{id}.html";

    public static string ChannelScript(string baseUrl, long id) => $"{baseUrl.TrimEnd('/')}/ch/{id}.js";

    public static string ChannelPage(string baseUrl, long id) => $"{baseUrl.TrimEnd('/')}/ch/{id}.html";

    public static string ShortsPage(string baseUrl, long id) => $"{baseUrl.TrimEnd('/')}/sh/{id}.html";

    /// <summary>The player host for a publication that is not the configured one.</summary>
    public static string PublicationBaseUrl(string publication) => $"https://{publication}.bbvms.com";

    /// <summary>
    /// Maps a pasted <c>*.bbvms.com/p|view|ch/...</c> URL to a video. Anything else returns null. Unlike the
    /// reference plugin's handlers, which match anywhere in the string, these are anchored to the whole URL.
    /// </summary>
    public static BlueBillywigVideo? TryParseOEmbedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
        {
            return null;
        }

        string candidate = url.Trim();
        try
        {
            Match m = Clip.Match(candidate);
            if (!m.Success) m = View.Match(candidate);
            if (m.Success)
            {
                return Build(BlueBillywigVideoType.Clip, m.Groups[1].Value, m.Groups[3].Value, m.Groups[2].Value);
            }

            m = Channel.Match(candidate);
            return m.Success ? Build(BlueBillywigVideoType.Channel, m.Groups[1].Value, m.Groups[2].Value, null) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static BlueBillywigVideo? Build(BlueBillywigVideoType type, string publication, string idText, string? playout) =>
        long.TryParse(idText, out long id) && id is > 0 and < 1_000_000_000_000
            ? new BlueBillywigVideo
            {
                Type = type,
                Id = id,
                Publication = publication.ToLowerInvariant(),
                Playout = playout,
                // The reference plugin's oEmbed handler renders the player only: no transcript, no chapters.
                ShowTranscript = false,
                ShowChapters = false,
                LazyLoad = type == BlueBillywigVideoType.Clip,
                Height = BlueBillywigVideo.DefaultChannelHeight,
            }
            : null;
}

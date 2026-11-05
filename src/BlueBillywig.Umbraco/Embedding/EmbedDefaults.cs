using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// The lazy-load switches in <see cref="BlueBillywigOptions"/>, resolved. They apply only where the editor or the caller
/// did not choose: a stored property value or an explicit <c>lazy</c> attribute always wins (the plugin's
/// <c>lazy="false"</c> shortcode attribute beats its filter in the same way).
/// </summary>
internal static class EmbedDefaults
{
    /// <summary>A video parsed from a pasted URL: <c>bluebillywig_lazy_load_oembed</c> (clips) and <c>_oembed_channel</c>.</summary>
    public static BlueBillywigVideo ForOEmbed(BlueBillywigOptions? options, BlueBillywigVideo video) => video.Type switch
    {
        BlueBillywigVideoType.Channel => video with { LazyLoad = options?.LazyLoadOEmbedChannel ?? false },
        BlueBillywigVideoType.Clip => video with { LazyLoad = options?.LazyLoadOEmbed ?? true },
        _ => video,
    };

    /// <summary>A video built from an id in Razor: <c>bluebillywig_lazy_load_shortcode</c>. Channels stay eager unless asked.</summary>
    public static bool ForComponent(BlueBillywigOptions? options, BlueBillywigVideoType type) =>
        type == BlueBillywigVideoType.Clip && (options?.LazyLoadComponent ?? true);

    /// <summary>
    /// A video made from an id, as the tag helper and the view component do. <paramref name="lazy"/> is the caller's own
    /// choice; without one the <see cref="ForComponent"/> switch decides.
    /// </summary>
    public static BlueBillywigVideo FromId(BlueBillywigOptions? options, string? type, long id, string? playout, bool? lazy, bool? transcript, bool? chapters)
    {
        BlueBillywigVideoType kind = type?.ToLowerInvariant() switch
        {
            "channel" => BlueBillywigVideoType.Channel,
            "shorts" => BlueBillywigVideoType.Shorts,
            _ => BlueBillywigVideoType.Clip,
        };
        return new BlueBillywigVideo
        {
            Type = kind,
            Id = id,
            Playout = BlueBillywigVideo.IsValidPlayout(playout) ? playout : null,
            LazyLoad = lazy ?? ForComponent(options, kind),
            ShowTranscript = transcript ?? true,
            ShowChapters = chapters ?? false,
            Height = kind == BlueBillywigVideoType.Shorts ? BlueBillywigVideo.DefaultShortsHeight : BlueBillywigVideo.DefaultChannelHeight,
        };
    }

    /// <summary>
    /// Precedence for a clip's playout: the embed's own playout, then the one chosen for the clip in the library, then the
    /// site default (applied by the caller). The reference plugin reads its per-clip option when it renders; here every
    /// render path ends in the renderer, which calls this once. Only a clip of the configured publication is looked up, because
    /// the library stores playouts by clip id and the same id elsewhere is another clip. A stored value that is not a valid
    /// playout name, or a store that fails, counts as no choice.
    /// </summary>
    public static BlueBillywigVideo WithClipPlayout(IClipPlayoutStore? playouts, BlueBillywigVideo video, bool configuredPublication)
    {
        if (playouts is null || !configuredPublication || video.Type != BlueBillywigVideoType.Clip || video.Playout is not null || video.Id <= 0)
        {
            return video;
        }

        string? stored;
        try
        {
            stored = playouts.Get(video.Id);
        }
        catch (Exception)
        {
            return video;
        }

        return BlueBillywigVideo.IsValidPlayout(stored) ? video with { Playout = stored } : video;
    }
}

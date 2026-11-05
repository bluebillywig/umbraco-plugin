using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.PropertyEditors.DeliveryApi;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>Turns the stored JSON into <see cref="BlueBillywigVideo"/>, and into a small headless object for the Delivery API.</summary>
public sealed class BlueBillywigVideoPropertyValueConverter : PropertyValueConverterBase, IDeliveryApiPropertyValueConverter
{
    private readonly IBlueBillywigSettingsStore _settings;

    private readonly IClipPlayoutStore? _playouts;

    public BlueBillywigVideoPropertyValueConverter(IBlueBillywigSettingsStore settings, IClipPlayoutStore? playouts = null)
    {
        _settings = settings;
        _playouts = playouts;
    }

    public override bool IsConverter(IPublishedPropertyType propertyType) =>
        propertyType.EditorAlias == Constants.VideoEditorAlias;

    public override Type GetPropertyValueType(IPublishedPropertyType propertyType) => typeof(BlueBillywigVideo);

    public override PropertyCacheLevel GetPropertyCacheLevel(IPublishedPropertyType propertyType) => PropertyCacheLevel.Element;

    public override object? ConvertSourceToIntermediate(IPublishedElement owner, IPublishedPropertyType propertyType, object? source, bool preview) =>
        BlueBillywigVideo.Parse(source);

    public override object? ConvertIntermediateToObject(IPublishedElement owner, IPublishedPropertyType propertyType, PropertyCacheLevel referenceCacheLevel, object? inter, bool preview) =>
        inter as BlueBillywigVideo;

    public PropertyCacheLevel GetDeliveryApiPropertyCacheLevel(IPublishedPropertyType propertyType) => PropertyCacheLevel.Element;

    public Type GetDeliveryApiPropertyValueType(IPublishedPropertyType propertyType) => typeof(BlueBillywigVideoApiModel);

    /// <summary>
    /// Ids and player URLs only. Titles, thumbnails and transcripts need SAPI calls, which a converter must not
    /// make, so the output is the same shallow shape whether or not the property is expanded.
    /// </summary>
    public object? ConvertIntermediateToDeliveryApiObject(IPublishedElement owner, IPublishedPropertyType propertyType, PropertyCacheLevel referenceCacheLevel, object? inter, bool preview, bool expanding)
    {
        if (inter is not BlueBillywigVideo video)
        {
            return null;
        }

        BlueBillywigSettings settings = _settings.Get();
        string publication = video.Publication ?? settings.Publication;
        if (!BlueBillywigVideo.IsValidPublication(publication))
        {
            return new BlueBillywigVideoApiModel(BlueBillywigVideo.TypeToString(video.Type), video.Id, null, null, null, null, null, null, null);
        }

        bool configured = publication.Equals(settings.Publication, StringComparison.OrdinalIgnoreCase);
        video = EmbedDefaults.WithClipPlayout(_playouts, video, configured);
        string baseUrl = configured
            ? settings.ResolveBaseUrl()
            : BlueBillywigUrls.PublicationBaseUrl(publication.ToLowerInvariant());
        string playout = video.Playout ?? (BlueBillywigVideo.IsValidPlayout(settings.Playout) ? settings.Playout : BlueBillywigVideo.DefaultClipPlayout);

        return video.Type switch
        {
            BlueBillywigVideoType.Clip => new BlueBillywigVideoApiModel(
                "clip", video.Id, publication, playout,
                BlueBillywigUrls.ClipPage(baseUrl, playout, video.Id),
                BlueBillywigUrls.ClipScript(baseUrl, playout, video.Id),
                null, video.ShowTranscript, video.ShowChapters),
            BlueBillywigVideoType.Channel => new BlueBillywigVideoApiModel(
                "channel", video.Id, publication, null,
                BlueBillywigUrls.ChannelPage(baseUrl, video.Id),
                BlueBillywigUrls.ChannelScript(baseUrl, video.Id),
                video.Height, null, null),
            _ => new BlueBillywigVideoApiModel(
                "shorts", video.Id, publication, null,
                BlueBillywigUrls.ShortsPage(baseUrl, video.Id),
                null,
                video.Height, null, null),
        };
    }
}

/// <summary>The Delivery API shape of a Blue Billywig video property.</summary>
public sealed record BlueBillywigVideoApiModel(
    string Type,
    long Id,
    string? Publication,
    string? Playout,
    string? PlayerUrl,
    string? ScriptUrl,
    string? Height,
    bool? ShowTranscript,
    bool? ShowChapters);

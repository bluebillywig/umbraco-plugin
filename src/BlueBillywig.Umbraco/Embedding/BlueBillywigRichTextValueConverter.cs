using BlueBillywig.Umbraco.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.PropertyEditors.DeliveryApi;
using Umbraco.Cms.Core.PropertyEditors.ValueConverters;
using Umbraco.Cms.Core.Strings;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// Wraps Umbraco's rich text converter. Everything is delegated to it; the only change is that a value holding
/// Blue Billywig embed references is returned as a <see cref="DeferredRichText"/>, so those references are turned
/// into markup per request. The inner converter is built from the container, which keeps this free of its
/// (version-specific) constructor. Umbraco's own converter is a "default" one, so this non-default one replaces it.
/// </summary>
public sealed class BlueBillywigRichTextValueConverter : PropertyValueConverterBase, IDeliveryApiPropertyValueConverter
{
    private readonly RteBlockRenderingValueConverter _inner;
    private readonly IBlueBillywigEmbedRenderer _renderer;
    private readonly IOptionsMonitor<BlueBillywigOptions>? _options;

    public BlueBillywigRichTextValueConverter(IServiceProvider services, IBlueBillywigEmbedRenderer renderer, IOptionsMonitor<BlueBillywigOptions>? options = null)
    {
        _inner = ActivatorUtilities.CreateInstance<RteBlockRenderingValueConverter>(services);
        _renderer = renderer;
        _options = options;
    }

    public override bool IsConverter(IPublishedPropertyType propertyType) => _inner.IsConverter(propertyType);

    public override Type GetPropertyValueType(IPublishedPropertyType propertyType) => _inner.GetPropertyValueType(propertyType);

    public override PropertyCacheLevel GetPropertyCacheLevel(IPublishedPropertyType propertyType) => _inner.GetPropertyCacheLevel(propertyType);

    public override bool? IsValue(object? value, PropertyValueLevel level) => _inner.IsValue(value, level);

    public override object? ConvertSourceToIntermediate(IPublishedElement owner, IPublishedPropertyType propertyType, object? source, bool preview) =>
        _inner.ConvertSourceToIntermediate(owner, propertyType, source, preview);

    public override object? ConvertIntermediateToObject(IPublishedElement owner, IPublishedPropertyType propertyType, PropertyCacheLevel referenceCacheLevel, object? inter, bool preview)
    {
        object? value = _inner.ConvertIntermediateToObject(owner, propertyType, referenceCacheLevel, inter, preview);
        return inter is IRichTextEditorIntermediateValue rich && RichTextEmbedRewriter.MayContainEmbeds(rich.Markup) && value is IHtmlEncodedString html
            ? new DeferredRichText(html, _renderer, video => EmbedDefaults.ForOEmbed(_options?.CurrentValue, video))
            : value;
    }

    public PropertyCacheLevel GetDeliveryApiPropertyCacheLevel(IPublishedPropertyType propertyType) => _inner.GetDeliveryApiPropertyCacheLevel(propertyType);

    public PropertyCacheLevel GetDeliveryApiPropertyCacheLevelForExpansion(IPublishedPropertyType propertyType) => _inner.GetDeliveryApiPropertyCacheLevelForExpansion(propertyType);

    public Type GetDeliveryApiPropertyValueType(IPublishedPropertyType propertyType) => _inner.GetDeliveryApiPropertyValueType(propertyType);

    /// <summary>Headless output carries the reference only: anything an older editor baked into the holder is emptied first.</summary>
    public object? ConvertIntermediateToDeliveryApiObject(IPublishedElement owner, IPublishedPropertyType propertyType, PropertyCacheLevel referenceCacheLevel, object? inter, bool preview, bool expanding)
    {
        if (inter is IRichTextEditorIntermediateValue rich && RichTextEmbedRewriter.MayContainEmbeds(rich.Markup))
        {
            inter = new ReferenceOnlyIntermediate(RichTextEmbedRewriter.ToReferences(rich.Markup!), rich.RichTextBlockModel);
        }

        return _inner.ConvertIntermediateToDeliveryApiObject(owner, propertyType, referenceCacheLevel, inter, preview, expanding);
    }

    private sealed class ReferenceOnlyIntermediate(string markup, RichTextBlockModel? blocks) : IRichTextEditorIntermediateValue
    {
        public string Markup { get; } = markup;

        public RichTextBlockModel? RichTextBlockModel { get; } = blocks;
    }
}

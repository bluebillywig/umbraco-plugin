using Umbraco.Cms.Core.Media.EmbedProviders;
using Umbraco.Cms.Core.Serialization;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// Makes the rich text Embed button accept <c>https://{publication}.bbvms.com/p|view|ch/...</c> URLs, the
/// shapes the reference plugin's oEmbed handlers accept. No HTTP request is made.
/// <para>
/// The rich text editor stores the returned markup, so the markup is a reference: inert text that names no player
/// host and runs nothing, not even inside the backoffice editor. The embed itself is made when the page is rendered,
/// by <see cref="RichTextEmbedRewriter"/> through <see cref="IBlueBillywigEmbedRenderer"/>, where consent, the CSP
/// nonce and escaping are applied for that request.
/// </para>
/// </summary>
public sealed class BlueBillywigOEmbedProvider : OEmbedProviderBase
{
    public BlueBillywigOEmbedProvider(IJsonSerializer jsonSerializer)
        : base(jsonSerializer)
    {
    }

    public override string ApiEndpoint => string.Empty;

    public override string[] UrlSchemeRegex => BlueBillywigUrls.OEmbedPatterns;

    public override Dictionary<string, string> RequestParams => [];

    public override Task<string?> GetMarkupAsync(string url, int? maxWidth, int? maxHeight, CancellationToken cancellationToken)
    {
        BlueBillywigVideo? video = BlueBillywigUrls.TryParseOEmbedUrl(url);
        if (video is null)
        {
            return Task.FromResult<string?>(null);
        }

        // Only the type and a numeric id: both are validated, so nothing here needs encoding. A span, not a div: the
        // editor stores this inside a paragraph, and a block element there is pulled out of it by the HTML parser.
        string kind = video.Type == BlueBillywigVideoType.Channel ? "channel" : "video";
        return Task.FromResult<string?>($"<span class=\"bb-oembed-ref\">Blue Billywig {kind} {video.Id}. The player is added when the page is shown.</span>");
    }
}

using Microsoft.AspNetCore.Html;
using Umbraco.Cms.Core.Strings;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// A rich text value that turns its Blue Billywig references into embeds when it is written, not when it is
/// converted. The converted value is cached across requests, so consent, the CSP nonce and the player markup
/// have to be decided here, per request.
/// </summary>
internal sealed class DeferredRichText : IHtmlEncodedString, IHtmlContent
{
    private readonly IHtmlEncodedString _inner;
    private readonly IBlueBillywigEmbedRenderer _renderer;
    private readonly Func<BlueBillywigVideo, BlueBillywigVideo>? _oembedDefaults;

    public DeferredRichText(IHtmlEncodedString inner, IBlueBillywigEmbedRenderer renderer, Func<BlueBillywigVideo, BlueBillywigVideo>? oembedDefaults = null)
    {
        _inner = inner;
        _renderer = renderer;
        _oembedDefaults = oembedDefaults;
    }

    /// <summary>The rendering is asynchronous but the interfaces are not; SAPI answers are cached, so the wait is short.</summary>
    public string ToHtmlString()
    {
        string html = _inner.ToHtmlString() ?? "";
        return RichTextEmbedRewriter.RewriteAsync(html, async video => (await _renderer.RenderAsync(video)).ToHtmlString(), oembedDefaults: _oembedDefaults).GetAwaiter().GetResult();
    }

    public void WriteTo(TextWriter writer, System.Text.Encodings.Web.HtmlEncoder encoder) => writer.Write(ToHtmlString());

    public override string ToString() => ToHtmlString();
}

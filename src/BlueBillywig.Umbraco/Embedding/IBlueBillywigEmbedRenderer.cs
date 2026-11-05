using Microsoft.AspNetCore.Html;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// The single place embed HTML is produced. The view component, tag helper, block partials and the
/// oEmbed provider all call this, so the assets, the consent gate and the nonce cannot be skipped.
/// </summary>
public interface IBlueBillywigEmbedRenderer
{
    Task<IHtmlContent> RenderAsync(BlueBillywigVideo? video, CancellationToken ct = default);
}

/// <summary>
/// Puts the lazy loader (<c>bb-lazy.css</c> and <c>bb-lazy.js</c>) on a page before an embed asks for it. Razor usage:
/// <c>&lt;bb-assets /&gt;</c> in the layout's head, with <c>@addTagHelper *, BlueBillywig.Umbraco</c>. It does something only
/// when <see cref="Configuration.BlueBillywigOptions.ForceLazyLoader"/> is on, and nothing when
/// <see cref="Configuration.BlueBillywigOptions.DisableLazyLoader"/> is on. The loader is emitted at most once per request.
/// </summary>
public interface IBlueBillywigAssets
{
    IHtmlContent RenderAssets();
}

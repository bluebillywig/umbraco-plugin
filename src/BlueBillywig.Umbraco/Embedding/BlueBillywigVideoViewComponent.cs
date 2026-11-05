using BlueBillywig.Umbraco.Configuration;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// Razor usage: <c>@await Component.InvokeAsync("BlueBillywigVideo", new { video = Model.Video })</c>, where the
/// value is a <see cref="BlueBillywigVideo"/>. Without a value, a video is made from <c>id</c> (and optionally
/// <c>type</c>, <c>playout</c>, <c>lazy</c>, <c>transcript</c>, <c>chapters</c>); then, when <c>lazy</c> is not given,
/// <see cref="BlueBillywigOptions.LazyLoadComponent"/> decides whether it is lazy loaded.
/// </summary>
[ViewComponent(Name = "BlueBillywigVideo")]
public sealed class BlueBillywigVideoViewComponent : ViewComponent
{
    private readonly IBlueBillywigEmbedRenderer _renderer;
    private readonly IOptionsMonitor<BlueBillywigOptions>? _options;

    public BlueBillywigVideoViewComponent(IBlueBillywigEmbedRenderer renderer, IOptionsMonitor<BlueBillywigOptions>? options = null)
    {
        _renderer = renderer;
        _options = options;
    }

    public async Task<IViewComponentResult> InvokeAsync(BlueBillywigVideo? video, long? id = null, string? type = null, string? playout = null, bool? lazy = null, bool? transcript = null, bool? chapters = null)
    {
        if (video is null && id is > 0)
        {
            video = EmbedDefaults.FromId(_options?.CurrentValue, type, id.Value, playout, lazy, transcript, chapters);
        }

        return new HtmlContentViewComponentResult(await _renderer.RenderAsync(video, HttpContext.RequestAborted));
    }
}

/// <summary>
/// The shortcode equivalent. <c>&lt;bb-video video="@Model.Video" /&gt;</c> renders a property value;
/// <c>&lt;bb-video type="clip" video-id="123" playout="default" lazy="false" /&gt;</c> renders by id
/// (the attribute is <c>video-id</c>, because a bare <c>id</c> is the HTML id attribute).
/// Enable it with <c>@addTagHelper *, BlueBillywig.Umbraco</c>.
/// </summary>
[HtmlTargetElement("bb-video", TagStructure = TagStructure.NormalOrSelfClosing)]
public sealed class BlueBillywigVideoTagHelper : TagHelper
{
    private readonly IBlueBillywigEmbedRenderer _renderer;
    private readonly IOptionsMonitor<BlueBillywigOptions>? _options;

    public BlueBillywigVideoTagHelper(IBlueBillywigEmbedRenderer renderer, IOptionsMonitor<BlueBillywigOptions>? options = null)
    {
        _renderer = renderer;
        _options = options;
    }

    [HtmlAttributeName("video")]
    public BlueBillywigVideo? Video { get; set; }

    [HtmlAttributeName("type")]
    public string? Type { get; set; }

    [HtmlAttributeName("video-id")]
    public long? Id { get; set; }

    [HtmlAttributeName("playout")]
    public string? Playout { get; set; }

    [HtmlAttributeName("lazy")]
    public bool? Lazy { get; set; }

    [HtmlAttributeName("transcript")]
    public bool? Transcript { get; set; }

    [HtmlAttributeName("chapters")]
    public bool? Chapters { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        BlueBillywigVideo? video = Video;
        if (video is null && Id is > 0)
        {
            video = EmbedDefaults.FromId(_options?.CurrentValue, Type, Id.Value, Playout, Lazy, Transcript, Chapters);
        }
        else if (video is not null && (Lazy is not null || Playout is not null))
        {
            video = video with { LazyLoad = Lazy ?? video.LazyLoad, Playout = BlueBillywigVideo.IsValidPlayout(Playout) ? Playout : video.Playout };
        }

        output.TagName = null;
        output.Content.SetHtmlContent(await _renderer.RenderAsync(video));
    }
}

/// <summary>
/// <c>&lt;bb-assets /&gt;</c>: see <see cref="IBlueBillywigAssets"/>.
/// </summary>
[HtmlTargetElement("bb-assets", TagStructure = TagStructure.NormalOrSelfClosing)]
public sealed class BlueBillywigAssetsTagHelper : TagHelper
{
    private readonly IBlueBillywigAssets _assets;

    public BlueBillywigAssetsTagHelper(IBlueBillywigAssets assets) => _assets = assets;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;
        output.Content.SetHtmlContent(_assets.RenderAssets());
    }
}

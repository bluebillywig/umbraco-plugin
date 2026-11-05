using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Localization;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.Seo;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>
/// Port of the reference plugin's <c>render_block</c>, <c>render_channel_block</c>, <c>render_shorts_block</c>,
/// <c>wrap_lazy_embed</c> and <c>render_consent_placeholder</c>. Every embed on the site is produced here.
/// </summary>
internal sealed partial class BlueBillywigEmbedRenderer : IBlueBillywigEmbedRenderer, IBlueBillywigAssets
{
    private const string AssetPath = "/App_Plugins/BlueBillywig/";
    private const string AssetsEmittedKey = "BlueBillywig.AssetsEmitted";
    private const string CtaOverlayEmittedKey = "BlueBillywig.CtaOverlayEmitted";
    private const string LoaderKeyItem = "BlueBillywig.LoaderKey";
    private const string ConsentCookie = "bb_consent";
    private static readonly HtmlEncoder Enc = EmbedHtml.Encoder;
    private static readonly string AssetVersion = typeof(BlueBillywigEmbedRenderer).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];
    private static readonly string[] TrustedDomains = ["bbvms.com", "bluebillywig.eu", "bluebillywig.com"];
    private static readonly string[] RtlLanguages = ["ar", "he", "fa", "ur", "yi", "ps", "ku", "dv"];

    private readonly IBlueBillywigSettingsStore _settings;
    private readonly IBlueBillywigClient _client;
    private readonly IBlueBillywigConsentProvider _consent;
    private readonly IBlueBillywigCspNonceProvider _nonces;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<BlueBillywigEmbedRenderer> _logger;
    private readonly IThumbnailState? _thumbnails;
    private readonly IOptionsMonitor<BlueBillywigOptions>? _options;
    private readonly ContentGateFactory? _gates;
    private readonly IClipPlayoutStore? _playouts;
    private readonly IBlueBillywigEmbedDiagnostics? _diagnostics;

    public BlueBillywigEmbedRenderer(
        IBlueBillywigSettingsStore settings,
        IBlueBillywigClient client,
        IBlueBillywigConsentProvider consent,
        IBlueBillywigCspNonceProvider nonces,
        IHttpContextAccessor http,
        ILogger<BlueBillywigEmbedRenderer> logger,
        IThumbnailState? thumbnails = null,
        IOptionsMonitor<BlueBillywigOptions>? options = null,
        ContentGateFactory? gates = null,
        IClipPlayoutStore? playouts = null,
        IBlueBillywigEmbedDiagnostics? diagnostics = null)
    {
        _diagnostics = diagnostics;
        _playouts = playouts;
        _gates = gates;
        _thumbnails = thumbnails;
        _options = options;
        _settings = settings;
        _client = client;
        _consent = consent;
        _nonces = nonces;
        _http = http;
        _logger = logger;
    }

    public async Task<IHtmlContent> RenderAsync(BlueBillywigVideo? video, CancellationToken ct = default)
    {
        BlueBillywigSettings settings = _settings.Get();
        string publication = video?.Publication ?? settings.Publication;
        if (video is null || !video.IsValid || !BlueBillywigVideo.IsValidPublication(publication))
        {
            return HtmlString.Empty;
        }

        HttpContext? http = _http.HttpContext;
        string? rawNonce = _nonces.GetNonce(http);
        string? nonce = NormalizeNonce(rawNonce);
        var ctx = new RenderContext(settings, publication, PlayerBaseUrl(settings, publication), nonce, http, ct, LoaderKeyFor(http))
        {
            // A provider that returned something, which we refused: every script of this embed goes out without a nonce.
            NonceRejected = !string.IsNullOrEmpty(rawNonce) && nonce is null,
        };

        // The playout chosen in the library applies when the embed names none (the reference plugin reads its per-clip option
        // at render time). Resolved here, once, so the player URL, the embed code request, the gate stand-in and the tag
        // helper, view component, property, rich text and oEmbed paths all agree.
        video = EmbedDefaults.WithClipPlayout(_playouts, video, SamePublication(ctx));

        // Assets first and unconditionally, like enqueue_embed_runtime() before the consent gate: the
        // placeholder's button needs the script, and no embed may ever ship without its assets.
        var html = new StringBuilder(AssetTags(ctx, _options?.CurrentValue.DisableLazyLoader == true));

        // The call-to-action form overlay ships with every embed while the feature is on, also when the lazy loader is
        // switched off: a clip with a form CTA is useless without it.
        html.Append(CtaOverlayTags(ctx));

        bool granted = _consent.IsGranted(http) || CookieGranted(http);

        // Content protection (G1 to G6). Every clip and channel passes through the gate here, so no render path (property,
        // block, rich text reference, tag helper, view component) can skip it. Shorts have no content protection policy.
        GateEntity? entity = video.Type switch
        {
            BlueBillywigVideoType.Clip => GateEntity.Clip(video.Id),
            BlueBillywigVideoType.Channel => GateEntity.ChannelOf(video.Id),
            _ => null,
        };

        if (entity is { } gated && _gates is { Enabled: true })
        {
            // The gate only knows the configured publication: the policy lookup and the token are for that publication's
            // clip with this id. An embed of another publication gets neither (the same id there is another clip, and a token
            // minted for the wrong tenant is worth nothing to the viewer and something to its owner): the player alone, which
            // enforces its own protection.
            html.Append(SamePublication(ctx)
                ? await RenderGatedAsync(gated, video, ctx with { GateActive = true }, granted)
                : granted ? await RenderGrantedAsync(video, ctx) : RenderDenied(video, ctx));
        }
        else
        {
            html.Append(granted ? await RenderGrantedAsync(video, ctx) : RenderDenied(video, ctx));
        }

        if (ctx.NonceRejected)
        {
            _logger.LogWarning("The CSP nonce provider returned a value that is not a valid nonce ([A-Za-z0-9+/=_-]); the embed's scripts go out without a nonce and a strict policy will block them.");
            html.Append(await ProblemMarkerAsync(ctx, "nonce-rejected"));
        }

        // JSON-LD is part of the clip markup (RenderClipAsync), so the gate covers it like the transcript.
        return new HtmlString(html.ToString());
    }

    /// <summary>
    /// The visible marker for a CSP nonce that could not be applied (the reference plugin's <c>cspFailureMarker</c>): an element
    /// with the class <c>bb-embed-problem</c> and a short encoded sentence, so a strict-CSP site does not just show an empty
    /// box. Narrower than the plugin, which shows it to everyone: only to a signed-in backoffice user, or to everybody while the
    /// site's <c>ShowEmbedProblems</c> switch is on. Everyone else gets the silent fallback (the warning is in the log). The
    /// marker for a signed-in user makes the response private, because the same URL then differs per user; if that is no
    /// longer possible the marker is not shown. There is no inline style: a strict policy may block it, which is the case
    /// this marker is for (the rule is in <c>bb-lazy.css</c>). <paramref name="reason"/> is one of our own constants.
    /// </summary>
    private async Task<string> ProblemMarkerAsync(RenderContext ctx, string reason)
    {
        EmbedProblemAudience audience = _diagnostics is null ? EmbedProblemAudience.Nobody : await _diagnostics.GetAudienceAsync(ctx.Http);
        if (audience == EmbedProblemAudience.Nobody)
        {
            return "";
        }

        if (audience == EmbedProblemAudience.SignedInUser && !GateResponse.MarkPrivate(ctx.Http, _logger))
        {
            return "";
        }

        return $"<div class=\"bb-embed-problem\" role=\"alert\" data-bb-reason=\"{Enc.Encode(reason)}\">{Enc.Encode(V("embedProblem"))}</div>";
    }

    /// <summary>
    /// The gate around one clip or channel (plugin: <c>filter_embed_block</c> and <c>filter_channel_block</c>, applied to the
    /// whole block output so the transcript, chapters and JSON-LD of a protected clip are replaced with the player).
    /// <para>
    /// Without consent nothing from the player host may load, so there is no embed to inject a token into. The gate still
    /// runs, on a stand-in script tag that is thrown away, so a visitor who may not watch is told so instead of being asked
    /// to consent to a player they cannot use; a visitor who may watch gets the consent placeholder.
    /// </para>
    /// </summary>
    private async Task<string> RenderGatedAsync(GateEntity entity, BlueBillywigVideo video, RenderContext ctx, bool granted)
    {
        string fragment;
        if (granted)
        {
            fragment = await RenderGrantedAsync(video, ctx);
        }
        else
        {
            string standIn = entity.Kind == GateEntityKind.Channel
                ? BlueBillywigUrls.ChannelScript(ctx.PlayerBase, video.Id)
                : BlueBillywigUrls.ClipScript(ctx.PlayerBase, video.Playout ?? (BlueBillywigVideo.IsValidPlayout(ctx.Settings.Playout) ? ctx.Settings.Playout : BlueBillywigVideo.DefaultClipPlayout), video.Id);
            fragment = ScriptTag(standIn, null);
        }

        GateResult result;
        try
        {
            string? publicationHost = _client.IsConnected && SamePublication(ctx) ? await PublicationHostAsync(ctx.Ct) : null;
            ContentGate gate = await _gates!.CreateAsync(host => IsTokenHost(host, ctx, publicationHost), ctx.Ct);
            result = await gate.ApplyAsync(entity, fragment, ctx.Ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Fail closed, and tell the admin: a gate that could not even be built must not show the embed.
            _logger.LogError(e, "Blue Billywig ContentGate: could not run the gate for {Kind}={Id}; rendering the gate message.", entity.Kind, entity.Id);
            _gates!.Health.RecordGateError(GateReasons.InternalError);
            GateResponse.MarkPrivate(ctx.Http, _logger);
            return GateMessage.Render(false, null, _gates.CustomMessage);
        }

        if (!result.MustBePrivate)
        {
            return granted ? result.Html : RenderDenied(video, ctx);
        }

        bool privateOk = GateResponse.MarkPrivate(ctx.Http, _logger);
        if (!granted)
        {
            return result.Outcome is GateOutcome.Denied or GateOutcome.ClosedOnError ? result.Html : RenderDenied(video, ctx);
        }

        if (!privateOk && result.Outcome == GateOutcome.TokenInjected)
        {
            // A token in a response a shared cache may keep would hand one member's pseudonym and access to everyone.
            _gates!.Health.RecordGateError(GateReasons.CacheUnsafe);
            return GateMessage.Render(false, null, _gates.CustomMessage);
        }

        return result.Html;
    }

    private async Task<string> RenderGrantedAsync(BlueBillywigVideo video, RenderContext ctx) => video.Type switch
    {
        BlueBillywigVideoType.Channel => RenderChannel(video, ctx),
        BlueBillywigVideoType.Shorts => RenderShorts(video, ctx),
        _ => await RenderClipAsync(video, ctx),
    };

    // Consent placeholders -------------------------------------------------------------------------------

    private string RenderDenied(BlueBillywigVideo video, RenderContext ctx)
    {
        switch (video.Type)
        {
            case BlueBillywigVideoType.Channel:
                string channelHeight = video.Height ?? BlueBillywigVideo.DefaultChannelHeight;
                return $"<div class=\"bb-channel-embed bb-block-channel\" role=\"region\" aria-label=\"{Enc.Encode(V("labelVideoChannel"))}\" style=\"width:100%;height:{Enc.Encode(channelHeight)};overflow:hidden;\">"
                    + ConsentPlaceholder("bb-lazy-fixed-height", channelHeight, V("loadChannel"), PlaceholderMessage(ctx), ctx.LoaderKey)
                    + "</div>";
            case BlueBillywigVideoType.Shorts:
                return ConsentPlaceholder("bb-lazy-fixed-height", video.Height ?? BlueBillywigVideo.DefaultShortsHeight, V("loadShorts"), PlaceholderMessage(ctx), ctx.LoaderKey);
            default:
                return $"<div class=\"bb-block-embed bb-embed-responsive\" role=\"region\" aria-label=\"{Enc.Encode(V("labelVideoPlayer"))}\">"
                    + ConsentPlaceholder("bb-lazy-ratio-16-9", "", V("loadVideo"), PlaceholderMessage(ctx), ctx.LoaderKey)
                    + "</div>";
        }
    }

    /// <summary>
    /// The placeholder's sentence: the consent provider's own text, else the site's setting, else the built-in one
    /// (E19, the reference plugin's <c>bluebillywig_consent_placeholder_message</c> filter). Always plain text; the caller encodes it.
    /// </summary>
    /// <summary>A visitor-facing built-in text in the request's language (<see cref="VisitorText"/>); English when there is no translation.</summary>
    private static string V(string key, params object[] args) => VisitorText.Get(key, null, args);

    private string PlaceholderMessage(RenderContext ctx)
    {
        string custom = ConsentMessage.Clean(_consent.GetPlaceholderMessage(ctx.Http));
        if (custom.Length == 0)
        {
            custom = ConsentMessage.Clean(ctx.Settings.ConsentMessage);
        }

        return custom.Length > 0 ? custom : V("consentMessage");
    }

    /// <summary>The placeholder markup. <paramref name="message"/> is plain text and is encoded here; nothing from the player host is mentioned or loaded.</summary>
    internal static string ConsentPlaceholder(string ratioClass, string height, string label, string? message = null, string? loaderKey = null)
    {
        string style = ratioClass == "bb-lazy-fixed-height" && height.Length > 0 && BlueBillywigVideo.IsValidHeight(height)
            ? $" style=\"--bb-lazy-height:{Enc.Encode(height)};\""
            : "";
        // The key lets the loader tell this placeholder from markup an editor or upstream wrote: its Load button sets the
        // consent cookie, so only a button inside a placeholder the renderer made may do that.
        string key = string.IsNullOrEmpty(loaderKey) ? "" : $" data-bb-key=\"{Enc.Encode(loaderKey)}\"";
        return $"<div class=\"bb-consent-placeholder {Enc.Encode(ratioClass)}\" role=\"region\" aria-label=\"{Enc.Encode(label)}\"{key}{style}>"
            + "<div class=\"bb-consent-placeholder__inner\">"
            + "<p class=\"bb-consent-placeholder__msg\">" + Enc.Encode(message ?? V("consentMessage")) + "</p>"
            + "<button type=\"button\" class=\"bb-consent-placeholder__btn\" data-bb-consent-grant=\"1\">" + Enc.Encode(V("loadVideo")) + "</button>"
            + "</div></div>";
    }

    // Channel and shorts ---------------------------------------------------------------------------------

    private static string RenderChannel(BlueBillywigVideo video, RenderContext ctx)
    {
        string height = video.Height ?? BlueBillywigVideo.DefaultChannelHeight;
        string pageUrl = BlueBillywigUrls.ChannelPage(ctx.PlayerBase, video.Id);
        string script = ScriptTag(BlueBillywigUrls.ChannelScript(ctx.PlayerBase, video.Id), ctx.Nonce);
        string noscriptLink = $"<noscript><a href=\"{Enc.Encode(pageUrl)}\">{Enc.Encode(V("watchChannel"))}</a></noscript>";
        string open = $"<div class=\"bb-channel-embed bb-block-channel\" role=\"region\" aria-label=\"{Enc.Encode(V("labelVideoChannel"))}\" style=\"width:100%;height:{Enc.Encode(height)};overflow:hidden;\">";

        return video.LazyLoad
            ? open + WrapLazy(script, "", V("loadChannel"), "bb-lazy-fixed-height", height, LazyTrustFor(ctx, null)) + noscriptLink + "</div>"
            : open + script + noscriptLink + "</div>";
    }

    private static string RenderShorts(BlueBillywigVideo video, RenderContext ctx)
    {
        string height = Enc.Encode(video.Height ?? BlueBillywigVideo.DefaultShortsHeight);
        string src = Enc.Encode(BlueBillywigUrls.ShortsPage(ctx.PlayerBase, video.Id));
        string style = video.Fullscreen
            ? $"width:100%;height:{height};border:none;"
            : $"width:100%;max-width:400px;height:{height};border:none;display:block;margin:0 auto;";
        return $"<iframe class=\"bb-block-shorts\" src=\"{src}\" style=\"{style}\" role=\"region\" aria-label=\"{Enc.Encode(V("labelShorts"))}\" allow=\"autoplay; fullscreen\" loading=\"lazy\"></iframe>";
    }

    // Clip -----------------------------------------------------------------------------------------------

    private async Task<string> RenderClipAsync(BlueBillywigVideo video, RenderContext ctx)
    {
        string playout = video.Playout ?? (BlueBillywigVideo.IsValidPlayout(ctx.Settings.Playout) ? ctx.Settings.Playout : BlueBillywigVideo.DefaultClipPlayout);
        string embedType = ctx.Settings.Embed;
        bool connected = _client.IsConnected && SamePublication(ctx);

        string? publicationHost = connected ? await PublicationHostAsync(ctx.Ct) : null;
        (string? embedHtml, bool unpreparable) = connected ? await FetchEmbedCodeAsync(video.Id, playout, embedType, ctx, publicationHost) : (null, false);
        embedHtml ??= ScriptTag(BlueBillywigUrls.ClipScript(ctx.PlayerBase, playout, video.Id), ctx.Nonce);

        // A clip edited in the last minutes gets ?v= on its player URL, so a browser's cached player data (new title,
        // new thumbnail) is not served for up to 15 minutes. The plugin's ThumbnailState::version_embed_html.
        embedHtml = _thumbnails?.VersionEmbedHtml(embedHtml, video.Id) ?? embedHtml;

        JsonNode? clip = null;
        if (connected)
        {
            SapiResult clipResult = await _client.GetAsync($"/sapi/mediaclip/{video.Id}", cacheFor: TimeSpan.FromMinutes(5), ct: ctx.Ct);
            clip = clipResult.Ok ? clipResult.Json : null;
        }

        // A clip with a content protection policy, with the gate off: the player and nothing else. The player enforces the
        // policy itself; the title, thumbnail, description and transcript are what the policy keeps from the public, and the
        // sitemap leaves such a clip out for the same reason. With the gate on, the gate covers the whole fragment.
        bool withheld = clip is not null && !ctx.GateActive && !SapiJson.PhpEmpty(clip["cpp"]);

        bool interactive = clip?["timelines"] is JsonArray { Count: > 0 };
        string overflow = interactive ? "overflow:visible;" : "overflow:hidden;";

        string poster = "";
        if (video.LazyLoad && connected && !withheld)
        {
            string? src = _thumbnails is null ? ClipThumbnails.MainSrc(clip) : _thumbnails.ClipMainSrc(video.Id, clip);
            if (src is not null)
            {
                poster = ClipThumbnails.Absolute(await ClipThumbnails.AssetBaseAsync(_client, ctx.Ct), src) ?? "";
            }
        }

        // What the lazy loader drops in, or what is rendered directly. The iframe inside it is sized by
        // bb-lazy.css to 100% of this box (2.0.5): the bare SAPI iframe has a hard-coded 405px height.
        string inner = $"<div style=\"position:absolute;top:0;left:0;width:100%;height:100%;\">{embedHtml}</div>";

        var output = new StringBuilder();
        // SAPI's embed code could not be given our nonce (it was not in the expected shape), so the plain script embed is
        // used. It carries the nonce and works, but on a strict-CSP site that is worth a visible note to the people who can act on it.
        if (unpreparable && ctx.Nonce is not null)
        {
            output.Append(await ProblemMarkerAsync(ctx, "embed-code-unprepared"));
        }

        string interactiveClass = interactive ? " bb-interactive" : "";
        if (video.LazyLoad)
        {
            string title = withheld ? "" : Text(clip?["title"]);
            string label = title.Length > 0 ? V("playTitle", title) : V("playVideo");
            output.Append($"<div class=\"bb-block-embed bb-embed-responsive bb-embed-lazy{interactiveClass}\" role=\"region\" aria-label=\"{Enc.Encode(V("labelVideoPlayer"))}\" data-bb-clip-id=\"{video.Id}\" data-bb-key=\"{Enc.Encode(ctx.LoaderKey)}\">")
                .Append(WrapLazy(inner, poster, label, "bb-lazy-ratio-16-9", "", LazyTrustFor(ctx, publicationHost)))
                .Append("</div>");
        }
        else
        {
            output.Append($"<div class=\"bb-block-embed bb-embed-responsive{interactiveClass}\" role=\"region\" aria-label=\"{Enc.Encode(V("labelVideoPlayer"))}\" data-bb-clip-id=\"{video.Id}\" data-bb-key=\"{Enc.Encode(ctx.LoaderKey)}\" style=\"position:relative;padding-bottom:56.25%;height:0;{overflow}\">")
                .Append(inner)
                .Append("</div>");
        }

        if (clip is not null && !withheld)
        {
            // The JavaScript embed brings its own JSON-LD; the other types get it here (Q1, Q2).
            if (VideoJsonLd.ShouldEmitFor(embedType))
            {
                string? mainSrc = _thumbnails is null ? ClipThumbnails.MainSrc(clip) : _thumbnails.ClipMainSrc(video.Id, clip);
                string? thumbnail = mainSrc is null ? null : ClipThumbnails.Absolute(await ClipThumbnails.AssetBaseAsync(_client, ctx.Ct), mainSrc);
                if (VideoJsonLd.Render(VideoJsonLd.FromClip(clip, thumbnail), ctx.Nonce, _logger) is { } jsonLd)
                {
                    output.Append(jsonLd);
                }
            }

            if (video.ShowTranscript)
            {
                output.Append(await RenderTranscriptAsync(video.Id, clip, ctx));
            }

            if (video.ShowChapters)
            {
                output.Append(RenderChapters(video.Id, clip));
            }
        }

        return output.ToString();
    }

    /// <summary>The prepared embed code, or null; <c>Unpreparable</c> is true when SAPI answered but the body could not be made safe.</summary>
    private async Task<(string? Html, bool Unpreparable)> FetchEmbedCodeAsync(long id, string playout, string embedType, RenderContext ctx, string? publicationHost)
    {
        SapiResult result = await _client.GetAsync(
            $"/sapi/embedcode/{id}/{BlueBillywigUrls.Segment(playout)}/{BlueBillywigUrls.Segment(embedType)}",
            cacheFor: TimeSpan.FromHours(1),
            ct: ctx.Ct);
        if (!result.Ok)
        {
            return (null, false);
        }

        string? body = result.Json is JsonObject obj && obj["body"] is JsonValue v && v.TryGetValue(out string? s) ? s : result.Json is null ? result.RawBody : null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, false);
        }

        bool Allowed(string host) => IsPlayerHost(host, ctx, publicationHost);

        if (EmbedHtml.TryPrepare(body, ctx.Nonce, Allowed, out string prepared))
        {
            return (prepared, false);
        }

        // Same fail-safe as the reference plugin's fallback: show the plain script embed rather than raw upstream markup.
        _logger.LogWarning("Embed code for clip {Id} was not in the expected shape; using the script embed instead. Body starts: {Body}", id, body.Length > 400 ? body[..400] : body);
        return (null, true);
    }

    /// <summary>
    /// Hosts a player script or iframe may come from, and the only hosts a gate token may be appended to: the player host
    /// we render ourselves, the publication's own base URL, and Blue Billywig's domains. A real dev OVP serves embeds from
    /// *.ovp.bluebillywig.eu.
    /// </summary>
    private static bool IsPlayerHost(string host, RenderContext ctx, string? publicationHost) =>
        host.Equals(new Uri(ctx.PlayerBase).Host, StringComparison.OrdinalIgnoreCase)
        || (publicationHost is not null && host.Equals(publicationHost, StringComparison.OrdinalIgnoreCase))
        || TrustedDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The only hosts a gate token may be appended to: the player host this render uses, the publication's own host from SAPI,
    /// and the hosts that name the configured publication under a Blue Billywig domain. The last one is for a development OVP,
    /// whose embed code points at <c>{publication}.dev.ovp.bluebillywig.eu</c> while its player base is a different domain
    /// (checked against the development publication). Broader than that (any <c>*.bbvms.com</c>) would hand a viewer's token,
    /// signed with this site's shared secret, to other tenants' URLs that an embed body happened to name.
    /// </summary>
    private static bool IsTokenHost(string host, RenderContext ctx, string? publicationHost) =>
        host.Equals(new Uri(ctx.PlayerBase).Host, StringComparison.OrdinalIgnoreCase)
        || (publicationHost is not null && host.Equals(publicationHost, StringComparison.OrdinalIgnoreCase))
        || (host.StartsWith(ctx.Publication + ".", StringComparison.OrdinalIgnoreCase)
            && TrustedDomains.Any(d => host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)));

    private async Task<string?> PublicationHostAsync(CancellationToken ct)
    {
        SapiResult publication = await _client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromHours(1), ct: ct);
        return publication.Ok
            && publication.Json?["baseurl"] is JsonValue v
            && v.TryGetValue(out string? url)
            && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
                ? uri.Host
                : null;
    }

    private async Task<string> RenderTranscriptAsync(long id, JsonNode clip, RenderContext ctx)
    {
        string transcript = Text(clip["transcript"]);
        if (transcript.Length > 0)
        {
            // The reference plugin sanitises the transcript as post HTML. SAPI text is untrusted here, so it is shown as text.
            return TranscriptBlock(id, V("transcript"), Text(clip["isocode"]), HtmlWithBreaks(transcript));
        }

        JsonNode? subtitle = (clip["subtitles"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(s => Text(s["id"]).Length > 0 && (s["status"] is null || Text(s["status"]) == "published"));
        if (subtitle is null || !long.TryParse(Text(subtitle["id"]), CultureInfo.InvariantCulture, out long subtitleId) || subtitleId <= 0)
        {
            return "";
        }

        SapiResult raw = await _client.GetAsync($"/sapi/subtitle/{subtitleId}/contents", ct: ctx.Ct);
        if (!raw.Ok || string.IsNullOrWhiteSpace(raw.RawBody))
        {
            return "";
        }

        string srt = StripSrt(FixDoubleEncodedUtf8(raw.RawBody));
        string language = Text(subtitle["languagename"]);
        return TranscriptBlock(id, V("transcriptLang", language.Length > 0 ? language : V("subtitles")), Text(subtitle["isocode"]), HtmlWithBreaks(srt));
    }

    private static string TranscriptBlock(long id, string summary, string isoRaw, string bodyHtml)
    {
        string iso = IsoCleanup().Replace(isoRaw, "");
        string lang = iso.Length > 0 ? iso.Split('-')[0].ToLowerInvariant() : "";
        string dir = RtlLanguages.Contains(lang) ? "rtl" : "ltr";
        string langAttr = iso.Length > 0 ? $" lang=\"{Enc.Encode(iso)}\"" : "";
        return $"<details class=\"bb-transcript\" id=\"bb-transcript-{id}\" aria-label=\"{Enc.Encode(V("labelTranscript"))}\">"
            + $"<summary style=\"cursor:pointer;font-weight:600;padding:10px 0;font-size:14px;\">{Enc.Encode(summary)}</summary>"
            + $"<div class=\"bb-transcript-text\"{langAttr} dir=\"{dir}\" style=\"max-height:300px;overflow-y:auto;padding:10px;background:#f9f9f9;border:1px solid #eee;border-radius:4px;font-size:13px;line-height:1.6;\">"
            + bodyHtml
            + "</div></details>";
    }

    private static string RenderChapters(long id, JsonNode clip)
    {
        JsonNode? chapters = clip["chapters"];
        if (chapters is JsonValue v && v.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                chapters = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return "";
            }
        }

        if (chapters is not JsonArray { Count: > 0 } list)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (JsonObject chapter in list.OfType<JsonObject>())
        {
            string title = Text(chapter["title"]);
            if (title.Length == 0) continue;
            long.TryParse(Text(chapter["time"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out long time);
            if (time < 0) time = 0;
            string stamp = $"{time / 60}:{time % 60:00}";
            // Data attributes only: the click handler lives in bb-lazy.js, so a strict script-src CSP does not block it.
            sb.Append($"<a href=\"#\" class=\"bb-chapter-link\" data-time=\"{time}\" style=\"display:inline-flex;align-items:center;gap:4px;padding:4px 10px;background:#f0f0f0;border-radius:12px;font-size:12px;text-decoration:none;color:#1d2327;\">")
                .Append($"<span style=\"color:#646970;\">{Enc.Encode(stamp)}</span> {Enc.Encode(title)}</a>");
        }

        return sb.Length == 0
            ? ""
            : $"<div class=\"bb-chapters\" data-bb-chapter-target=\"{id}\" style=\"margin-top:10px;\" aria-label=\"{Enc.Encode(V("videoChapters"))}\">"
                + "<div style=\"font-weight:600;font-size:14px;padding:6px 0;\">" + Enc.Encode(V("chapters")) + "</div>"
                + "<div class=\"bb-chapters-list\" style=\"display:flex;flex-wrap:wrap;gap:6px;\">"
                + sb
                + "</div></div>";
    }

    // Lazy wrapper ---------------------------------------------------------------------------------------

    /// <summary>Port of <c>wrap_lazy_embed</c>: a placeholder bb-lazy.js swaps for the real embed, plus a noscript copy.</summary>
    internal static string WrapLazy(string embedHtml, string posterUrl, string label, string ratioClass, string height, LazyTrust trust)
    {
        if (embedHtml.Length == 0)
        {
            return "";
        }

        var style = new StringBuilder();
        if (posterUrl.Length > 0)
        {
            // Quotes and brackets would end the CSS url() early.
            string safe = posterUrl.Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("\"", "%22");
            style.Append($"background-image:url('{safe}');");
        }

        if (ratioClass == "bb-lazy-fixed-height" && BlueBillywigVideo.IsValidHeight(height))
        {
            style.Append($"--bb-lazy-height:{height};");
        }

        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(embedHtml));
        var sb = new StringBuilder();
        sb.Append($"<div class=\"bb-lazy-embed {Enc.Encode(ratioClass)}\" role=\"button\" tabindex=\"0\" aria-label=\"{Enc.Encode(label)}\" data-bb-embed=\"{encoded}\" data-bb-key=\"{Enc.Encode(trust.Key)}\" data-bb-hosts=\"{Enc.Encode(trust.Hosts)}\"");
        if (style.Length > 0)
        {
            sb.Append($" style=\"{Enc.Encode(style.ToString())}\"");
        }

        sb.Append("></div><noscript>").Append(embedHtml).Append("</noscript>");
        return sb.ToString();
    }

    // Assets ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>&lt;bb-assets /&gt;</c>: the lazy loader ahead of any embed, for pages that need it early or whose embeds this
    /// package cannot see. Emits only when <see cref="BlueBillywigOptions.ForceLazyLoader"/> is on (the plugin's head-time
    /// enqueue has no detection to run here), never when <see cref="BlueBillywigOptions.DisableLazyLoader"/> is on, and
    /// at most once per request, so an embed rendered later does not repeat it.
    /// </summary>
    public IHtmlContent RenderAssets()
    {
        BlueBillywigOptions options = _options?.CurrentValue ?? new BlueBillywigOptions();
        if (options.DisableLazyLoader || !options.ForceLazyLoader)
        {
            return HtmlString.Empty;
        }

        HttpContext? http = _http.HttpContext;
        var ctx = new RenderContext(_settings.Get(), "", "", NormalizeNonce(_nonces.GetNonce(http)), http, default, LoaderKeyFor(http));
        return new HtmlString(AssetTags(ctx, false) + CtaOverlayTags(ctx));
    }

    /// <summary>The texts the loader script shows on its own (the failure message), in the request's language.</summary>
    internal static readonly string[] LoaderTextKeys = ["videoUnavailable"];

    /// <summary>The defaults the form overlay script falls back to when the call to action sets no label of its own.</summary>
    internal static readonly string[] OverlayTextKeys = ["ctaSubscribe", "ctaEmail", "ctaConsent", "ctaPrivacy", "ctaSubmit", "ctaThanks", "ctaErrEmail", "ctaErrConsent", "ctaErrSubmit"];

    /// <summary>
    /// <c>data-bb-text</c> for a script tag: the translated texts as a JSON object, HTML-encoded. The scripts are static files, so the
    /// request's language reaches them this way (the same route as <c>data-bb-key</c>). A script that finds no or broken JSON uses its
    /// own English defaults.
    /// </summary>
    internal static string ScriptText(IEnumerable<string> keys)
        => $" data-bb-text=\"{Enc.Encode(JsonSerializer.Serialize(VisitorText.ForScript(keys)))}\"";

    /// <summary>The stylesheet and loader script, once per request. Called by the render path itself.</summary>
    private static string AssetTags(RenderContext ctx, bool disabled)
    {
        if (disabled)
        {
            return "";
        }

        if (ctx.Http is not null)
        {
            if (ctx.Http.Items.ContainsKey(AssetsEmittedKey))
            {
                return "";
            }

            ctx.Http.Items[AssetsEmittedKey] = true;
        }

        string prefix = (ctx.Http?.Request.PathBase.Value ?? "") + AssetPath;
        string nonce = string.IsNullOrEmpty(ctx.Nonce) ? "" : $" nonce=\"{Enc.Encode(ctx.Nonce)}\"";
        // data-bb-key: the loader acts only on wrappers that carry this value, see LoaderKeyFor.
        return $"<link rel=\"stylesheet\" href=\"{Enc.Encode(prefix)}bb-lazy.css?v={AssetVersion}\" />"
            + $"<script src=\"{Enc.Encode(prefix)}bb-lazy.js?v={AssetVersion}\" defer{nonce} data-bb-key=\"{Enc.Encode(ctx.LoaderKey)}\"{ScriptText(LoaderTextKeys)}></script>";
    }

    /// <summary>
    /// The form overlay stylesheet and script (<c>bb-cta-form-overlay.*</c>), once per request, with the CSP nonce. Not
    /// switched off by <see cref="BlueBillywigOptions.DisableLazyLoader"/>; only by the settings turning the call-to-action
    /// feature off.
    /// </summary>
    private static string CtaOverlayTags(RenderContext ctx)
    {
        if (!ctx.Settings.EnableCta)
        {
            return "";
        }

        if (ctx.Http is not null)
        {
            if (ctx.Http.Items.ContainsKey(CtaOverlayEmittedKey))
            {
                return "";
            }

            ctx.Http.Items[CtaOverlayEmittedKey] = true;
        }

        string prefix = (ctx.Http?.Request.PathBase.Value ?? "") + AssetPath;
        string nonce = string.IsNullOrEmpty(ctx.Nonce) ? "" : $" nonce=\"{Enc.Encode(ctx.Nonce)}\"";
        return $"<link rel=\"stylesheet\" href=\"{Enc.Encode(prefix)}bb-cta-form-overlay.css?v={AssetVersion}\" />"
            + $"<script src=\"{Enc.Encode(prefix)}bb-cta-form-overlay.js?v={AssetVersion}\" defer{nonce} data-bb-key=\"{Enc.Encode(ctx.LoaderKey)}\"{ScriptText(OverlayTextKeys)}></script>";
    }

    // Trust between the renderer and the browser scripts ------------------------------------------------

    /// <summary>
    /// A random value, new for every request, that the renderer prints on the loader and overlay script tags and on every
    /// wrapper it makes. The scripts act only on markup that carries it, so a wrapper, a placeholder or a <c>bb-cta-form</c>
    /// element that came from SAPI, from an editor or from stored content (none of which can know a value that did not exist
    /// when they were written) is inert. The same assumption as the CSP nonce: markup produced for one request is not reused
    /// for another, so a cache that keeps a rendered block apart from its page would break lazy loading, not security.
    /// With no HTTP context (a unit test) the value is made per call.
    /// </summary>
    private static string LoaderKeyFor(HttpContext? http)
    {
        if (http is not null && http.Items.TryGetValue(LoaderKeyItem, out object? existing) && existing is string key)
        {
            return key;
        }

        string created = Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
        if (http is not null)
        {
            http.Items[LoaderKeyItem] = created;
        }

        return created;
    }

    /// <summary>What the wrapper hands the loader: the key, and the hosts a script or iframe of the embed may load from.</summary>
    internal sealed record LazyTrust(string Key, string Hosts);

    /// <summary>
    /// The loader's host list, the same rule as the server's <see cref="IsPlayerHost"/>: the player host of this render, the
    /// publication's own host, and the trusted domains. <c>*.example.com</c> stands for the domain and its subdomains.
    /// </summary>
    private static LazyTrust LazyTrustFor(RenderContext ctx, string? publicationHost)
    {
        var hosts = new List<string>();
        if (Uri.TryCreate(ctx.PlayerBase, UriKind.Absolute, out Uri? player))
        {
            hosts.Add(player.Host);
        }

        if (publicationHost is not null)
        {
            hosts.Add(publicationHost);
        }

        hosts.AddRange(TrustedDomains.Select(d => "*." + d));
        return new LazyTrust(ctx.LoaderKey, string.Join(' ', hosts.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    // Helpers --------------------------------------------------------------------------------------------

    private static string ScriptTag(string src, string? nonce)
    {
        string nonceAttr = string.IsNullOrEmpty(nonce) ? "" : $" nonce=\"{Enc.Encode(nonce)}\"";
        return $"<script type=\"text/javascript\"{nonceAttr} src=\"{Enc.Encode(src)}\"></script>";
    }

    private static string? NormalizeNonce(string? nonce) =>
        string.IsNullOrEmpty(nonce) || !nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_') ? null : nonce;

    private static bool CookieGranted(HttpContext? http) =>
        http?.Request.Cookies.TryGetValue(ConsentCookie, out string? value) == true && value == "1";

    /// <summary>The configured publication may sit on a different host (a dev or staging OVP); any other publication is on bbvms.com.</summary>
    private static string PlayerBaseUrl(BlueBillywigSettings settings, string publication) =>
        publication.Equals(settings.Publication, StringComparison.OrdinalIgnoreCase)
            ? settings.ResolveBaseUrl()
            : BlueBillywigUrls.PublicationBaseUrl(publication.ToLowerInvariant());

    private static bool SamePublication(RenderContext ctx) =>
        ctx.Publication.Equals(ctx.Settings.Publication, StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonNode? node) =>
        node is JsonValue v ? (v.TryGetValue(out string? s) ? s ?? "" : v.ToString()) : "";

    private static string HtmlWithBreaks(string text) =>
        string.Join("<br />\n", text.Replace("\r\n", "\n").Split('\n').Select(Enc.Encode));

    private static string StripSrt(string srt)
    {
        string text = SrtTimestamp().Replace(srt, "");
        text = SrtIndex().Replace(text, "");
        return ManyNewlines().Replace(text.Trim(), "\n\n");
    }

    /// <summary>
    /// Some imported subtitles are UTF-8 bytes that were read as Windows-1252 and encoded again. If the text
    /// is cp1252-representable and those bytes form valid UTF-8, the reinterpretation is the real text.
    /// </summary>
    internal static string FixDoubleEncodedUtf8(string text)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding cp1252 = Encoding.GetEncoding(
                1252,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            string candidate = new UTF8Encoding(false, true).GetString(cp1252.GetBytes(text));
            return candidate != text ? candidate : text;
        }
        catch (Exception ex) when (ex is EncoderFallbackException or DecoderFallbackException or ArgumentException)
        {
            return text;
        }
    }

    [GeneratedRegex(@"[^a-z0-9-]", RegexOptions.IgnoreCase)]
    private static partial Regex IsoCleanup();

    [GeneratedRegex(@"\d{2}:\d{2}:\d{2},\d{3}\s*-->\s*\d{2}:\d{2}:\d{2},\d{3}")]
    private static partial Regex SrtTimestamp();

    [GeneratedRegex(@"^\d+$", RegexOptions.Multiline)]
    private static partial Regex SrtIndex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    private sealed record RenderContext(
        BlueBillywigSettings Settings,
        string Publication,
        string PlayerBase,
        string? Nonce,
        HttpContext? Http,
        CancellationToken Ct,
        string LoaderKey)
    {
        /// <summary>The content gate is on and covers this embed, so it decides what the viewer sees.</summary>
        public bool GateActive { get; init; }

        /// <summary>The nonce provider returned a value that was refused, so nothing of this render carries a nonce.</summary>
        public bool NonceRejected { get; init; }
    }
}

namespace BlueBillywig.Umbraco.Configuration;

/// <summary>
/// Optional overrides from the <c>BlueBillywig</c> config section. A value set here wins over
/// the one saved from the backoffice, so credentials can come from the environment or a secret
/// store instead of the database.
/// </summary>
public sealed class BlueBillywigOptions
{
    public const string SectionName = "BlueBillywig";

    public string? Publication { get; set; }

    /// <summary>SAPI origin. Defaults to <c>https://{publication}.bbvms.com</c>.</summary>
    public string? BaseUrl { get; set; }

    public string? ApiId { get; set; }
    public string? ApiSecret { get; set; }
    public string? ReadOnlyApiId { get; set; }
    public string? ReadOnlyApiSecret { get; set; }

    /// <summary>
    /// The shared secret of the content protection policy that signs the gate's tokens (the reference plugin's
    /// <c>blue-billywig-cp-secret</c>). Set here, it wins over a value saved in the backoffice and the dashboard shows the
    /// field as pinned, so the secret can come from the environment or a secret store instead of the database.
    /// </summary>
    public string? GateSharedSecret { get; set; }

    /// <summary>
    /// The sentence in the consent placeholder shown to a visitor who has not consented. Plain text, encoded on output, at most
    /// 500 characters; empty keeps the built-in text. Set here it wins over the settings dashboard and the dashboard shows the
    /// field as pinned. Counterpart of the reference plugin's <c>bluebillywig_consent_placeholder_message</c> filter; a site
    /// that needs a per-visitor or localized text implements <see cref="Embedding.IBlueBillywigConsentProvider.GetPlaceholderMessage"/>.
    /// </summary>
    public string? ConsentPlaceholderMessage { get; set; }

    /// <summary>
    /// Aliases of the user groups that may delete clips, on top of access to the Blue Billywig section. Defaults to
    /// <c>admin</c> and <c>editor</c>, the nearest thing to the reference plugin's Editor level and up.
    /// </summary>
    public string[]? DeleteUserGroups { get; set; }

    /// <summary>
    /// Switches off the lazy loader completely: <c>bb-lazy.css</c> and <c>bb-lazy.js</c> are not emitted by the renderer or
    /// by <c>&lt;bb-assets /&gt;</c>, whatever else is set. Counterpart of the reference plugin's filter
    /// <c>bluebillywig_disable_lazy_loader</c>, for a theme that ships its own placeholder loader. The loader script also
    /// carries the consent placeholder's Load button and the chapter links' click handler, so a site that disables it takes
    /// those over too.
    /// </summary>
    public bool DisableLazyLoader { get; set; }

    /// <summary>
    /// Makes <c>&lt;bb-assets /&gt;</c> emit the lazy loader even when nothing on the page has asked for it yet, for embeds the
    /// renderer cannot see, such as markup a script adds later. Counterpart of <c>bluebillywig_enqueue_lazy_loader</c>
    /// returning true. Embeds rendered by this package always ship the loader themselves (the plugin's render-time safety
    /// net), so leaving this off can never produce an embed without its assets; only <see cref="DisableLazyLoader"/> does.
    /// </summary>
    public bool ForceLazyLoader { get; set; }

    /// <summary>
    /// Whether a pasted clip URL in rich text (oEmbed) is lazy loaded. Default true. Counterpart of
    /// <c>bluebillywig_lazy_load_oembed</c>.
    /// </summary>
    public bool? LazyLoadOEmbed { get; set; }

    /// <summary>
    /// Whether a pasted channel URL in rich text is lazy loaded. Default false, because a channel is usually in hero
    /// position. Counterpart of <c>bluebillywig_lazy_load_oembed_channel</c>.
    /// </summary>
    public bool? LazyLoadOEmbedChannel { get; set; }

    /// <summary>
    /// Whether a clip rendered by id from Razor (the <c>BlueBillywigVideo</c> view component or the <c>&lt;bb-video&gt;</c> tag
    /// helper) is lazy loaded when the call does not say. Default true. Counterpart of <c>bluebillywig_lazy_load_shortcode</c>.
    /// A stored property value always keeps its own <c>lazyLoad</c> choice.
    /// </summary>
    public bool? LazyLoadComponent { get; set; }

    /// <summary>
    /// Shows the visible embed problem marker (an element with the class <c>bb-embed-problem</c>) to every visitor, not only to
    /// signed-in backoffice users. For a staging site or a debugging session; leave it off in production, where the marker
    /// would tell visitors about a server-side problem. Counterpart of the reference plugin's CSP failure marker, which it
    /// shows to everyone. Default false.
    /// </summary>
    public bool ShowEmbedProblems { get; set; }
}

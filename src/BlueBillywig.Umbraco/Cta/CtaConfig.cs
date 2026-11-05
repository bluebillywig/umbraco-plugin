namespace BlueBillywig.Umbraco.Cta;

/// <summary>
/// The flat editor config of a call to action, the same keys as the reference plugin's <c>bb-cta-editor.js</c>
/// <c>DEFAULT_CTA</c>. Defaults are the plugin's defaults for an absent key. Colours are hex without <c>#</c>;
/// positions and sizes are percentages of the canvas; times are seconds.
/// </summary>
public sealed record CtaConfig
{
    /// <summary>Read-only: the SAPI id of the stored timeline. Ignored by <see cref="CtaTranslator.ToTimeline"/>.</summary>
    public string TimelineId { get; init; } = "";

    public string Title { get; init; } = "Call to action";

    /// <summary><c>link</c> or <c>form</c>. Anything else behaves as <c>link</c>, like the plugin.</summary>
    public string CtaType { get; init; } = "link";

    public string Headline { get; init; } = "";

    public string Body { get; init; } = "";

    public string ButtonLabel { get; init; } = "";

    public string Url { get; init; } = "";

    public bool OpenInNewTab { get; init; }

    public bool PauseOnShow { get; init; }

    public string FormEmailLabel { get; init; } = "Email address";

    public string FormSubmitLabel { get; init; } = "Submit";

    public string FormThankYou { get; init; } = "Thanks!";

    public string FormSubmitEndpoint { get; init; } = "";

    /// <summary>Null means "not set": the translator falls back to <see cref="HoverColor"/>'s own default chain.</summary>
    public string? BackgroundColor { get; init; }

    public string? HoverColor { get; init; }

    public string TextColor { get; init; } = "FFFFFF";

    public string FontFamily { get; init; } = "Verdana";

    /// <summary>Percent of the canvas height.</summary>
    public double FontSize { get; init; } = 3.2;

    /// <summary>Percent of the width.</summary>
    public double CornerRadius { get; init; } = 1;

    public int BorderThickness { get; init; }

    public string BorderColor { get; init; } = "ffffff";

    /// <summary>0 to 100.</summary>
    public double BackgroundAlpha { get; init; } = 100;

    /// <summary>Commonclip id of the background image; empty means a text CTA.</summary>
    public string BackgroundImageId { get; init; } = "";

    /// <summary>Optional mouse-over image, image CTAs only.</summary>
    public string HoverImageId { get; init; } = "";

    public double Xpos { get; init; } = 10;

    public double Ypos { get; init; } = 70;

    public double Width { get; init; } = 30;

    public double Height { get; init; } = 15;

    /// <summary>0 means from the clip start.</summary>
    public double StartAt { get; init; }

    /// <summary>0 means until the clip end (then <see cref="ClipLength"/> is used).</summary>
    public double EndAt { get; init; }

    /// <summary>Used only when <see cref="EndAt"/> is 0. Not stored in the timeline.</summary>
    public double ClipLength { get; init; }
}

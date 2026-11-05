using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Sapi;

namespace BlueBillywig.Umbraco.Cta;

/// <summary>
/// Translates between the flat CTA editor config and the versioned TimelineNG structure SAPI expects
/// (<c>modelVersion</c> 2). Port of the reference plugin's <c>CtaTranslator</c>.
/// <para>
/// A CTA is one <c>textLabel</c> widget (definition 10) when no background image is set, or an <c>image</c> widget
/// (definition 2) plus, when there is any text, an overlapping <c>textLabel</c> carrying the text. The output is a
/// <see cref="JsonObject"/> whose key order is fixed by construction, so <see cref="Serialize"/> is byte-stable.
/// </para>
/// <para>
/// Seam for the wiring: the caller must (1) run <see cref="CtaValidator"/> on editor input, (2) look the clip's current
/// timeline up with <see cref="CtaTimelineLocator"/> and pass it through <see cref="CtaSavePlanner"/>, which refuses a
/// timeline this class did not author and refuses to guess when the lookup failed, and (3) only then PUT or POST.
/// Nothing in this class talks to SAPI.
/// </para>
/// </summary>
public sealed partial class CtaTranslator
{
    /// <summary>Canvas coordinate space for the position percentages.</summary>
    public const int DesignWidth = 1920;

    public const int DesignHeight = 1080;

    /// <summary>Marker on <c>widgets[0].label</c> and the timeline label that says "we authored this".</summary>
    public const string AuthoredLabelPrefix = "bb-cta:";

    private const string FallbackColor = "002837";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly ICtaIdGenerator _ids;

    public CtaTranslator(ICtaIdGenerator? ids = null)
    {
        _ids = ids ?? new RandomCtaIdGenerator();
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{3,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    /// <summary>The compact, deterministic text form of a timeline (default JSON encoder: no raw &lt; &gt; &amp; or non-ASCII).</summary>
    public static string Serialize(JsonNode timeline) => timeline.ToJsonString(Compact);

    /// <summary>
    /// True if <paramref name="timeline"/> is absent or was authored by this package (or the reference plugin, which
    /// writes the same marker), so the caller may overwrite it. A timeline with unfamiliar widgets, such as a hand-built
    /// interactive video, is someone else's and must not be replaced.
    /// </summary>
    public static bool IsAuthoredByUs(JsonNode? timeline)
    {
        if (timeline is not JsonObject root || root.Count == 0)
        {
            return true;
        }

        // Both labels: editing the timeline in OVP6 changes the widget label but keeps the timeline label.
        if (SapiJson.At(root, "widgets", 0, "label") is JsonValue wl && wl.TryGetValue(out string? widgetLabel) && widgetLabel.StartsWith(AuthoredLabelPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (SapiJson.At(root, "label") is JsonValue tl && tl.TryGetValue(out string? timelineLabel) && timelineLabel.StartsWith(AuthoredLabelPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        // Backwards compatible with the plugin: one or two textLabel or image widgets are a CTA, with or without the marker.
        if (root["widgets"] is JsonArray widgets && widgets.Count is >= 1 and <= 2)
        {
            return widgets.All(w => SapiJson.Str(SapiJson.At(w, "widgetdefinitiontype")) is "textLabel" or "image");
        }

        return false;
    }

    /// <summary>
    /// Builds the full TimelineNG for a clip. When <paramref name="existing"/> is given, its timeline id and the widget,
    /// track, in/out point, trigger and action ids are reused, so save, reload, save is byte-stable.
    /// </summary>
    public JsonObject ToTimeline(long clipId, CtaConfig cfg, JsonNode? existing = null)
    {
        string widgetId = ReuseOr(existing, "w", "widgets", 0, "id");
        string widgetId2 = ReuseOr(existing, "w", "widgets", 1, "id");
        string trackId = ReuseOr(existing, "t", "order", 0, "id");
        string trackId2 = ReuseOr(existing, "t", "order", 1, "id");
        string iopId = ReuseOr(existing, "i", "order", 0, "inoutpoints", 0, "id");
        string iopId2 = ReuseOr(existing, "i", "order", 1, "inoutpoints", 0, "id");

        JsonNode? prevOnClick = SapiJson.At(existing, "widgets", 0, "statesets", 0, "states", 0, "config", "onclick");

        double startAt = Fin(cfg.StartAt, 0);
        double outSeconds = Fin(cfg.EndAt, 0) != 0 ? Fin(cfg.EndAt, 0) : Fin(cfg.ClipLength, 0);

        bool hasImage = !SapiJson.PhpEmpty(cfg.BackgroundImageId);
        string label = AuthoredLabelPrefix + cfg.Title;
        string text = CtaText.Combine(cfg);
        string cssClass = CssClassFor(cfg);
        string buttonLabel = CtaText.Slot(cfg.ButtonLabel);

        JsonObject config;
        string widgetType;
        string widgetTypeId;
        JsonNode mouseover;
        if (hasImage)
        {
            // All the standard fields must be present for the player to render the image widget; same shape OVP6 writes.
            config = new JsonObject
            {
                ["xpos"] = Pct(cfg.Xpos, 10),
                ["ypos"] = Pct(cfg.Ypos, 70),
                ["width"] = Pct(cfg.Width, 30),
                ["height"] = Pct(cfg.Height, 15),
                ["imageid"] = cfg.BackgroundImageId,
                ["imageid_mouseover"] = "",
                ["imageid_mousedown"] = "",
                ["imageid_toggle"] = "",
                ["alpha"] = "100",
                ["onclick"] = ClickActions(cfg, prevOnClick),
                ["onclickToggleOn"] = null,
                ["onclickToggleOff"] = null,
                ["onshow"] = null,
                ["onhide"] = null,
                ["ontransitionDone"] = null,
                ["onmouseover"] = null,
                ["onmouseout"] = null,
                ["ignoreTimelineShow"] = "false",
                ["ignoreTimelineHide"] = "false",
                ["preload"] = "true",
                ["cssclass"] = cssClass,
                ["aria_label"] = buttonLabel,
                ["useAsKPI"] = "false",
                ["exposeWidgetStats"] = "false",
                ["stats_shows"] = "0",
                ["stats_clicks"] = "0",
            };
            widgetType = "image";
            widgetTypeId = "2";
            mouseover = SapiJson.PhpEmpty(cfg.HoverImageId) ? new JsonArray() : new JsonObject { ["imageid"] = cfg.HoverImageId };
        }
        else
        {
            config = new JsonObject
            {
                ["xpos"] = Pct(cfg.Xpos, 10),
                ["ypos"] = Pct(cfg.Ypos, 70),
                ["width"] = Pct(cfg.Width, 30),
                ["height"] = Pct(cfg.Height, 15),
                ["text"] = text,
                ["textFormat"] = TextFormat(cfg),
                ["backgroundcolor"] = Hex(cfg.BackgroundColor ?? FallbackColor),
                ["cornerradius"] = Pct(cfg.CornerRadius, 1),
                ["borderthickness"] = cfg.BorderThickness.ToString(CultureInfo.InvariantCulture),
                ["bordercolor"] = Hex(cfg.BorderColor),
                ["background_alpha"] = Pct(cfg.BackgroundAlpha, 100),
                ["preload"] = "true",
                ["aria_label"] = buttonLabel,
                ["cssclass"] = cssClass,
                ["useAsKPI"] = "false",
                ["exposeWidgetStats"] = "true",
                ["ignoreTimelineShow"] = "false",
                ["ignoreTimelineHide"] = "false",
                ["onshow"] = null,
                ["onhide"] = null,
                ["onmouseover"] = null,
                ["onmouseout"] = null,
                ["onclick"] = ClickActions(cfg, prevOnClick),
            };
            widgetType = "textLabel";
            widgetTypeId = "10";
            mouseover = new JsonObject { ["backgroundcolor"] = Hex(cfg.HoverColor ?? cfg.BackgroundColor ?? "33535f") };
        }

        var widgets = new JsonArray { Widget(widgetId, widgetType, widgetTypeId, label, config, mouseover) };

        // Any text over an image gets its own overlay widget. Keyed on the combined text, not the button label alone: the
        // plugin's 2.0.4 fix, because an image CTA with only a headline or body used to lose that text at write time.
        bool hasTextOverImage = hasImage && text.Length > 0;
        if (hasTextOverImage)
        {
            var textConfig = new JsonObject
            {
                ["xpos"] = Pct(cfg.Xpos, 10),
                ["ypos"] = Pct(cfg.Ypos, 70),
                ["width"] = Pct(cfg.Width, 30),
                ["height"] = Pct(cfg.Height, 15),
                ["text"] = text,
                ["textFormat"] = TextFormat(cfg),
                ["backgroundcolor"] = "000000",
                ["background_alpha"] = "0%",
                ["cornerradius"] = "0",
                ["borderthickness"] = "0",
                ["bordercolor"] = "000000",
                ["preload"] = "true",
                ["aria_label"] = "",
                ["cssclass"] = "",
                ["useAsKPI"] = "false",
                ["exposeWidgetStats"] = "false",
                ["ignoreTimelineShow"] = "false",
                ["ignoreTimelineHide"] = "false",
                ["onshow"] = null,
                ["onhide"] = null,
                ["onmouseover"] = null,
                ["onmouseout"] = null,
                ["onclick"] = null,
            };
            widgets.Add(Widget(widgetId2, "textLabel", "10", label + " (text)", textConfig, new JsonArray()));
        }

        var order = new JsonArray { Track(trackId, widgetId, iopId, startAt, outSeconds) };
        if (hasTextOverImage)
        {
            order.Add(Track(trackId2, widgetId2, iopId2, startAt, outSeconds));
        }

        var timeline = new JsonObject
        {
            ["type"] = "TimelineNG",
            ["subtype"] = "versioned",
            ["modelVersion"] = "2",
            ["status"] = "published",
            ["parentEntityType"] = "MediaClip",
            ["parentEntityId"] = clipId.ToString(CultureInfo.InvariantCulture),
            ["interactions"] = new JsonArray(),
            ["groupTags"] = new JsonArray(),
            ["widgets"] = widgets,
            ["order"] = order,
            ["forms"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "",
                    ["method"] = "POST",
                    ["action"] = "",
                    ["email"] = "",
                    ["formEmailTemplate"] = "",
                    ["formEmailFrom"] = "",
                    ["onSuccess"] = new JsonArray(),
                    ["onFailure"] = new JsonArray(),
                    ["onAllWidgetsValidated"] = new JsonArray(),
                    ["id"] = "1",
                },
            },
            ["options"] = new JsonObject
            {
                ["exitArea"] = false,
                ["designWidth"] = DesignWidth,
                ["designHeight"] = DesignHeight,
            },
            ["label"] = label,
        };

        if (SapiJson.At(existing, "id") is { } id && !SapiJson.PhpEmpty(id))
        {
            timeline["id"] = id.DeepClone();
        }

        return timeline;
    }

    /// <summary>
    /// Extracts the editor config from a stored TimelineNG. Null when the shape is not recognised: callers must tell
    /// "no timeline" from "timeline present but foreign" and not overwrite the latter.
    /// </summary>
    public static CtaConfig? FromTimeline(JsonNode? timeline)
    {
        JsonNode? widget0 = SapiJson.At(timeline, "widgets", 0);
        JsonNode? cfg = SapiJson.At(widget0, "statesets", 0, "states", 0, "config");
        if (SapiJson.PhpEmpty(cfg))
        {
            return null;
        }

        // For an image CTA the text is on the overlay widget, not on the image widget (the plugin's 2.0.4 fix).
        JsonNode? textCfg = cfg;
        if (SapiJson.Str(SapiJson.At(cfg, "text")) is var t0 && CtaText.PhpTrim(t0).Length == 0 && timeline?["widgets"] is JsonArray all)
        {
            for (int i = 1; i < all.Count; i++)
            {
                JsonNode? wc = SapiJson.At(all[i], "statesets", 0, "states", 0, "config");
                if (wc is JsonObject && CtaText.PhpTrim(SapiJson.Str(SapiJson.At(wc, "text"))).Length > 0)
                {
                    textCfg = wc;
                    break;
                }
            }
        }

        JsonNode? mouseover = null;
        if (SapiJson.At(widget0, "statesets", 0, "states") is JsonArray states)
        {
            foreach (JsonNode? st in states)
            {
                if (SapiJson.Str(SapiJson.At(st, "name")) == "mouseover" && SapiJson.At(st, "config") is JsonNode mo)
                {
                    mouseover = mo;
                    break;
                }
            }
        }

        JsonNode? inout = SapiJson.At(timeline, "order", 0, "inoutpoints", 0);

        string url = "";
        bool newTab = true;
        bool pause = false;
        if (SapiJson.At(cfg, "onclick") is JsonArray onclick)
        {
            foreach (JsonNode? trigger in onclick)
            {
                if (SapiJson.At(trigger, "actions") is not JsonArray actions)
                {
                    continue;
                }

                foreach (JsonNode? action in actions)
                {
                    string entity = SapiJson.Str(SapiJson.At(action, "entity"));
                    if (entity == "navigation" && SapiJson.At(action, "parameters") is JsonArray parameters)
                    {
                        foreach (JsonNode? p in parameters)
                        {
                            string name = SapiJson.Str(SapiJson.At(p, "name"));
                            if (name == "p_0")
                            {
                                url = SapiJson.Str(SapiJson.At(p, "value"));
                            }

                            if (name == "p_1")
                            {
                                newTab = SapiJson.Str(SapiJson.At(p, "value")) == "_blank";
                            }
                        }
                    }

                    if (entity == "player" && SapiJson.Str(SapiJson.At(action, "command")) == "pause")
                    {
                        pause = true;
                    }
                }
            }
        }

        string cssClass = SapiJson.Str(SapiJson.At(cfg, "cssclass"));
        bool isForm = cssClass.Contains("bb-cta-form", StringComparison.Ordinal);
        JsonObject? form = DecodeFormConfig(cssClass);

        string label = SapiJson.At(widget0, "label") is { } wl ? SapiJson.Str(wl) : SapiJson.Str(SapiJson.At(timeline, "label"));
        if (label.StartsWith(AuthoredLabelPrefix, StringComparison.Ordinal))
        {
            label = label[AuthoredLabelPrefix.Length..];
        }

        (string headline, string body, string buttonLabel) = CtaText.Split(SapiJson.Str(SapiJson.At(textCfg, "text")), CtaText.MaskFrom(cssClass));

        JsonNode? format = SapiJson.At(textCfg, "textFormat", "textFormat");
        string backgroundColor = Or(SapiJson.At(cfg, "backgroundcolor"), FallbackColor);

        return new CtaConfig
        {
            TimelineId = SapiJson.Str(SapiJson.At(timeline, "id")),
            Title = label,
            CtaType = isForm ? "form" : "link",
            Headline = headline,
            Body = body,
            ButtonLabel = buttonLabel,
            Url = url,
            OpenInNewTab = newTab,
            PauseOnShow = pause,
            FormEmailLabel = FormField(form, "emailLabel", "Email address"),
            FormSubmitLabel = FormField(form, "submitLabel", "Submit"),
            FormThankYou = FormField(form, "thankYou", "Thanks!"),
            FormSubmitEndpoint = FormField(form, "submitEndpoint", ""),
            Xpos = NumPct(SapiJson.At(cfg, "xpos"), 10),
            Ypos = NumPct(SapiJson.At(cfg, "ypos"), 70),
            Width = NumPct(SapiJson.At(cfg, "width"), 30),
            Height = NumPct(SapiJson.At(cfg, "height"), 15),
            BackgroundColor = backgroundColor,
            HoverColor = Or(SapiJson.At(mouseover, "backgroundcolor"), SapiJson.At(cfg, "backgroundcolor") is { } bg ? SapiJson.Str(bg) : "33535f"),
            TextColor = Or(SapiJson.At(format, "color"), "FFFFFF"),
            FontFamily = Or(SapiJson.At(format, "font"), "Verdana"),
            FontSize = NumPct(SapiJson.At(format, "fontsize"), 3.2),
            CornerRadius = NumPct(SapiJson.At(cfg, "cornerradius"), 1),
            BorderThickness = (int)Math.Clamp(SapiJson.Int(SapiJson.At(cfg, "borderthickness")), int.MinValue, int.MaxValue),
            BorderColor = Or(SapiJson.At(cfg, "bordercolor"), "ffffff"),
            BackgroundAlpha = NumPct(SapiJson.At(cfg, "background_alpha"), 100),
            BackgroundImageId = SapiJson.Str(SapiJson.At(cfg, "imageid")),
            // Added over the plugin, whose reload dropped the mouse-over image and so lost it on the next save.
            HoverImageId = SapiJson.Str(SapiJson.At(mouseover, "imageid")),
            StartAt = SapiJson.Num(SapiJson.At(inout, "inpoint"), 0),
            EndAt = SapiJson.Num(SapiJson.At(inout, "outpoint"), 0),
        };
    }

    /// <summary>A hex colour with <c>#</c> stripped, or the brand teal when it is not 3 to 8 hex digits.</summary>
    public static string Hex(string? value)
    {
        string s = (value ?? "").TrimStart('#');
        return HexColor().IsMatch(s) ? s : FallbackColor;
    }

    /// <summary>Strict form for the validator: the stripped colour, or null when invalid.</summary>
    public static string? HexStrict(string? value)
    {
        string s = (value ?? "").TrimStart('#');
        return HexColor().IsMatch(s) ? s : null;
    }

    // Widget parts ---------------------------------------------------------------------------------------

    private static JsonObject Widget(string id, string type, string typeId, string label, JsonObject config, JsonNode mouseover) => new()
    {
        ["id"] = id,
        ["status"] = "",
        ["widgetdefinitiontype"] = type,
        ["widgetdefinitionid"] = typeId,
        ["label"] = label,
        ["statesets"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "default",
                ["states"] = new JsonArray
                {
                    State("default", config),
                    State("mouseover", mouseover),
                    State("mousedown", new JsonArray()),
                    State("toggled", new JsonArray()),
                    State("toggledmouseover", new JsonArray()),
                    State("toggledmousedown", new JsonArray()),
                },
            },
        },
        ["grouping"] = new JsonArray(),
        ["animations"] = new JsonArray(),
    };

    private static JsonObject State(string name, JsonNode config) => new() { ["name"] = name, ["config"] = config };

    private static JsonObject Track(string trackId, string widgetId, string iopId, double inpoint, double outpoint) => new()
    {
        ["id"] = trackId,
        ["type"] = "track",
        ["widgetid"] = widgetId,
        ["targetIndex"] = null,
        ["inoutpoints"] = new JsonArray
        {
            new JsonObject { ["id"] = iopId, ["inpoint"] = inpoint, ["outpoint"] = outpoint },
        },
    };

    private static JsonObject TextFormat(CtaConfig cfg) => new()
    {
        ["textFormat"] = new JsonObject
        {
            ["bold"] = "false",
            ["italic"] = "false",
            ["underline"] = "false",
            ["align"] = "center",
            ["verticalalign"] = "center",
            ["font"] = cfg.FontFamily,
            ["embeddedfont"] = "-1",
            ["color"] = Hex(cfg.TextColor),
            ["margin"] = "1.111%;0.625%;1.111%;0.625%",
            ["fontsize"] = Pct(cfg.FontSize, 3.2),
        },
    };

    // Click actions and classes --------------------------------------------------------------------------

    /// <summary>
    /// CSS classes marking CTA intent so the frontend can render form mode. Form config is base64url JSON inside the
    /// class string, which is why the URL in it goes through <see cref="CtaUrl"/>: it is later POSTed to.
    /// </summary>
    private static string CssClassFor(CtaConfig cfg)
    {
        var classes = new List<string>();
        if (cfg.CtaType == "form")
        {
            var form = new JsonObject
            {
                ["emailLabel"] = cfg.FormEmailLabel,
                ["submitLabel"] = cfg.FormSubmitLabel,
                ["thankYou"] = cfg.FormThankYou,
                ["submitEndpoint"] = CtaUrl.SanitizeEndpoint(cfg.FormSubmitEndpoint),
            };
            classes.Add("bb-cta-form");
            classes.Add("bb-cta-form-config-" + Base64Url.Encode(form.ToJsonString(Compact)));
        }

        string mask = CtaText.MaskFor(cfg);
        if (mask.Length > 0)
        {
            classes.Add(CtaText.MaskPrefix + mask);
        }

        return string.Join(' ', classes);
    }

    private JsonNode? ClickActions(CtaConfig cfg, JsonNode? prevOnClick)
    {
        // Reuse trigger and action ids from the existing timeline so save, reload, save is byte-stable.
        PrevIds ids = ExtractPrevIds(prevOnClick);

        if (cfg.CtaType == "form")
        {
            return cfg.PauseOnShow ? new JsonArray { PauseGroup(ids) } : null;
        }

        string url = CtaUrl.Sanitize(cfg.Url);
        if (url.Length == 0)
        {
            return null;
        }

        var groups = new JsonArray
        {
            new JsonObject
            {
                ["id"] = ids.NavGroup ?? _ids.Next("c"),
                ["condition"] = new JsonObject { ["variables"] = new JsonArray(), ["expression"] = "" },
                ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["entity"] = "navigation",
                        ["command"] = "",
                        ["parameters"] = new JsonArray
                        {
                            new JsonObject { ["name"] = "p_0", ["value"] = url },
                            new JsonObject { ["name"] = "p_1", ["value"] = cfg.OpenInNewTab ? "_blank" : "_self" },
                            new JsonObject { ["name"] = "p_2", ["value"] = "" },
                        },
                        ["id"] = ids.NavAction ?? _ids.Next("a"),
                    },
                },
                ["else_actions"] = new JsonArray(),
                ["type"] = "simple",
            },
        };
        if (cfg.PauseOnShow)
        {
            groups.Add(PauseGroup(ids));
        }

        return groups;
    }

    private JsonObject PauseGroup(PrevIds ids) => new()
    {
        ["id"] = ids.PauseGroup ?? _ids.Next("c"),
        ["condition"] = new JsonObject { ["variables"] = new JsonArray(), ["expression"] = "" },
        ["actions"] = new JsonArray
        {
            new JsonObject
            {
                ["entity"] = "player",
                ["command"] = "pause",
                ["parameters"] = new JsonArray(),
                ["id"] = ids.PauseAction ?? _ids.Next("a"),
            },
        },
        ["else_actions"] = new JsonArray(),
        ["type"] = "simple",
    };

    private readonly record struct PrevIds(string? NavGroup, string? NavAction, string? PauseGroup, string? PauseAction);

    private static PrevIds ExtractPrevIds(JsonNode? prev)
    {
        string? navGroup = null, navAction = null, pauseGroup = null, pauseAction = null;
        if (prev is JsonArray groups)
        {
            foreach (JsonNode? group in groups)
            {
                if (SapiJson.At(group, "actions") is not JsonArray actions)
                {
                    continue;
                }

                foreach (JsonNode? action in actions)
                {
                    string entity = SapiJson.Str(SapiJson.At(action, "entity"));
                    if (entity == "navigation")
                    {
                        navGroup = NonEmpty(SapiJson.At(group, "id"));
                        navAction = NonEmpty(SapiJson.At(action, "id"));
                    }
                    else if (entity == "player")
                    {
                        pauseGroup = NonEmpty(SapiJson.At(group, "id"));
                        pauseAction = NonEmpty(SapiJson.At(action, "id"));
                    }
                }
            }
        }

        return new PrevIds(navGroup, navAction, pauseGroup, pauseAction);
    }

    /// <summary>Decodes the base64url JSON in a <c>bb-cta-form-config-</c> class. Null when absent or malformed.</summary>
    private static JsonObject? DecodeFormConfig(string cssClass)
    {
        Match m = FormConfigClass().Match(cssClass);
        if (!m.Success)
        {
            return null;
        }

        byte[]? raw = Base64Url.TryDecode(m.Groups[1].Value);
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"bb-cta-form-config-([A-Za-z0-9_\-]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FormConfigClass();

    // Small helpers --------------------------------------------------------------------------------------

    private string ReuseOr(JsonNode? existing, string prefix, params object[] path)
    {
        // Only a non-empty string counts as reusable, like the plugin.
        return SapiJson.At(existing, path) is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : _ids.Next(prefix);
    }

    private static string? NonEmpty(JsonNode? node) => SapiJson.At(node) is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    private static string FormField(JsonObject? form, string key, string fallback) =>
        form is not null && form.TryGetPropertyValue(key, out JsonNode? v) && v is not null ? SapiJson.Str(v) : fallback;

    /// <summary>PHP <c>$x ?? default</c> for a string value: only null falls back.</summary>
    private static string Or(JsonNode? node, string fallback) => node is null ? fallback : SapiJson.Str(node);

    /// <summary>A number the plugin would have formatted as <c>strval(floatval(n)) . '%'</c>. Non-finite input becomes the default.</summary>
    private static string Pct(double value, double fallback) => Fin(value, fallback).ToString("R", CultureInfo.InvariantCulture) + "%";

    private static double NumPct(JsonNode? node, double fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        return SapiJson.Str(node) is { } s && node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? SapiJson.ParsePrefix(s.TrimEnd('%'))
            : SapiJson.Num(node, fallback);
    }

    /// <summary>A finite number (else the default), with negative zero folded to zero: JSON text cannot keep the sign through a reload.</summary>
    private static double Fin(double value, double fallback) => double.IsFinite(value) ? (value == 0 ? 0 : value) : fallback;
}

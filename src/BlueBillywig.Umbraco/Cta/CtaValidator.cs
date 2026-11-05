namespace BlueBillywig.Umbraco.Cta;

/// <summary>The input rules of the plugin's <c>save_cta</c>, applied at the boundary instead of silently defaulting.</summary>
public static class CtaValidator
{
    public static IReadOnlyList<string> Validate(CtaConfig cfg)
    {
        var errors = new List<string>();
        if (cfg.CtaType == "form")
        {
            if (string.IsNullOrEmpty(cfg.FormSubmitLabel) || cfg.FormSubmitLabel == "0")
            {
                errors.Add("Submit label is required for form CTAs");
            }

            if (cfg.FormSubmitEndpoint.Length > 0 && !CtaUrl.IsEndpointUrl(cfg.FormSubmitEndpoint))
            {
                errors.Add("Submit endpoint must be an https URL (http works only for localhost)");
            }
        }
        else
        {
            if (string.IsNullOrEmpty(cfg.Url) || cfg.Url == "0")
            {
                errors.Add("URL is required");
            }
            else if (!CtaUrl.IsHttpUrl(cfg.Url))
            {
                errors.Add("URL must be a valid http(s) URL");
            }

            if (Sapi.SapiJson.PhpEmpty(cfg.ButtonLabel) && Sapi.SapiJson.PhpEmpty(cfg.BackgroundImageId))
            {
                errors.Add("Button label or background image is required");
            }
        }

        if (!Sapi.SapiJson.PhpEmpty(cfg.BackgroundImageId) && !cfg.BackgroundImageId.All(char.IsAsciiDigit))
        {
            errors.Add("Background image ID must be numeric");
        }

        if (cfg.HoverImageId.Length > 0 && !cfg.HoverImageId.All(char.IsAsciiDigit))
        {
            errors.Add("Hover image ID must be numeric");
        }

        foreach ((string key, string? value) in new[] { ("backgroundColor", cfg.BackgroundColor), ("hoverColor", cfg.HoverColor), ("textColor", cfg.TextColor), ("borderColor", cfg.BorderColor) })
        {
            if (!string.IsNullOrEmpty(value) && CtaTranslator.HexStrict(value) is null)
            {
                errors.Add($"{key}: Invalid hex color: {value.TrimStart('#')}");
            }
        }

        // An outpoint of 0 would hide the CTA forever, so "until end" needs the clip length.
        if (cfg.EndAt == 0 && cfg.ClipLength == 0)
        {
            errors.Add("Clip duration not available yet");
        }

        return errors;
    }
}

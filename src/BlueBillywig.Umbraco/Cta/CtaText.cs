using System.Text.RegularExpressions;

namespace BlueBillywig.Umbraco.Cta;

/// <summary>
/// Text slot handling of the plugin's <c>CtaTranslator</c>: <c>combineText</c>, <c>splitText</c> and the
/// <c>bb-cta-tf-</c> mask that makes the split lossless (the 2.0.4 fix for a headline coming back as the button label).
/// </summary>
public static partial class CtaText
{
    /// <summary>Marker class carrying which text slots were filled. Deliberately not a <c>bb-cta-form*</c> class.</summary>
    public const string MaskPrefix = "bb-cta-tf-";

    private static readonly char[] PhpWhitespace = [' ', '\t', '\n', '\r', '\0', '\u000B'];

    [GeneratedRegex(@"\n\n+", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\n{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"(?:^|\s)bb-cta-tf-([01]{3})(?=\s|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MaskClass();

    /// <summary>PHP <c>trim()</c>: only space, tab, newline, CR, NUL and vertical tab, not Unicode spaces.</summary>
    public static string PhpTrim(string text) => text.Trim(PhpWhitespace);

    /// <summary>
    /// One slot's text as stored: trimmed, line endings unified, and blank lines inside the value folded to a single line
    /// break. Deviation from the plugin, which kept "A\n\nB" inside a slot; the blank line is the slot separator in the
    /// stored blob, so such a value changed the slot count and the mask on the next save (not byte-stable).
    /// </summary>
    internal static string Slot(string? value) =>
        BlankLines().Replace(PhpTrim((value ?? "").Replace("\r\n", "\n").Replace('\r', '\n')), "\n");

    /// <summary>Which of headline, body and button label are non-empty, as a 3-bit string; "" when there is no text.</summary>
    public static string MaskFor(CtaConfig cfg)
    {
        string mask = (Slot(cfg.Headline).Length > 0 ? "1" : "0") + (Slot(cfg.Body).Length > 0 ? "1" : "0") + (Slot(cfg.ButtonLabel).Length > 0 ? "1" : "0");
        return mask == "000" ? "" : mask;
    }

    /// <summary>Headline, body and button label as one blob, blank-line separated, empty slots dropped.</summary>
    public static string Combine(CtaConfig cfg) =>
        string.Join("\n\n", new[] { Slot(cfg.Headline), Slot(cfg.Body), Slot(cfg.ButtonLabel) }.Where(s => s.Length > 0));

    /// <summary>The mask out of a stored <c>cssclass</c>, matched as a whole class token. "" when absent.</summary>
    public static string MaskFrom(string cssClass)
    {
        Match m = MaskClass().Match(cssClass);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// Inverse of <see cref="Combine"/>: (headline, body, button label). The mask is used while its bit count still
    /// matches the number of parts; if the text was edited in OVP6 it falls back to the historical positional guess
    /// (1 part: button; 2 parts: headline and button; 3 parts: all), which is also what pre-mask timelines get.
    /// </summary>
    public static (string Headline, string Body, string ButtonLabel) Split(string? text, string mask = "")
    {
        string[] parts = ParagraphBreak().Split(text ?? "").Select(PhpTrim).Where(s => s.Length > 0).ToArray();
        int n = parts.Length;
        if (n == 0)
        {
            return ("", "", "");
        }

        if (mask.Length == 3 && mask.All(c => c is '0' or '1') && mask.Count(c => c == '1') == n)
        {
            string[] slots = ["", "", ""];
            int next = 0;
            for (int slot = 0; slot < 3; slot++)
            {
                if (mask[slot] == '1')
                {
                    slots[slot] = parts[next++];
                }
            }

            return (slots[0], slots[1], slots[2]);
        }

        return n switch
        {
            1 => ("", "", parts[0]),
            2 => (parts[0], "", parts[1]),
            _ => (parts[0], parts[1], parts[2]),
        };
    }

}

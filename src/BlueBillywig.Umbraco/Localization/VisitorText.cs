using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace BlueBillywig.Umbraco.Localization;

/// <summary>
/// The texts a visitor sees that the package itself writes: the consent placeholder and its Load button, the gate message, the
/// transcript and chapter labels, the player's accessible names, and the defaults of the email form overlay. The reference
/// plugin translates the same strings through its translation files (PARITY T2); here they are JSON files embedded in the
/// assembly, one per language (<c>Localization/Visitor/*.json</c>), with English as the source and the fallback.
/// </summary>
/// <remarks>
/// The language is the two-letter language of <see cref="CultureInfo.CurrentUICulture"/> (<c>nb-NO</c> and <c>da-DK</c> are
/// <c>nb</c> and <c>da</c>); Norwegian <c>no</c> and <c>nn</c> read the Bokmål file. A language without a file, or a file that lacks a
/// key or holds an empty value for it, reads English. A key that does not exist at all comes back as the key, so a typo is visible
/// and never an empty button. Text an editor typed (a custom consent message, a custom gate message, CTA labels) is never
/// translated: it is theirs.
/// </remarks>
public static class VisitorText
{
    /// <summary>The languages with a file. English is the source text.</summary>
    public static readonly IReadOnlyList<string> Languages = ["en", "da", "de", "fi", "fr", "nb", "nl", "sv"];

    private const string Fallback = "en";

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The text for <paramref name="key"/> in the culture (default: the current UI culture); <c>{0}</c>, <c>{1}</c> are replaced by <paramref name="args"/>.</summary>
    public static string Get(string key, CultureInfo? culture = null, params object[] args)
    {
        return Resolve(Table(LanguageOf(culture ?? CultureInfo.CurrentUICulture)), key, args);
    }

    /// <summary>
    /// The lookup rule on its own, so a test can hand it a language table with a gap: the language's text when it has a non-blank
    /// one, else the English text, else the key.
    /// </summary>
    internal static string Resolve(IReadOnlyDictionary<string, string> language, string key, params object[] args)
    {
        string? text = (language.TryGetValue(key, out string? own) && !string.IsNullOrWhiteSpace(own) ? own : null) ?? Lookup(Fallback, key);
        return text is null ? key : Format(text, args);
    }

    /// <summary>The file language for a culture: its two-letter language when we have a file, <c>nb</c> for <c>no</c> and <c>nn</c>, else English.</summary>
    public static string LanguageOf(CultureInfo culture)
    {
        string language = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        if (language is "no" or "nn")
        {
            language = "nb";
        }

        return Languages.Contains(language) ? language : Fallback;
    }

    /// <summary>All keys and values of one language file as shipped (no fallback applied). Empty for a language without a file.</summary>
    public static IReadOnlyDictionary<string, string> Table(string language)
        => Tables.GetOrAdd(language, Load);

    /// <summary>The same keys the browser scripts need, as a flat map for the language: what the renderer prints on a script tag.</summary>
    public static IReadOnlyDictionary<string, string> ForScript(IEnumerable<string> keys, CultureInfo? culture = null)
        => keys.ToDictionary(k => k, k => Get(k, culture));

    private static string? Lookup(string language, string key)
        => Table(language).TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string Format(string text, object[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            text = text.Replace("{" + i.ToString(CultureInfo.InvariantCulture) + "}", Convert.ToString(args[i], CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
        }

        return text;
    }

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        Assembly assembly = typeof(VisitorText).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream($"BlueBillywig.Visitor.{language}.json");
        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}

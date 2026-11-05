using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Localization;

namespace BlueBillywig.Umbraco.Tests.Localization;

/// <summary>
/// T2: the texts a visitor sees. Every language file has exactly the English keys and no empty value, a missing text reads
/// English, and the renderer, the gate message and the script tags use the request's language.
/// </summary>
public partial class VisitorTextTests
{
    /// <summary>Other tests assert the English texts; pin the default UI culture so a developer machine in another language cannot change them.</summary>
    [ModuleInitializer]
    internal static void PinEnglish()
    {
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en");
        CultureInfo.CurrentUICulture = new CultureInfo("en");
    }

    private static readonly string[] Others = ["da", "de", "fi", "fr", "nb", "nl", "sv"];

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();

    private static string[] Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value).Order().ToArray();

    [Fact]
    public void The_languages_are_english_plus_the_seven_of_the_reference_plugin()
    {
        Assert.Equal(["en", "da", "de", "fi", "fr", "nb", "nl", "sv"], VisitorText.Languages);
    }

    [Theory]
    [InlineData("da")]
    [InlineData("de")]
    [InlineData("fi")]
    [InlineData("fr")]
    [InlineData("nb")]
    [InlineData("nl")]
    [InlineData("sv")]
    public void A_language_has_exactly_the_english_keys_no_empty_value_and_the_same_placeholders(string language)
    {
        IReadOnlyDictionary<string, string> english = VisitorText.Table("en");
        IReadOnlyDictionary<string, string> table = VisitorText.Table(language);

        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(), table.Keys.Order());
        foreach ((string key, string value) in table)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language}.{key} is empty");
            Assert.Equal(Placeholders(english[key]), Placeholders(value));
        }
    }

    [Fact]
    public void English_has_no_empty_value_and_the_built_in_consent_text_is_the_constant_the_docs_quote()
    {
        foreach ((string key, string value) in VisitorText.Table("en"))
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"en.{key} is empty");
        }

        Assert.Equal(ConsentMessage.Default, VisitorText.Get("consentMessage", Culture("en")));
    }

    [Theory]
    [InlineData("nl-NL", "Video laden")]
    [InlineData("nl", "Video laden")]
    [InlineData("da-DK", "Indlæs video")]
    [InlineData("de-AT", "Video laden")]
    [InlineData("fi-FI", "Lataa video")]
    [InlineData("fr-BE", "Charger la vidéo")]
    [InlineData("nb-NO", "Last video")]
    [InlineData("no", "Last video")]
    [InlineData("nn-NO", "Last video")]
    [InlineData("sv-SE", "Ladda video")]
    [InlineData("en-GB", "Load video")]
    public void The_language_comes_from_the_culture(string culture, string expected)
    {
        Assert.Equal(expected, VisitorText.Get("loadVideo", Culture(culture)));
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("es-ES")]
    [InlineData("")]
    public void A_language_without_a_file_reads_english(string culture)
    {
        Assert.Equal("Load video", VisitorText.Get("loadVideo", Culture(culture)));
    }

    [Fact]
    public void A_missing_or_blank_text_in_a_language_reads_english_and_an_unknown_key_reads_as_itself()
    {
        var gap = new Dictionary<string, string> { ["loadVideo"] = "Video laden", ["loadChannel"] = "   ", ["gateLogIn"] = "" };

        Assert.Equal("Video laden", VisitorText.Resolve(gap, "loadVideo"));
        Assert.Equal("Load video channel", VisitorText.Resolve(gap, "loadChannel"));
        Assert.Equal("Log in", VisitorText.Resolve(gap, "gateLogIn"));
        Assert.Equal("Load Shorts experience", VisitorText.Resolve(gap, "loadShorts"));
        Assert.Equal("noSuchKey", VisitorText.Resolve(gap, "noSuchKey"));
        Assert.Equal("Load video", VisitorText.Resolve(new Dictionary<string, string>(), "loadVideo"));
    }

    [Fact]
    public void Arguments_fill_the_numbered_markers()
    {
        Assert.Equal("Play: Mijn video", VisitorText.Get("playTitle", Culture("en"), "Mijn video"));
        Assert.Equal("Afspelen: Mijn video", VisitorText.Get("playTitle", Culture("nl"), "Mijn video"));
        Assert.Equal("Transcript (Nederlands)", VisitorText.Get("transcriptLang", Culture("en"), "Nederlands"));
    }

    [Fact]
    public void Every_key_the_renderer_and_the_scripts_ask_for_exists_in_english()
    {
        IReadOnlyDictionary<string, string> english = VisitorText.Table("en");
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "BlueBillywig.Umbraco", "Embedding", "BlueBillywigEmbedRenderer.cs"));
        string[] asked = Regex.Matches(source, @"\bV\(""([A-Za-z]+)""").Select(m => m.Groups[1].Value).Distinct().ToArray();

        Assert.True(asked.Length > 10);
        Assert.All(asked, key => Assert.True(english.ContainsKey(key), key));
        Assert.All(BlueBillywigEmbedRenderer.LoaderTextKeys.Concat(BlueBillywigEmbedRenderer.OverlayTextKeys), key => Assert.True(english.ContainsKey(key), key));
    }

    // The renderer ---------------------------------------------------------------------------------------

    private static async Task<string> RenderDenied(string culture)
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = Culture(culture);
        try
        {
            (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(consent: false);
            return await Rig.Render(renderer, new BlueBillywigVideo { Id = 42 });
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public async Task The_consent_placeholder_speaks_the_visitors_language()
    {
        string html = await RenderDenied("nl-NL");

        Assert.Contains(">Video laden</button>", html);
        Assert.Contains("aria-label=\"Video laden\"", html);
        Assert.Contains("Deze video bekijken verzendt gegevens naar Blue Billywig, onze videoleverancier.", html);
        Assert.DoesNotContain("Load video", html);
    }

    [Fact]
    public async Task The_consent_placeholder_is_english_for_a_language_without_a_file()
    {
        string html = await RenderDenied("ja-JP");

        Assert.Contains(">Load video</button>", html);
        Assert.Contains(">" + ConsentMessage.Default + "</p>", html);
    }

    [Fact]
    public async Task The_script_tags_carry_the_texts_the_scripts_show_on_their_own()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = Culture("fr-FR");
        try
        {
            (BlueBillywigEmbedRenderer renderer, _, _) = Rig.Create(consent: false);
            string html = await Rig.Render(renderer, new BlueBillywigVideo { Id = 42 });

            Match lazy = Regex.Match(html, @"<script src=""[^""]*bb-lazy\.js[^>]*data-bb-text=""([^""]*)""");
            Assert.True(lazy.Success, html);
            Dictionary<string, string>? loader = JsonSerializer.Deserialize<Dictionary<string, string>>(System.Net.WebUtility.HtmlDecode(lazy.Groups[1].Value));
            Assert.Equal(["videoUnavailable"], loader!.Keys);
            Assert.Equal("Vidéo indisponible — essayez de recharger la page.", loader["videoUnavailable"]);

            // The overlay script ships while the call to action feature is on (the default of the fake settings).
            Match overlay = Regex.Match(html, @"<script src=""[^""]*bb-cta-form-overlay\.js[^>]*data-bb-text=""([^""]*)""");
            if (overlay.Success)
            {
                Dictionary<string, string>? texts = JsonSerializer.Deserialize<Dictionary<string, string>>(System.Net.WebUtility.HtmlDecode(overlay.Groups[1].Value));
                Assert.Equal(BlueBillywigEmbedRenderer.OverlayTextKeys.Order(), texts!.Keys.Order());
                Assert.Equal("Envoyer", texts["ctaSubmit"]);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void The_gate_message_speaks_the_visitors_language_and_a_custom_message_is_never_translated()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = Culture("de-DE");
        try
        {
            // The gate message is HTML-encoded (WebUtility), so non-ASCII letters come out as character references.
            string anonymous = System.Net.WebUtility.HtmlDecode(GateMessage.Render(false, "/login", null));
            Assert.Contains("Für diesen Inhalt ist eine Berechtigung erforderlich", anonymous);
            Assert.Contains("Bitte melden Sie sich an, um dieses Video anzusehen.", anonymous);
            Assert.Contains(">Anmelden</a>", anonymous);

            Assert.Contains("Ihr Konto hat keine Berechtigung, diesen Inhalt anzusehen.", System.Net.WebUtility.HtmlDecode(GateMessage.Render(true, null, null)));
            Assert.Contains(">Only for members</div>", GateMessage.Render(false, null, "Only for members"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void The_gate_message_is_english_by_default()
    {
        string html = GateMessage.Render(false, "/login", null);

        Assert.Contains("This content requires access", html);
        Assert.Contains("Please log in to watch this video.", html);
        Assert.Contains(">Log in</a>", html);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "BlueBillywig.Umbraco.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root not found");
    }
}

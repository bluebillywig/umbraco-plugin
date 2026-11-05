using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Cta;

namespace BlueBillywig.Umbraco.Tests.Cta;

/// <summary>Property-style loops over generated configs with a fixed seed: save, reload, save must be byte-identical.</summary>
public class CtaStabilityTests
{
    private const int Seed = 20260504;
    private const int Iterations = 1500;

    private static readonly string[] Words =
    [
        "", "Go", "Learn more", "  padded  ", "0", "a\nb", "A\n\nB", "A\n\n\n\nB", "line1\r\nline2", "x\ry", " ", "<b>bold</b>", "\"quoted\" & 'single'", "</script><img src=x onerror=1>",
        "café ☃ \U0001F600", "tab\there", "back\\slash", "\u2028sep\u2029", "-", "bb-cta-tf-011", "bb-cta-form-config-abc", "Ünïcödé",
    ];

    private static readonly string[] Colours = ["", "FFFFFF", "#abc", "ff00FF80", "zzzzzz", "12", "123456789", "#", "0000ff"];

    private static readonly string[] Urls =
    [
        "", "https://example.com", "https://example.com/a?b=1&c=2#frag", "http://example.com:8080/x", "javascript:alert(1)", "ftp://example.com", "//example.com", "https://u:p@example.com", "https://example.com/a b", "not a url",
    ];

    private static readonly string[] ImageIds = ["", "0", "555", " 77 ", "123456789"];

    private static readonly double[] Numbers = [0, 1, -1, 0.1, 0.1 + 0.2, 3.2, 12.5, 33.333333333333336, 100, 1e-7, 1e21, 123456.789, double.NaN, double.PositiveInfinity, -0.0];

    private static string Pick(Random r, string[] pool) => pool[r.Next(pool.Length)];

    private static double PickNumber(Random r) => r.Next(3) == 0 ? Numbers[r.Next(Numbers.Length)] : Math.Round(r.NextDouble() * 120, r.Next(0, 6));

    internal static CtaConfig Generate(Random r) => new()
    {
        Title = Pick(r, Words),
        CtaType = r.Next(4) switch { 0 => "form", 1 => "banana", _ => "link" },
        Headline = Pick(r, Words),
        Body = Pick(r, Words),
        ButtonLabel = Pick(r, Words),
        Url = Pick(r, Urls),
        OpenInNewTab = r.Next(2) == 0,
        PauseOnShow = r.Next(2) == 0,
        FormEmailLabel = Pick(r, Words),
        FormSubmitLabel = Pick(r, Words),
        FormThankYou = Pick(r, Words),
        FormSubmitEndpoint = Pick(r, Urls),
        BackgroundColor = r.Next(3) == 0 ? null : Pick(r, Colours),
        HoverColor = r.Next(3) == 0 ? null : Pick(r, Colours),
        TextColor = Pick(r, Colours),
        FontFamily = Pick(r, Words),
        FontSize = PickNumber(r),
        CornerRadius = PickNumber(r),
        BorderThickness = r.Next(-3, 20),
        BorderColor = Pick(r, Colours),
        BackgroundAlpha = PickNumber(r),
        BackgroundImageId = r.Next(2) == 0 ? "" : Pick(r, ImageIds),
        HoverImageId = r.Next(2) == 0 ? "" : Pick(r, ImageIds),
        Xpos = PickNumber(r),
        Ypos = PickNumber(r),
        Width = PickNumber(r),
        Height = PickNumber(r),
        StartAt = PickNumber(r),
        EndAt = r.Next(3) == 0 ? 0 : PickNumber(r),
        ClipLength = r.Next(2) == 0 ? 0 : PickNumber(r),
    };

    [Fact]
    public void Save_reload_save_is_byte_identical_for_generated_configs()
    {
        var random = new Random(Seed);
        int image = 0, form = 0, withText = 0, withNav = 0;
        for (int i = 0; i < Iterations; i++)
        {
            CtaConfig cfg = Generate(random);
            var translator = new CtaTranslator(new SeqIds());

            JsonObject first = translator.ToTimeline(1000 + i, cfg);
            string firstText = CtaTranslator.Serialize(first);

            // Reload as SAPI would hand it back: text, then parsed. Then save again with it as the existing timeline.
            JsonNode stored = JsonNode.Parse(firstText)!;
            CtaConfig back = CtaTranslator.FromTimeline(stored) ?? throw new Xunit.Sdk.XunitException($"iteration {i}: reload returned null for {firstText}");
            string secondText = CtaTranslator.Serialize(new CtaTranslator(new SeqIds()).ToTimeline(1000 + i, back, stored));
            Assert.True(firstText == secondText, $"iteration {i} not byte-stable\nconfig: {cfg}\nfirst:  {firstText}\nsecond: {secondText}");

            // And a third time, from the second.
            JsonNode stored2 = JsonNode.Parse(secondText)!;
            CtaConfig back2 = CtaTranslator.FromTimeline(stored2)!;
            Assert.Equal(firstText, CtaTranslator.Serialize(new CtaTranslator(new SeqIds()).ToTimeline(1000 + i, back2, stored2)));

            image += cfg.BackgroundImageId is not ("" or "0") ? 1 : 0;
            form += cfg.CtaType == "form" ? 1 : 0;
            withText += CtaText.Combine(cfg).Length > 0 ? 1 : 0;
            withNav += firstText.Contains("\"navigation\"") ? 1 : 0;
        }

        // The loop must actually reach the interesting branches.
        Assert.True(image > Iterations / 5 && form > Iterations / 8 && withText > Iterations / 2 && withNav > Iterations / 10, $"image={image} form={form} text={withText} nav={withNav}");
    }

    [Fact]
    public void Random_ids_are_reused_so_a_second_save_with_the_real_generator_is_stable()
    {
        var random = new Random(Seed + 1);
        for (int i = 0; i < 200; i++)
        {
            CtaConfig cfg = Generate(random);
            var translator = new CtaTranslator(); // RandomCtaIdGenerator
            string firstText = CtaTranslator.Serialize(translator.ToTimeline(7, cfg));
            JsonNode stored = JsonNode.Parse(firstText)!;
            string secondText = CtaTranslator.Serialize(translator.ToTimeline(7, CtaTranslator.FromTimeline(stored)!, stored));
            Assert.Equal(firstText, secondText);
        }
    }

    [Fact]
    public void A_sapi_assigned_id_and_extra_fields_leave_the_rest_byte_identical()
    {
        var random = new Random(Seed + 2);
        for (int i = 0; i < 300; i++)
        {
            CtaConfig cfg = Generate(random);
            var translator = new CtaTranslator(new SeqIds());
            JsonObject first = translator.ToTimeline(5, cfg);

            JsonNode stored = JsonNode.Parse(CtaTranslator.Serialize(first))!;
            stored["id"] = 90000 + i;
            stored["createddate"] = "2026-01-01T00:00:00Z";
            stored["updateddate"] = "2026-01-02T00:00:00Z";

            JsonObject second = new CtaTranslator(new SeqIds()).ToTimeline(5, CtaTranslator.FromTimeline(stored)!, stored);
            Assert.Equal(90000 + i, (int)second["id"]!);

            // Same document as the first save plus the id at the end; nothing the editor did not change moved.
            first["id"] = 90000 + i;
            Assert.Equal(CtaTranslator.Serialize(first), CtaTranslator.Serialize(second));
            Assert.Equal((90000 + i).ToString(), CtaTranslator.FromTimeline(second)!.TimelineId);
        }
    }

    [Fact]
    public void Reload_returns_the_editor_values_the_save_wrote()
    {
        var random = new Random(Seed + 3);
        for (int i = 0; i < 500; i++)
        {
            CtaConfig cfg = Generate(random);
            CtaConfig back = CtaTranslator.FromTimeline(JsonNode.Parse(CtaTranslator.Serialize(new CtaTranslator(new SeqIds()).ToTimeline(1, cfg)))!)!;

            // Text slots: the 2.0.4 fix, for every generated mix of empty, padded, multi-line and Unicode values.
            Assert.Equal(CtaText.Slot(cfg.Headline), back.Headline);
            Assert.Equal(CtaText.Slot(cfg.Body), back.Body);
            Assert.Equal(CtaText.Slot(cfg.ButtonLabel), back.ButtonLabel);
            Assert.Equal(cfg.Title, back.Title);
            Assert.Equal(cfg.CtaType == "form" ? "form" : "link", back.CtaType);
            bool image = cfg.BackgroundImageId is not ("" or "0");
            Assert.Equal(image ? cfg.BackgroundImageId : "", back.BackgroundImageId);
            if (cfg.CtaType == "form")
            {
                Assert.Equal(cfg.FormEmailLabel, back.FormEmailLabel);
                Assert.Equal(cfg.FormSubmitLabel, back.FormSubmitLabel);
                Assert.Equal(cfg.FormThankYou, back.FormThankYou);
                Assert.Equal(CtaUrl.SanitizeEndpoint(cfg.FormSubmitEndpoint), back.FormSubmitEndpoint);
            }

            if (cfg.CtaType != "form" && CtaUrl.Sanitize(cfg.Url).Length > 0)
            {
                Assert.Equal(CtaUrl.Sanitize(cfg.Url), back.Url);
                Assert.Equal(cfg.OpenInNewTab, back.OpenInNewTab);
                Assert.Equal(cfg.PauseOnShow, back.PauseOnShow);
            }

            double Fin(double v, double d) => double.IsFinite(v) ? v : d;
            Assert.Equal(Fin(cfg.Xpos, 10), back.Xpos);
            Assert.Equal(Fin(cfg.Ypos, 70), back.Ypos);
            Assert.Equal(Fin(cfg.StartAt, 0), back.StartAt);
        }
    }
}

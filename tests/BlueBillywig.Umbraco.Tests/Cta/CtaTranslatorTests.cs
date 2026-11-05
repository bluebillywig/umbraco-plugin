using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Cta;

namespace BlueBillywig.Umbraco.Tests.Cta;

/// <summary>Ports of the plugin's CtaImageRoundTripTest and CtaTextSlotRoundTripTest, with the same inputs and expected values.</summary>
public class CtaTranslatorTests
{
    // CtaImageRoundTripTest ------------------------------------------------------------------------------

    [Fact]
    public void Image_cta_recovers_text_from_the_overlay_widget()
    {
        var cfg = new CtaConfig
        {
            Title = "Promo",
            CtaType = "link",
            Headline = "Big News",
            Body = "Read all about it",
            ButtonLabel = "Learn more",
            BackgroundImageId = "555",
            TextColor = "ff0000",
            FontFamily = "Arial",
        };
        JsonObject timeline = T.Translator().ToTimeline(101, cfg);

        // The text really is on the overlay, not on the image widget.
        Assert.False(T.Config0(timeline).ContainsKey("text"), "image widget must not carry the text");
        Assert.Equal("image", (string?)timeline["widgets"]![0]!["widgetdefinitiontype"]);
        Assert.Equal("textLabel", (string?)timeline["widgets"]![1]!["widgetdefinitiontype"]);

        CtaConfig back = CtaTranslator.FromTimeline(timeline)!;
        Assert.Equal("Big News", back.Headline);
        Assert.Equal("Read all about it", back.Body);
        Assert.Equal("Learn more", back.ButtonLabel);
        Assert.Equal("555", back.BackgroundImageId);
        Assert.Equal("ff0000", back.TextColor);
        Assert.Equal("Arial", back.FontFamily);
    }

    [Fact]
    public void Text_only_cta_still_round_trips_from_widget_zero()
    {
        var cfg = new CtaConfig { Title = "Promo", CtaType = "link", Headline = "Head", Body = "Body", ButtonLabel = "Go" };
        CtaConfig back = CtaTranslator.FromTimeline(T.Translator().ToTimeline(101, cfg))!;
        Assert.Equal("Head", back.Headline);
        Assert.Equal("Body", back.Body);
        Assert.Equal("Go", back.ButtonLabel);
        Assert.Equal("", back.BackgroundImageId);
    }

    // CtaTextSlotRoundTripTest ---------------------------------------------------------------------------

    public static TheoryData<string, string, string> Slots => new()
    {
        { "Head", "", "" },
        { "", "Body", "" },
        { "", "", "Go" },
        { "Head", "Body", "" },
        { "Head", "", "Go" },
        { "", "Body", "Go" },
        { "Head", "Body", "Go" },
        { "", "", "" },
    };

    [Theory]
    [MemberData(nameof(Slots))]
    public void Text_cta_round_trips_every_slot_combination(string headline, string body, string button)
    {
        var cfg = new CtaConfig { Title = "Promo", CtaType = "link", Headline = headline, Body = body, ButtonLabel = button };
        CtaConfig back = CtaTranslator.FromTimeline(T.Translator().ToTimeline(101, cfg))!;
        Assert.Equal(headline, back.Headline);
        Assert.Equal(body, back.Body);
        Assert.Equal(button, back.ButtonLabel);
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void Image_cta_round_trips_every_slot_combination(string headline, string body, string button)
    {
        var cfg = new CtaConfig { Title = "Promo", CtaType = "link", Headline = headline, Body = body, ButtonLabel = button, BackgroundImageId = "555" };
        CtaConfig back = CtaTranslator.FromTimeline(T.Translator().ToTimeline(101, cfg))!;
        Assert.Equal(headline, back.Headline);
        Assert.Equal(body, back.Body);
        Assert.Equal(button, back.ButtonLabel);
        Assert.Equal("555", back.BackgroundImageId);
    }

    [Fact]
    public void Headline_only_cta_does_not_migrate_to_the_button_label()
    {
        // The audit #4 symptom: the headline came back as the button label and re-saving kept the swap.
        CtaConfig back = CtaTranslator.FromTimeline(T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", Headline = "Just a headline" }))!;
        Assert.Equal("Just a headline", back.Headline);
        Assert.Equal("", back.ButtonLabel);
    }

    [Fact]
    public void Image_cta_without_a_button_label_still_stores_its_text()
    {
        // The write-side half: no button label used to mean no overlay widget, so the text never reached SAPI.
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", Headline = "Big News", Body = "Read all about it", BackgroundImageId = "555" });
        Assert.Equal(2, timeline["widgets"]!.AsArray().Count);
        Assert.Equal("textLabel", (string?)timeline["widgets"]![1]!["widgetdefinitiontype"]);
        string overlay = (string)T.Config(timeline, 1)["text"]!;
        Assert.Contains("Big News", overlay);
        Assert.Contains("Read all about it", overlay);
    }

    [Fact]
    public void Image_cta_with_no_text_at_all_emits_no_overlay()
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", BackgroundImageId = "555" });
        Assert.Single(timeline["widgets"]!.AsArray());
        Assert.Single(timeline["order"]!.AsArray());
    }

    [Fact]
    public void Mask_is_written_to_the_cssclass_and_does_not_collide_with_the_form_marker()
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", Headline = "Head", ButtonLabel = "Go" });
        string css = (string)T.Config0(timeline)["cssclass"]!;
        Assert.Equal("bb-cta-tf-101", css);
        Assert.DoesNotContain("bb-cta-form", css);
        Assert.Equal("link", CtaTranslator.FromTimeline(timeline)!.CtaType);
    }

    [Fact]
    public void Form_cta_keeps_its_config_class_alongside_the_mask()
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", CtaType = "form", Headline = "Subscribe", FormEmailLabel = "Your email" });
        string css = (string)T.Config0(timeline)["cssclass"]!;
        Assert.Contains("bb-cta-form ", css);
        Assert.Matches(@"bb-cta-form-config-[A-Za-z0-9_\-]+", css);
        Assert.Contains("bb-cta-tf-100", css);

        CtaConfig back = CtaTranslator.FromTimeline(timeline)!;
        Assert.Equal("form", back.CtaType);
        Assert.Equal("Your email", back.FormEmailLabel);
        Assert.Equal("Subscribe", back.Headline);
        Assert.Equal("", back.ButtonLabel);
    }

    [Fact]
    public void Mask_is_read_as_a_whole_class_not_a_substring()
    {
        // The base64url form payload precedes the marker and may contain '-', so a lookalike inside operator-supplied labels
        // must not win over the real trailing marker. 011 has the same bit count as the real 101, so it would pass the
        // parts-count guard and silently move the headline into the body.
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", Headline = "Head", ButtonLabel = "Go" });
        T.Config0(timeline)["cssclass"] = "bb-cta-form-config-Xbb-cta-tf-011Y bb-cta-tf-101";
        CtaConfig back = CtaTranslator.FromTimeline(timeline)!;
        Assert.Equal("Head", back.Headline);
        Assert.Equal("", back.Body);
        Assert.Equal("Go", back.ButtonLabel);
    }

    [Fact]
    public void Legacy_timeline_without_a_mask_keeps_the_historical_reading()
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", Headline = "Head", ButtonLabel = "Go" });
        T.Config0(timeline)["cssclass"] = "";
        CtaConfig back = CtaTranslator.FromTimeline(timeline)!;
        Assert.Equal("Head", back.Headline);
        Assert.Equal("", back.Body);
        Assert.Equal("Go", back.ButtonLabel);
    }

    [Fact]
    public void Externally_edited_text_falls_back_instead_of_misassigning()
    {
        // The mask says 2 slots but OVP6 left 3 paragraphs: trusting the mask would drop one. The bit count gates its use.
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", Headline = "Head", ButtonLabel = "Go" });
        T.Config0(timeline)["text"] = "One\n\nTwo\n\nThree";
        CtaConfig back = CtaTranslator.FromTimeline(timeline)!;
        Assert.Equal("One", back.Headline);
        Assert.Equal("Two", back.Body);
        Assert.Equal("Three", back.ButtonLabel);
    }

    [Fact]
    public void Save_reload_save_is_stable_for_a_headline_only_cta()
    {
        var cfg = new CtaConfig { Title = "Promo", Headline = "Just a headline" };
        var translator = T.Translator();
        JsonObject first = translator.ToTimeline(101, cfg);
        CtaConfig back = CtaTranslator.FromTimeline(first)!;
        JsonObject second = translator.ToTimeline(101, back, first);
        CtaConfig again = CtaTranslator.FromTimeline(second)!;
        Assert.Equal("Just a headline", again.Headline);
        Assert.Equal("", again.ButtonLabel);
        Assert.Equal((string?)T.Config0(first)["text"], (string?)T.Config0(second)["text"]);
    }

    // Shape ----------------------------------------------------------------------------------------------

    [Fact]
    public void Text_cta_has_the_versioned_model_two_shape_in_a_fixed_key_order()
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", ButtonLabel = "Go", Url = "https://example.com/x", OpenInNewTab = true, PauseOnShow = true, EndAt = 30 });
        Assert.Equal(
            ["type", "subtype", "modelVersion", "status", "parentEntityType", "parentEntityId", "interactions", "groupTags", "widgets", "order", "forms", "options", "label"],
            timeline.Select(p => p.Key).ToArray());
        Assert.Equal("TimelineNG", (string?)timeline["type"]);
        Assert.Equal("versioned", (string?)timeline["subtype"]);
        Assert.Equal("2", (string?)timeline["modelVersion"]);
        Assert.Equal("101", (string?)timeline["parentEntityId"]);
        Assert.Equal("bb-cta:Promo", (string?)timeline["label"]);
        Assert.Equal("bb-cta:Promo", (string?)timeline["widgets"]![0]!["label"]);
        Assert.Equal("textLabel", (string?)timeline["widgets"]![0]!["widgetdefinitiontype"]);
        Assert.Equal("10", (string?)timeline["widgets"]![0]!["widgetdefinitionid"]);
        Assert.Equal(1920, (int)timeline["options"]!["designWidth"]!);
        Assert.Equal(1080, (int)timeline["options"]!["designHeight"]!);
        Assert.Equal(30d, (double)timeline["order"]![0]!["inoutpoints"]![0]!["outpoint"]!);
    }

    [Fact]
    public void Empty_collections_are_arrays_not_objects_and_nulls_are_kept()
    {
        // PHP's array() encodes as [] and the player's parser expects that for the unused state configs.
        string json = CtaTranslator.Serialize(T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", ButtonLabel = "Go", EndAt = 10 }));
        Assert.Contains("\"name\":\"mousedown\",\"config\":[]", json);
        Assert.Contains("\"grouping\":[]", json);
        Assert.Contains("\"animations\":[]", json);
        Assert.Contains("\"interactions\":[]", json);
        Assert.Contains("\"onSuccess\":[]", json);
        Assert.Contains("\"onshow\":null", json);
        Assert.Contains("\"onclick\":null", json);
        Assert.Contains("\"targetIndex\":null", json);
        Assert.DoesNotContain("{}", json);
    }

    [Fact]
    public void Link_cta_writes_navigation_and_pause_actions_and_reads_them_back()
    {
        var cfg = new CtaConfig { Title = "Promo", ButtonLabel = "Go", Url = "https://example.com/offer?a=1&b=2", OpenInNewTab = true, PauseOnShow = true, EndAt = 10 };
        JsonObject timeline = T.Translator().ToTimeline(101, cfg);
        JsonArray onclick = T.Config0(timeline)["onclick"]!.AsArray();
        Assert.Equal(2, onclick.Count);
        Assert.Equal("navigation", (string?)onclick[0]!["actions"]![0]!["entity"]);
        Assert.Equal("_blank", (string?)onclick[0]!["actions"]![0]!["parameters"]![1]!["value"]);
        Assert.Equal("player", (string?)onclick[1]!["actions"]![0]!["entity"]);
        Assert.Equal("pause", (string?)onclick[1]!["actions"]![0]!["command"]);

        CtaConfig back = CtaTranslator.FromTimeline(T.Reload(timeline))!;
        Assert.Equal("https://example.com/offer?a=1&b=2", back.Url);
        Assert.True(back.OpenInNewTab);
        Assert.True(back.PauseOnShow);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("//evil.example/x")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://user:pw@example.com/x")]
    [InlineData("https://example.com/\"onmouseover=\"x")]
    [InlineData("https://example.com/a b")]
    [InlineData("")]
    public void Unsafe_urls_never_reach_a_navigation_action(string url)
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", ButtonLabel = "Go", Url = url, EndAt = 10 });
        Assert.Null(T.Config0(timeline)["onclick"]);
        Assert.DoesNotContain("navigation", CtaTranslator.Serialize(timeline));
    }

    [Fact]
    public void Form_endpoint_that_is_not_http_is_dropped_from_the_embedded_form_config()
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", CtaType = "form", FormSubmitEndpoint = "javascript:alert(1)", EndAt = 10 });
        Assert.Equal("", CtaTranslator.FromTimeline(timeline)!.FormSubmitEndpoint);
        JsonObject withEndpoint = T.Translator().ToTimeline(101, new CtaConfig { Title = "Promo", CtaType = "form", FormSubmitEndpoint = "https://hooks.example.com/in", EndAt = 10 });
        Assert.Equal("https://hooks.example.com/in", CtaTranslator.FromTimeline(withEndpoint)!.FormSubmitEndpoint);
    }

    [Fact]
    public void Colours_are_stripped_of_hash_and_fall_back_to_the_brand_colour()
    {
        JsonObject timeline = T.Translator().ToTimeline(101, new CtaConfig { Title = "P", ButtonLabel = "Go", BackgroundColor = "#AbCdEf", TextColor = "zzz", BorderColor = "#123", EndAt = 1 });
        JsonObject cfg = T.Config0(timeline);
        Assert.Equal("AbCdEf", (string?)cfg["backgroundcolor"]);
        Assert.Equal("002837", (string?)cfg["textFormat"]!["textFormat"]!["color"]);
        Assert.Equal("123", (string?)cfg["bordercolor"]);
        // Hover falls back to the background colour when unset.
        Assert.Equal("AbCdEf", (string?)timeline["widgets"]![0]!["statesets"]![0]!["states"]![1]!["config"]!["backgroundcolor"]);
    }

    // Deviations from the plugin that keep save, reload, save byte-stable --------------------------------

    [Fact]
    public void Hover_image_survives_a_reload()
    {
        // The plugin's reload dropped hoverImageId, so the next save lost the mouse-over image.
        var translator = T.Translator();
        JsonObject first = translator.ToTimeline(101, new CtaConfig { Title = "P", BackgroundImageId = "555", HoverImageId = "556", EndAt = 5 });
        CtaConfig back = CtaTranslator.FromTimeline(first)!;
        Assert.Equal("556", back.HoverImageId);
        Assert.Equal(CtaTranslator.Serialize(first), CtaTranslator.Serialize(translator.ToTimeline(101, back, first)));
    }

    [Fact]
    public void Padded_button_label_is_trimmed_for_the_aria_label_too()
    {
        // The plugin trimmed the text but not aria_label, so a label with spaces changed aria_label on the second save.
        var translator = T.Translator();
        JsonObject first = translator.ToTimeline(101, new CtaConfig { Title = "P", ButtonLabel = "  Go  ", EndAt = 5 });
        Assert.Equal("Go", (string?)T.Config0(first)["aria_label"]);
        Assert.Equal(CtaTranslator.Serialize(first), CtaTranslator.Serialize(translator.ToTimeline(101, CtaTranslator.FromTimeline(first)!, first)));
    }

    [Fact]
    public void Blank_line_inside_a_slot_is_folded_so_the_slot_count_survives()
    {
        // "A\n\nB" inside a field is the blob's own slot separator; the plugin changed the slot count and the mask on the next save.
        var translator = T.Translator();
        JsonObject first = translator.ToTimeline(101, new CtaConfig { Title = "P", Headline = "A\n\nB", ButtonLabel = "Go", EndAt = 5 });
        CtaConfig back = CtaTranslator.FromTimeline(first)!;
        Assert.Equal("A\nB", back.Headline);
        Assert.Equal("", back.Body);
        Assert.Equal("Go", back.ButtonLabel);
        Assert.Equal(CtaTranslator.Serialize(first), CtaTranslator.Serialize(translator.ToTimeline(101, back, first)));
    }

    // Reuse of ids ---------------------------------------------------------------------------------------

    [Fact]
    public void Existing_ids_and_the_timeline_id_are_reused()
    {
        var cfg = new CtaConfig { Title = "P", BackgroundImageId = "5", Headline = "H", ButtonLabel = "Go", Url = "https://example.com", PauseOnShow = true, EndAt = 5 };
        var translator = new CtaTranslator(new SeqIds());
        JsonObject first = translator.ToTimeline(101, cfg);
        JsonNode stored = T.Reload(first);
        stored["id"] = 4242;
        stored["updateddate"] = "2026-01-01T00:00:00Z";

        // A translator whose ids would all differ proves nothing is regenerated.
        JsonObject second = new CtaTranslator(new SeqIds()).ToTimeline(101, cfg with { Headline = "Changed" }, stored);
        Assert.Equal(4242, (int)second["id"]!);
        Assert.Equal((string?)first["widgets"]![0]!["id"], (string?)second["widgets"]![0]!["id"]);
        Assert.Equal((string?)first["widgets"]![1]!["id"], (string?)second["widgets"]![1]!["id"]);
        Assert.Equal((string?)first["order"]![1]!["id"], (string?)second["order"]![1]!["id"]);
        Assert.Equal((string?)first["order"]![0]!["inoutpoints"]![0]!["id"], (string?)second["order"]![0]!["inoutpoints"]![0]!["id"]);
        Assert.Equal(T.Config0(first)["onclick"]!.ToJsonString(), T.Config0(second)["onclick"]!.ToJsonString());
        Assert.False(second.ContainsKey("updateddate"));
    }

    [Fact]
    public void An_empty_or_non_string_existing_id_is_not_reused()
    {
        JsonNode existing = JsonNode.Parse("""{"id":"","widgets":[{"id":""},{"id":5}],"order":[{"id":null}]}""")!;
        JsonObject timeline = T.Translator().ToTimeline(1, new CtaConfig { Title = "P", ButtonLabel = "Go", EndAt = 1 }, existing);
        Assert.False(timeline.ContainsKey("id"));
        Assert.Equal("w1", (string?)timeline["widgets"]![0]!["id"]);
    }

    [Fact]
    public void Random_ids_are_prefixed_hex()
    {
        var ids = new RandomCtaIdGenerator();
        string a = ids.Next("w");
        Assert.Matches("^w[0-9a-f]{16}$", a);
        Assert.NotEqual(a, ids.Next("w"));
    }

    [Fact]
    public void Round_trip_is_culture_independent()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");
            var cfg = new CtaConfig { Title = "P", ButtonLabel = "Go", Xpos = 12.5, FontSize = 3.25, StartAt = 1.5, EndAt = 9.75, BorderThickness = 3 };
            var translator = T.Translator();
            JsonObject first = translator.ToTimeline(101, cfg);
            string json = CtaTranslator.Serialize(first);
            Assert.Contains("\"xpos\":\"12.5%\"", json);
            Assert.Contains("\"fontsize\":\"3.25%\"", json);
            Assert.DoesNotContain("12,5", json);
            CtaConfig back = CtaTranslator.FromTimeline(T.Reload(first))!;
            Assert.Equal(12.5, back.Xpos);
            Assert.Equal(3.25, back.FontSize);
            Assert.Equal(json, CtaTranslator.Serialize(translator.ToTimeline(101, back, T.Reload(first))));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void From_timeline_returns_null_for_a_shape_it_does_not_know()
    {
        Assert.Null(CtaTranslator.FromTimeline(null));
        Assert.Null(CtaTranslator.FromTimeline(JsonNode.Parse("{}")));
        Assert.Null(CtaTranslator.FromTimeline(JsonNode.Parse("""{"widgets":[{"statesets":[{"states":[{"name":"default","config":[]}]}]}]}""")));
    }

    [Fact]
    public void Numbers_read_back_from_strings_with_a_percent_sign_or_a_suffix()
    {
        JsonNode timeline = JsonNode.Parse("""{"widgets":[{"label":"x","statesets":[{"states":[{"name":"default","config":{"xpos":"12.5%","ypos":"7%%","width":40,"height":"abc","text":"Go"}}]}]}]}""")!;
        CtaConfig back = CtaTranslator.FromTimeline(timeline)!;
        Assert.Equal(12.5, back.Xpos);
        Assert.Equal(7, back.Ypos);
        Assert.Equal(40, back.Width);
        Assert.Equal(0, back.Height);
        Assert.Equal("Go", back.ButtonLabel);
    }
}

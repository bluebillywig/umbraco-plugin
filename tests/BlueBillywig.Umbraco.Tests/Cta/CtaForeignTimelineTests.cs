using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Cta;

namespace BlueBillywig.Umbraco.Tests.Cta;

/// <summary>C3: a timeline this package did not author is never overwritten or deleted; C7 with the lookup failures.</summary>
public class CtaForeignTimelineTests
{
    private static readonly CtaConfig Valid = new() { Title = "Promo", ButtonLabel = "Go", Url = "https://example.com/offer", EndAt = 30 };

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static JsonNode Interactive() => Parse("""
        {"id":7,"label":"Quiz","widgets":[
          {"id":"w1","label":"Question","widgetdefinitiontype":"choice","statesets":[]},
          {"id":"w2","label":"Next","widgetdefinitiontype":"button","statesets":[]},
          {"id":"w3","label":"Result","widgetdefinitiontype":"textLabel","statesets":[]}]}
        """);

    [Fact]
    public void Absent_or_empty_timelines_may_be_written()
    {
        Assert.True(CtaTranslator.IsAuthoredByUs(null));
        Assert.True(CtaTranslator.IsAuthoredByUs(Parse("{}")));
        Assert.True(CtaTranslator.IsAuthoredByUs(Parse("[]")));
    }

    [Fact]
    public void A_widget_label_or_timeline_label_with_the_marker_is_ours()
    {
        Assert.True(CtaTranslator.IsAuthoredByUs(Parse("""{"widgets":[{"label":"bb-cta:Promo"},{"widgetdefinitiontype":"video"},{"widgetdefinitiontype":"video"}]}""")));
        // The user edited the widget in OVP6, which renames it; the timeline label still carries the marker.
        Assert.True(CtaTranslator.IsAuthoredByUs(Parse("""{"label":"bb-cta:Promo","widgets":[{"label":"Renamed","widgetdefinitiontype":"video"},{"widgetdefinitiontype":"video"},{"widgetdefinitiontype":"video"}]}""")));
    }

    [Fact]
    public void One_or_two_text_or_image_widgets_without_a_marker_are_treated_as_ours_like_the_plugin()
    {
        Assert.True(CtaTranslator.IsAuthoredByUs(Parse("""{"widgets":[{"widgetdefinitiontype":"textLabel"}]}""")));
        Assert.True(CtaTranslator.IsAuthoredByUs(Parse("""{"widgets":[{"widgetdefinitiontype":"image"},{"widgetdefinitiontype":"textLabel"}]}""")));
    }

    [Fact]
    public void Interactive_video_timelines_are_foreign()
    {
        Assert.False(CtaTranslator.IsAuthoredByUs(Interactive()));
        Assert.False(CtaTranslator.IsAuthoredByUs(Parse("""{"widgets":[{"widgetdefinitiontype":"textLabel"},{"widgetdefinitiontype":"choice"}]}""")));
        Assert.False(CtaTranslator.IsAuthoredByUs(Parse("""{"widgets":[{"widgetdefinitiontype":"textLabel"},{"widgetdefinitiontype":"image"},{"widgetdefinitiontype":"image"}]}""")));
        Assert.False(CtaTranslator.IsAuthoredByUs(Parse("""{"id":3,"widgets":[]}""")));
        Assert.False(CtaTranslator.IsAuthoredByUs(Parse("""{"id":3,"label":"Something else","widgets":[{"label":"x"}]}""")));
        Assert.False(CtaTranslator.IsAuthoredByUs(Parse("""{"id":3,"label":"BB-CTA:upper","widgets":[]}""")));
        Assert.False(CtaTranslator.IsAuthoredByUs(Parse("""{"id":3,"label":12,"widgets":"nope"}""")));
    }

    [Fact]
    public void Save_over_a_foreign_timeline_is_refused_and_builds_no_payload()
    {
        CtaSavePlan plan = CtaSavePlanner.PlanSave(5, Valid, TimelineLookup.Found((JsonObject)Interactive()), T.Translator());
        Assert.Equal(CtaSaveAction.RefuseForeign, plan.Action);
        Assert.False(plan.MayWrite);
        Assert.Null(plan.Payload);
    }

    [Fact]
    public void Delete_of_a_foreign_timeline_is_refused()
    {
        Assert.Equal(CtaDeleteAction.RefuseForeign, CtaSavePlanner.PlanDelete(TimelineLookup.Found((JsonObject)Interactive())).Action);
    }

    [Fact]
    public void Save_over_our_own_timeline_updates_it_with_its_ids()
    {
        JsonObject ours = T.Translator().ToTimeline(5, Valid);
        ours["id"] = 12;
        CtaSavePlan plan = CtaSavePlanner.PlanSave(5, Valid with { Title = "Renamed" }, TimelineLookup.Found(ours), T.Translator());
        Assert.Equal(CtaSaveAction.Update, plan.Action);
        Assert.Equal(12, (int)plan.Payload!["id"]!);
        Assert.Equal((string?)ours["widgets"]![0]!["id"], (string?)plan.Payload["widgets"]![0]!["id"]);
        Assert.Equal("bb-cta:Renamed", (string?)plan.Payload["label"]);
    }

    [Fact]
    public void Save_for_a_clip_without_a_timeline_creates_one()
    {
        CtaSavePlan plan = CtaSavePlanner.PlanSave(5, Valid, TimelineLookup.None, T.Translator());
        Assert.Equal(CtaSaveAction.Create, plan.Action);
        Assert.False(plan.Payload!.ContainsKey("id"));
    }

    [Fact]
    public void A_failed_lookup_never_becomes_a_create()
    {
        CtaSavePlan plan = CtaSavePlanner.PlanSave(5, Valid, TimelineLookup.Failed("boom"), T.Translator());
        Assert.Equal(CtaSaveAction.RefuseLookupFailed, plan.Action);
        Assert.Null(plan.Payload);
        Assert.Equal(CtaDeleteAction.RefuseLookupFailed, CtaSavePlanner.PlanDelete(TimelineLookup.Failed("boom")).Action);
    }

    [Fact]
    public void Invalid_input_is_refused_before_the_lookup_matters()
    {
        CtaSavePlan plan = CtaSavePlanner.PlanSave(5, Valid with { Url = "javascript:alert(1)" }, TimelineLookup.None, T.Translator());
        Assert.Equal(CtaSaveAction.RefuseInvalid, plan.Action);
        Assert.Contains("URL must be a valid http(s) URL", plan.Errors!);
    }

    [Fact]
    public void Delete_plans_follow_what_exists()
    {
        Assert.Equal(CtaDeleteAction.NothingToDelete, CtaSavePlanner.PlanDelete(TimelineLookup.None).Action);
        JsonObject ours = T.Translator().ToTimeline(5, Valid);
        Assert.Equal(CtaDeleteAction.NothingToDelete, CtaSavePlanner.PlanDelete(TimelineLookup.Found(ours)).Action);
        ours["id"] = 12;
        CtaDeletePlan plan = CtaSavePlanner.PlanDelete(TimelineLookup.Found(ours));
        Assert.Equal(CtaDeleteAction.Delete, plan.Action);
        Assert.Equal("12", plan.TimelineId);
    }

    // Validator (the plugin's save_cta rules) ------------------------------------------------------------

    [Fact]
    public void Validator_ports_the_save_cta_rules()
    {
        Assert.Empty(CtaValidator.Validate(Valid));
        Assert.Contains("URL is required", CtaValidator.Validate(Valid with { Url = "" }));
        Assert.Contains("Button label or background image is required", CtaValidator.Validate(Valid with { ButtonLabel = "" }));
        Assert.DoesNotContain("Button label or background image is required", CtaValidator.Validate(Valid with { ButtonLabel = "", BackgroundImageId = "5" }));
        Assert.Contains("Background image ID must be numeric", CtaValidator.Validate(Valid with { BackgroundImageId = "5a" }));
        Assert.Contains("Clip duration not available yet", CtaValidator.Validate(Valid with { EndAt = 0, ClipLength = 0 }));
        Assert.Empty(CtaValidator.Validate(Valid with { EndAt = 0, ClipLength = 20 }));
        Assert.Contains(CtaValidator.Validate(Valid with { BackgroundColor = "nothex" }), e => e.StartsWith("backgroundColor:"));

        var form = Valid with { CtaType = "form", Url = "", ButtonLabel = "" };
        Assert.Empty(CtaValidator.Validate(form));
        Assert.Contains("Submit label is required for form CTAs", CtaValidator.Validate(form with { FormSubmitLabel = "" }));
        Assert.Contains("Submit endpoint must be an https URL (http works only for localhost)", CtaValidator.Validate(form with { FormSubmitEndpoint = "file:///etc/passwd" }));
    }
}

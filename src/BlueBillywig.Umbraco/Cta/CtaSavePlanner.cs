using System.Text.Json.Nodes;

namespace BlueBillywig.Umbraco.Cta;

public enum CtaSaveAction
{
    Create,
    Update,

    /// <summary>Editor input failed <see cref="CtaValidator"/>. Nothing may be sent.</summary>
    RefuseInvalid,

    /// <summary>The clip's timeline was not authored by us (HTTP 409 <c>foreign_timeline</c> in the plugin).</summary>
    RefuseForeign,

    /// <summary>The timeline lookup failed (HTTP 502 in the plugin). Creating would risk a duplicate.</summary>
    RefuseLookupFailed,
}

public sealed record CtaSavePlan(CtaSaveAction Action, JsonObject? Payload = null, IReadOnlyList<string>? Errors = null)
{
    public bool MayWrite => Action is CtaSaveAction.Create or CtaSaveAction.Update;
}

public enum CtaDeleteAction
{
    /// <summary>No timeline to delete; success with <c>deleted: false</c>.</summary>
    NothingToDelete,

    Delete,

    RefuseForeign,

    RefuseLookupFailed,
}

public sealed record CtaDeletePlan(CtaDeleteAction Action, string? TimelineId = null);

/// <summary>
/// Decides what a CTA save or delete may do, from the editor input and the clip's current timeline. Pure: the wiring
/// performs the SAPI call only when <see cref="CtaSavePlan.MayWrite"/> or <see cref="CtaDeleteAction.Delete"/> says so,
/// and answers 409 and 502 for the two refusals.
/// </summary>
public static class CtaSavePlanner
{
    public static CtaSavePlan PlanSave(long clipId, CtaConfig cfg, TimelineLookup lookup, CtaTranslator translator)
    {
        IReadOnlyList<string> errors = CtaValidator.Validate(cfg);
        if (errors.Count > 0)
        {
            return new CtaSavePlan(CtaSaveAction.RefuseInvalid, null, errors);
        }

        switch (lookup.Kind)
        {
            case TimelineLookupKind.Failed:
                return new CtaSavePlan(CtaSaveAction.RefuseLookupFailed, null, [lookup.Error ?? "timeline lookup failed"]);
            case TimelineLookupKind.Found when !CtaTranslator.IsAuthoredByUs(lookup.Timeline):
                return new CtaSavePlan(CtaSaveAction.RefuseForeign);
            case TimelineLookupKind.Found:
                return new CtaSavePlan(CtaSaveAction.Update, translator.ToTimeline(clipId, cfg, lookup.Timeline));
            default:
                return new CtaSavePlan(CtaSaveAction.Create, translator.ToTimeline(clipId, cfg));
        }
    }

    public static CtaDeletePlan PlanDelete(TimelineLookup lookup)
    {
        switch (lookup.Kind)
        {
            case TimelineLookupKind.Failed:
                return new CtaDeletePlan(CtaDeleteAction.RefuseLookupFailed);
            case TimelineLookupKind.Found when Sapi.SapiJson.PhpEmpty(lookup.Timeline?["id"]):
                return new CtaDeletePlan(CtaDeleteAction.NothingToDelete);
            case TimelineLookupKind.Found when !CtaTranslator.IsAuthoredByUs(lookup.Timeline):
                return new CtaDeletePlan(CtaDeleteAction.RefuseForeign);
            case TimelineLookupKind.Found:
                return new CtaDeletePlan(CtaDeleteAction.Delete, Sapi.SapiJson.Str(lookup.Timeline!["id"]));
            default:
                return new CtaDeletePlan(CtaDeleteAction.NothingToDelete);
        }
    }
}

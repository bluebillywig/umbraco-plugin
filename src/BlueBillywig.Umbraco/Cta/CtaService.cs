using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BlueBillywig.Umbraco.Cta;

public interface ICtaService
{
    Task<EditOutcome<CtaStateResponseModel>> GetAsync(long clipId, CancellationToken ct);

    Task<EditOutcome<CtaStateResponseModel>> SaveAsync(long clipId, CtaConfig config, CancellationToken ct);

    Task<EditOutcome<CtaDeleteResponseModel>> DeleteAsync(long clipId, CancellationToken ct);

    /// <summary>Creates the image as a creative (<c>/sapi/commonclip</c>) and returns its id.</summary>
    Task<EditOutcome<CtaImageUploadResponseModel>> UploadImageAsync(string? dataUri, CancellationToken ct);

    /// <summary>The save rules without saving, so the editor can show their messages as the editor types.</summary>
    EditOutcome<CtaValidateResponseModel> Validate(CtaConfig config);
}

/// <summary>
/// Ports the reference plugin's <c>get_cta</c>, <c>save_cta</c>, <c>delete_cta</c> and <c>upload_cta_image</c>. The decisions
/// are made by <see cref="CtaValidator"/>, <see cref="CtaTimelineLocator"/> and <see cref="CtaSavePlanner"/>; this class only
/// performs the SAPI calls they allow. Nothing is written when the plan refuses, a lookup that failed is never read as
/// "no timeline" (so a save cannot create a duplicate), a foreign timeline is refused with 409 and left untouched, and a 2xx
/// answer that carries no id counts as a failure. When the settings turn the call-to-action feature off every method answers 404.
/// </summary>
internal sealed class CtaService : ICtaService
{
    /// <summary>Longest text value accepted from the editor. The player cuts nothing; SAPI should not be handed megabytes.</summary>
    private const int MaxText = 5000;

    private static readonly SapiResult NotAccepted = SapiResult.Failure(502, "Blue Billywig did not accept the change");

    // One save or delete per clip at a time inside this process, so two quick saves cannot both see "no timeline" and
    // each create one (the duplicate guard, C7, covers the lookup; this covers the race).
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    private readonly IBlueBillywigClient _client;
    private readonly IBlueBillywigSettingsStore _settings;
    private readonly IThumbnailState _thumbnails;
    private readonly ICtaIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly ILogger<CtaService> _logger;

    public CtaService(IBlueBillywigClient client, IBlueBillywigSettingsStore settings, IThumbnailState thumbnails, ICtaIdGenerator ids, TimeProvider time, ILogger<CtaService> logger)
    {
        _client = client;
        _settings = settings;
        _thumbnails = thumbnails;
        _ids = ids;
        _time = time;
        _logger = logger;
    }

    private bool Enabled => _settings.Get().EnableCta;

    private static EditOutcome<T> Disabled<T>() where T : class => EditOutcome<T>.Reject(StatusCodes.Status404NotFound, "Call to action is turned off in the settings");

    private CtaTimelineLocator Locator() => new(_client, _logger);

    public async Task<EditOutcome<CtaStateResponseModel>> GetAsync(long clipId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return Disabled<CtaStateResponseModel>();
        }

        (double length, SapiResult? failure) = await ReadClipLengthAsync(clipId, ct);
        if (failure is not null)
        {
            return EditOutcome<CtaStateResponseModel>.Fail(failure);
        }

        TimelineLookup lookup = await Locator().FindAsync(clipId, ct);
        switch (lookup.Kind)
        {
            case TimelineLookupKind.Failed:
                return EditOutcome<CtaStateResponseModel>.Fail(SapiResult.Failure(502, lookup.Error ?? "Could not read the timeline"));
            case TimelineLookupKind.Found when !CtaTranslator.IsAuthoredByUs(lookup.Timeline):
                return EditOutcome<CtaStateResponseModel>.Of(new CtaStateResponseModel
                {
                    Cta = null,
                    TimelineId = "",
                    ClipLength = length,
                    ForeignTimeline = true,
                    ForeignTimelineId = SapiJson.Str(lookup.Timeline?["id"]),
                });
            case TimelineLookupKind.Found:
                return EditOutcome<CtaStateResponseModel>.Of(State(lookup.Timeline, length));
            default:
                return EditOutcome<CtaStateResponseModel>.Of(State(null, length));
        }
    }

    public async Task<EditOutcome<CtaStateResponseModel>> SaveAsync(long clipId, CtaConfig config, CancellationToken ct)
    {
        if (!Enabled)
        {
            return Disabled<CtaStateResponseModel>();
        }

        if (clipId <= 0)
        {
            return EditOutcome<CtaStateResponseModel>.Reject(StatusCodes.Status400BadRequest, "clip id required");
        }

        if (TooLong(config) is { } tooLong)
        {
            return EditOutcome<CtaStateResponseModel>.Reject(StatusCodes.Status400BadRequest, tooLong, [tooLong]);
        }

        SemaphoreSlim gate = _locks.GetOrAdd(clipId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // The clip must exist in this publication: a timeline for a bad id would be an orphan. Its length is read
            // here and not trusted from the browser, because an "until end" outpoint of 0 would hide the call to action.
            (double length, SapiResult? failure) = await ReadClipLengthAsync(clipId, ct);
            if (failure is not null)
            {
                return EditOutcome<CtaStateResponseModel>.Fail(failure);
            }

            CtaConfig cfg = config with { ClipLength = length };
            IReadOnlyList<string> invalid = CtaValidator.Validate(cfg);
            if (invalid.Count > 0)
            {
                return EditOutcome<CtaStateResponseModel>.Reject(StatusCodes.Status400BadRequest, string.Join("; ", invalid), invalid);
            }

            TimelineLookup lookup = await Locator().FindAsync(clipId, ct);
            CtaSavePlan plan = CtaSavePlanner.PlanSave(clipId, cfg, lookup, new CtaTranslator(_ids));
            switch (plan.Action)
            {
                case CtaSaveAction.RefuseInvalid:
                    return EditOutcome<CtaStateResponseModel>.Reject(StatusCodes.Status400BadRequest, string.Join("; ", plan.Errors ?? []), plan.Errors);
                case CtaSaveAction.RefuseLookupFailed:
                    return EditOutcome<CtaStateResponseModel>.Fail(SapiResult.Failure(502, plan.Errors?.FirstOrDefault() ?? "Could not read the timeline"));
                case CtaSaveAction.RefuseForeign:
                    return EditOutcome<CtaStateResponseModel>.Reject(StatusCodes.Status409Conflict, "This clip already has an interactive timeline created by another tool. Remove it there first, or edit the call to action in that tool.");
            }

            JsonObject payload = plan.Payload!;
            SapiResult write = plan.Action == CtaSaveAction.Update
                ? await _client.PutAsync($"/sapi/timeline/{BlueBillywigUrls.Segment(SapiJson.Str(payload["id"]))}", payload, ct)
                : await _client.PostAsync("/sapi/timeline", payload, ct);
            if (!write.Ok)
            {
                _logger.LogWarning("Blue Billywig CTA save failed: clip={Clip}", clipId);
                return EditOutcome<CtaStateResponseModel>.Fail(write);
            }

            // A 2xx that is not the saved entity is not a save.
            if (write.Json is not JsonObject saved || SapiJson.PhpEmpty(saved["id"]))
            {
                _logger.LogWarning("Blue Billywig CTA save: clip={Clip} SAPI answered without a timeline id", clipId);
                return EditOutcome<CtaStateResponseModel>.Fail(NotAccepted);
            }

            _logger.LogInformation("Blue Billywig CTA saved: clip={Clip} timeline={Timeline}", clipId, SapiJson.Str(saved["id"]));
            Changed(clipId);

            // Show what SAPI returned. If it did not echo the widgets, show what was sent, with the id it was given.
            JsonObject shown = saved["widgets"] is JsonArray ? saved : WithId(payload, saved["id"]);
            return EditOutcome<CtaStateResponseModel>.Of(State(shown, length));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EditOutcome<CtaDeleteResponseModel>> DeleteAsync(long clipId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return Disabled<CtaDeleteResponseModel>();
        }

        if (clipId <= 0)
        {
            return EditOutcome<CtaDeleteResponseModel>.Reject(StatusCodes.Status400BadRequest, "clip id required");
        }

        SemaphoreSlim gate = _locks.GetOrAdd(clipId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            CtaDeletePlan plan = CtaSavePlanner.PlanDelete(await Locator().FindAsync(clipId, ct));
            switch (plan.Action)
            {
                case CtaDeleteAction.NothingToDelete:
                    return EditOutcome<CtaDeleteResponseModel>.Of(new CtaDeleteResponseModel { Deleted = false });
                case CtaDeleteAction.RefuseLookupFailed:
                    return EditOutcome<CtaDeleteResponseModel>.Fail(SapiResult.Failure(502, "Could not read the timeline"));
                case CtaDeleteAction.RefuseForeign:
                    return EditOutcome<CtaDeleteResponseModel>.Reject(StatusCodes.Status409Conflict, "Refusing to delete a timeline created by another tool.");
            }

            SapiResult result = await _client.DeleteAsync($"/sapi/timeline/{BlueBillywigUrls.Segment(plan.TimelineId!)}", ct);
            if (!result.Ok)
            {
                _logger.LogWarning("Blue Billywig CTA delete failed: clip={Clip} timeline={Timeline}", clipId, plan.TimelineId);
                return EditOutcome<CtaDeleteResponseModel>.Fail(result);
            }

            _logger.LogInformation("Blue Billywig CTA deleted: clip={Clip} timeline={Timeline}", clipId, plan.TimelineId);
            Changed(clipId);
            return EditOutcome<CtaDeleteResponseModel>.Of(new CtaDeleteResponseModel { Deleted = true });
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EditOutcome<CtaImageUploadResponseModel>> UploadImageAsync(string? dataUri, CancellationToken ct)
    {
        if (!Enabled)
        {
            return Disabled<CtaImageUploadResponseModel>();
        }

        if (CtaImageValidator.Validate(dataUri) is { } invalid)
        {
            return EditOutcome<CtaImageUploadResponseModel>.Reject(StatusCodes.Status400BadRequest, invalid);
        }

        // The plugin's creative: a published image commonclip whose one thumbnail is the picture. Deviation: usetype is
        // "commercial", the value SAPI knows for a creative. The plugin sends "creative", which the dev OVP does not know:
        // it stored an editorial clip that /sapi/creative cannot find (observed on bb.dev), and a widget's imageid has to
        // name a creative.
        var payload = new JsonObject
        {
            ["title"] = "CTA background " + _time.GetUtcNow().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["usetype"] = "commercial",
            ["mediatype"] = "image",
            ["status"] = "published",
            ["thumbnails"] = new JsonArray
            {
                new JsonObject
                {
                    ["data"] = dataUri,
                    ["main"] = true,
                    ["crops"] = new JsonObject
                    {
                        ["landscape"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1, ["main"] = true },
                    },
                },
            },
        };

        SapiResult result = await _client.PostAsync("/sapi/commonclip", payload, ct);
        if (!result.Ok)
        {
            return EditOutcome<CtaImageUploadResponseModel>.Fail(result);
        }

        // The id goes into a widget and a URL later: it has to be a plain number.
        string id = SapiJson.Str(result.Json?["id"]);
        if (id.Length == 0 || id == "0" || !id.All(char.IsAsciiDigit))
        {
            return EditOutcome<CtaImageUploadResponseModel>.Fail(NotAccepted);
        }

        _logger.LogInformation("Blue Billywig CTA image created: creative={Creative}", id);
        return EditOutcome<CtaImageUploadResponseModel>.Of(new CtaImageUploadResponseModel { ImageId = id });
    }

    public EditOutcome<CtaValidateResponseModel> Validate(CtaConfig config)
    {
        if (!Enabled)
        {
            return Disabled<CtaValidateResponseModel>();
        }

        IReadOnlyList<string> errors = TooLong(config) is { } tooLong ? [tooLong] : CtaValidator.Validate(config);
        return EditOutcome<CtaValidateResponseModel>.Of(new CtaValidateResponseModel { Errors = errors });
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private async Task<(double Length, SapiResult? Failure)> ReadClipLengthAsync(long clipId, CancellationToken ct)
    {
        if (clipId <= 0)
        {
            return (0, SapiResult.Failure(404, "Clip not found"));
        }

        SapiResult clip = await _client.GetAsync($"/sapi/mediaclip/{clipId}", ct: ct);
        if (!clip.Ok)
        {
            return (0, clip);
        }

        // Without an id this is not a clip (a PUT to an unknown id would make SAPI create an empty one; a timeline for it is an orphan).
        if (clip.Json is not JsonObject json || SapiJson.PhpEmpty(json["id"]))
        {
            return (0, SapiResult.Failure(404, "Clip not found"));
        }

        return (Math.Max(0, SapiJson.Num(json["length"], 0)), null);
    }

    private static CtaStateResponseModel State(JsonNode? timeline, double length) => new()
    {
        Cta = timeline is null ? null : CtaTranslator.FromTimeline(timeline),
        TimelineId = timeline is null ? "" : SapiJson.Str(timeline["id"]),
        ClipLength = length,
        ForeignTimeline = false,
        ForeignTimelineId = "",
    };

    private static JsonObject WithId(JsonObject payload, JsonNode? id)
    {
        var copy = (JsonObject)payload.DeepClone();
        copy["id"] = id?.DeepClone();
        return copy;
    }

    /// <summary>Drops what still shows the clip as it was before this change.</summary>
    private void Changed(long clipId)
    {
        // The embed renderer reads the clip (does it have timelines) through the cache, and a browser may hold the
        // player data (which carries the timelines) for minutes; both would show the old state.
        _client.ClearCache();
        _thumbnails.Touch(clipId);
    }

    private static string? TooLong(CtaConfig c)
    {
        foreach ((string name, string? value) in new[]
        {
            ("title", c.Title), ("headline", c.Headline), ("body", c.Body), ("buttonLabel", c.ButtonLabel), ("formEmailLabel", c.FormEmailLabel),
            ("formSubmitLabel", c.FormSubmitLabel), ("formThankYou", c.FormThankYou), ("fontFamily", c.FontFamily), ("backgroundColor", c.BackgroundColor),
            ("hoverColor", c.HoverColor), ("textColor", c.TextColor), ("borderColor", c.BorderColor), ("backgroundImageId", c.BackgroundImageId), ("hoverImageId", c.HoverImageId),
        })
        {
            if (value is { Length: > MaxText })
            {
                return $"{name} is too long (max {MaxText} characters)";
            }
        }

        foreach ((string name, string value) in new[] { ("url", c.Url), ("formSubmitEndpoint", c.FormSubmitEndpoint) })
        {
            if (value.Length > CtaUrl.MaxLength)
            {
                return $"{name} is too long (max {CtaUrl.MaxLength} characters)";
            }
        }

        return null;
    }
}

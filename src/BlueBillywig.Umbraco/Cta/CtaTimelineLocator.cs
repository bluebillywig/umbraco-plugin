using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Cta;

public enum TimelineLookupKind
{
    /// <summary>The clip has no (live) timeline.</summary>
    None,

    /// <summary>A timeline was found.</summary>
    Found,

    /// <summary>SAPI could not tell. Callers must not create or replace anything on this.</summary>
    Failed,
}

/// <summary>Three-state answer to "which timeline does this clip have". There is deliberately no way to read a failure as "none".</summary>
public sealed record TimelineLookup(TimelineLookupKind Kind, JsonObject? Timeline = null, string? Error = null)
{
    public static TimelineLookup None { get; } = new(TimelineLookupKind.None);

    public static TimelineLookup Found(JsonObject timeline) => new(TimelineLookupKind.Found, timeline);

    public static TimelineLookup Failed(string error) => new(TimelineLookupKind.Failed, null, error);
}

/// <summary>
/// Port of the plugin's <c>SapiClient::getTimelineForClip</c>, including the duplicate guard: when the lookup cannot be
/// completed it says <see cref="TimelineLookupKind.Failed"/> instead of "no timeline", because "none" would let a save
/// create a second live timeline next to the one that could not be read (fable-audit #2, ADO 19578).
/// </summary>
public sealed class CtaTimelineLocator
{
    /// <summary>Per-request cap on timeline fetches, like the plugin.</summary>
    public const int MaxTimelines = 25;

    private readonly IBlueBillywigClient _client;
    private readonly ILogger _logger;

    public CtaTimelineLocator(IBlueBillywigClient client, ILogger? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<TimelineLookup> FindAsync(long clipId, CancellationToken ct = default)
    {
        if (clipId <= 0)
        {
            return TimelineLookup.Failed("Missing clip id");
        }

        // SAPI ignores parentEntityId filters on /sapi/timeline, so ask the clip for its timelines inline.
        SapiResult clip = await _client.GetAsync($"/sapi/mediaclip/{clipId}", new Dictionary<string, string> { ["expand"] = "timelines" }, ct: ct);
        if (!clip.Ok || clip.Json is not JsonObject clipJson)
        {
            return TimelineLookup.Failed($"Could not read the clip ({clip.StatusCode}); refusing to guess whether it has a timeline.");
        }

        if (clipJson["timelines"] is not JsonArray entries || entries.Count == 0)
        {
            return TimelineLookup.None;
        }

        // Prefer one we authored (marker on widgets[0].label); otherwise the last live one, so the editor can see it and
        // explicitly refuse or replace it.
        JsonObject? chosen = null;
        int attempted = 0;
        int errors = 0;
        int seen = 0;
        bool truncated = false;
        foreach (JsonNode? entry in entries)
        {
            if (seen >= MaxTimelines)
            {
                truncated = true;
                _logger.LogWarning("Blue Billywig CTA: clip={Clip} has more than {Limit} timelines; stopped looking.", clipId, MaxTimelines);
                break;
            }

            seen++;
            JsonNode? idNode = SapiJson.At(entry, "id");
            if (SapiJson.PhpEmpty(idNode))
            {
                continue;
            }

            attempted++;
            long timelineId = SapiJson.Int(idNode);
            if (timelineId <= 0)
            {
                errors++;
                _logger.LogWarning("Blue Billywig CTA: clip={Clip} has a timeline entry with an unusable id; cannot verify it.", clipId);
                continue;
            }

            SapiResult tl = await _client.GetAsync($"/sapi/timeline/{timelineId}", ct: ct);
            if (!tl.Ok || tl.Json is not JsonObject timeline)
            {
                errors++;
                _logger.LogWarning("Blue Billywig CTA: clip={Clip} timeline={Timeline} fetch failed ({Status}).", clipId, timelineId, tl.StatusCode);
                continue;
            }

            // SAPI soft-deletes timelines (status=deleted) and keeps the reference on the clip.
            if (string.Equals(SapiJson.Str(timeline["status"]), "deleted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SapiJson.At(timeline, "widgets", 0, "label") is JsonValue lv && lv.TryGetValue(out string? label) && label.StartsWith(CtaTranslator.AuthoredLabelPrefix, StringComparison.Ordinal))
            {
                return TimelineLookup.Found(timeline);
            }

            chosen = timeline;
        }

        // Stricter than the plugin, which only failed when EVERY attempted fetch failed. Here any unreadable or unseen
        // timeline, when none of ours was found, means we cannot say the clip has no CTA of ours: a save would risk a duplicate.
        if (errors > 0 && attempted > 0 && (chosen is null || errors == attempted))
        {
            return TimelineLookup.Failed("Could not fetch every timeline for this clip; refusing to create a duplicate.");
        }

        if (truncated)
        {
            return TimelineLookup.Failed($"This clip has more than {MaxTimelines} timelines; refusing to guess which is the call to action.");
        }

        return chosen is null ? TimelineLookup.None : TimelineLookup.Found(chosen);
    }
}

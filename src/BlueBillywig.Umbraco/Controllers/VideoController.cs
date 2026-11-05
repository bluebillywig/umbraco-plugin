using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Web.Common.Authorization;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>Lookups for the "Blue Billywig Video" property editor. Read-only against SAPI.</summary>
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class VideoController : BlueBillywigControllerBase
{
    private const int MaxResults = 15;

    private readonly IBlueBillywigClient _client;
    private readonly ILibraryService _library;
    private readonly IThumbnailState? _thumbnails;

    public VideoController(IBlueBillywigClient client, ILibraryService library, IThumbnailState? thumbnails = null)
    {
        _client = client;
        _library = library;
        _thumbnails = thumbnails;
    }

    /// <summary>
    /// Searches and pages the library for the picker. It sits under the content section policy, not the Blue Billywig
    /// section's: an editor who may only insert videos does not need the library section to find one.
    /// </summary>
    [HttpGet("clips")]
    [ProducesResponseType<LibraryClipListResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Clips(string? search, string? folder, string? sourcetype, string? mediatype, string? from, string? to, int? page, CancellationToken ct)
    {
        if (!LibraryQuery.TryParse(search, folder, sourcetype, mediatype, from, to, page, out LibraryFilter filter, out string error))
        {
            return BadRequest(new { error });
        }

        LibraryResult<LibraryClipListResponseModel> result = await _library.ListClipsAsync(filter, MaxResults, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    /// <summary>The folder tree, for the picker's folder sidebar.</summary>
    [HttpGet("folders")]
    [ProducesResponseType<IReadOnlyList<FolderResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Folders(CancellationToken ct)
    {
        LibraryResult<IReadOnlyList<FolderResponseModel>> result = await _library.GetFoldersAsync(ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    [HttpGet("clips/{id:long}")]
    [ProducesResponseType<ClipSummaryResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Clip(long id, CancellationToken ct)
    {
        if (id is <= 0 or >= 1_000_000_000_000)
        {
            return BadRequest(new { error = "Invalid id" });
        }

        SapiResult result = await _client.GetAsync($"/sapi/mediaclip/{id}", ct: ct);
        if (!result.Ok)
        {
            return result.StatusCode == StatusCodes.Status404NotFound ? NotFound() : FromSapi(result);
        }

        ClipSummaryResponseModel? summary = result.Json is JsonObject clip
            ? ToSummary(clip, await ClipThumbnails.AssetBaseAsync(_client, ct), _thumbnails)
            : null;
        return summary is null ? NotFound() : Ok(summary);
    }

    /// <summary>
    /// The player URL for the property editor's "Load preview" (the reference plugin's editor frames the player with a session
    /// token so drafts render). It sits under the content section policy next to the picker, so an editor who may only insert
    /// videos can preview them. The token comes from the read-only key pair only and only for the configured publication.
    /// </summary>
    [HttpGet("preview")]
    [ProducesResponseType<ClipPreviewResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Preview(long id, string? type, string? playout, string? publication, CancellationToken ct)
    {
        if (id is <= 0 or >= 1_000_000_000_000)
        {
            return BadRequest(new { error = "Invalid id" });
        }

        BlueBillywigVideoType kind;
        switch (type?.ToLowerInvariant())
        {
            case null or "" or "clip": kind = BlueBillywigVideoType.Clip; break;
            case "channel": kind = BlueBillywigVideoType.Channel; break;
            case "shorts": kind = BlueBillywigVideoType.Shorts; break;
            default: return BadRequest(new { error = "Invalid type" });
        }

        if (!string.IsNullOrEmpty(playout) && !BlueBillywigVideo.IsValidPlayout(playout))
        {
            return BadRequest(new { error = "Invalid playout" });
        }

        if (!string.IsNullOrEmpty(publication) && !BlueBillywigVideo.IsValidPublication(publication.ToLowerInvariant()))
        {
            return BadRequest(new { error = "Invalid publication" });
        }

        LibraryResult<ClipPreviewResponseModel> result = await _library.GetEmbedPreviewAsync(kind, id, playout, publication, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    /// <summary>
    /// Whether the publication has channels and shorts (the reference plugin's <c>bb_block_features</c>), so the picker offers
    /// a type only when there is something to choose. Cached for an hour by the client; an error is never cached and a flag
    /// that could not be determined is reported true, so a failing count never hides a type.
    /// </summary>
    [HttpGet("features")]
    [ProducesResponseType<EmbedFeaturesResponseModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Features(CancellationToken ct)
    {
        Task<bool?> channels = HasAnyAsync("/sapi/channel", ct);
        Task<bool?> shorts = HasAnyAsync("/sapi/shorts", ct);
        await Task.WhenAll(channels, shorts);
        return Ok(new EmbedFeaturesResponseModel { HasChannels = channels.Result ?? true, HasShorts = shorts.Result ?? true });
    }

    /// <summary>True when the entity list has at least one entry, null when the count could not be read.</summary>
    private async Task<bool?> HasAnyAsync(string path, CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync(path, new Dictionary<string, string> { ["limit"] = "0" }, cacheFor: TimeSpan.FromHours(1), ct: ct);
        return result.Ok && result.Json?["numfound"] is JsonValue found && TryCount(found, out long count) ? count > 0 : null;
    }

    private static bool TryCount(JsonValue value, out long count) =>
        value.TryGetValue(out count) || (value.TryGetValue(out string? text) && long.TryParse(text, out count));

    [HttpGet("channels")]
    [ProducesResponseType<IReadOnlyList<EntityOptionResponseModel>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Channels(CancellationToken ct) => ListEntitiesAsync("/sapi/channel", "Channel", ct);

    [HttpGet("shorts")]
    [ProducesResponseType<IReadOnlyList<EntityOptionResponseModel>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Shorts(CancellationToken ct) => ListEntitiesAsync("/sapi/shorts", "Shorts", ct);

    [HttpGet("playouts")]
    [ProducesResponseType<IReadOnlyList<PlayoutOptionResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Playouts(CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync("/sapi/playout/", cacheFor: TimeSpan.FromHours(1), ct: ct);
        if (!result.Ok)
        {
            return FromSapi(result);
        }

        List<PlayoutOptionResponseModel> playouts = (result.Json?["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => new PlayoutOptionResponseModel { Label = Str(p["name"]), Value = Str(p["label"]) })
            .Where(p => BlueBillywigVideo.IsValidPlayout(p.Value))
            .Select(p => p.Label.Length == 0 ? new PlayoutOptionResponseModel { Label = p.Value, Value = p.Value } : p)
            .ToList();
        if (playouts.Count == 0)
        {
            playouts.Add(new PlayoutOptionResponseModel { Label = "Default", Value = "default" });
        }

        return Ok(playouts);
    }

    private async Task<IActionResult> ListEntitiesAsync(string path, string fallbackName, CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync(path, new Dictionary<string, string> { ["q"] = "*", ["limit"] = "100", ["sort"] = "name asc" }, ct: ct);
        if (!result.Ok)
        {
            return FromSapi(result);
        }

        IReadOnlyList<EntityOptionResponseModel> items = (result.Json?["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(e => new { Id = Int(e["id"]), Entity = e })
            .Where(e => e.Id > 0)
            .Select(e => new EntityOptionResponseModel
            {
                Id = e.Id,
                Name = Str(e.Entity["name"]) is { Length: > 0 } name ? name : $"{fallbackName} {e.Id}",
                Status = Str(e.Entity["status"]) is { Length: > 0 } status ? status : null,
            })
            .ToList();
        return Ok(items);
    }

    private static ClipSummaryResponseModel? ToSummary(JsonObject clip, string assetBase, IThumbnailState? thumbnails)
    {
        long id = Int(clip["id"]);
        if (id <= 0)
        {
            return null;
        }

        int length = (int)Int(clip["length"]);
        return new ClipSummaryResponseModel
        {
            Id = id,
            Title = Str(clip["title"]) is { Length: > 0 } title ? title : $"Clip {id}",
            ThumbnailUrl = ClipThumbnails.Absolute(assetBase, thumbnails is null ? ClipThumbnails.MainSrc(clip) : thumbnails.ClipMainSrc(id, clip)),
            Length = length > 0 ? length : null,
            Status = Str(clip["status"]) is { Length: > 0 } status ? status : null,
            EmbargoFrom = ClipEditRules.EmbargoOrNull(Str(clip["embargoFrom"])),
            EmbargoTo = ClipEditRules.EmbargoOrNull(Str(clip["embargoTo"])),
        };
    }

    private static string Str(JsonNode? node) => node is JsonValue v ? (v.TryGetValue(out string? s) ? s?.Trim() ?? "" : v.ToString()) : "";

    private static long Int(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue(out long l) || (v.TryGetValue(out string? s) && long.TryParse(s, out l))) ? l : 0;
}

using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// The Blue Billywig section's library: clips, livestreams, playlists, folders and details. Reading needs access to
/// the section; deleting also needs a user group from the configured list. A SAPI failure is a 502, never an empty list.
/// </summary>
[Authorize(Policy = BlueBillywigPolicies.Library)]
public sealed class LibraryController : BlueBillywigControllerBase
{
    private const long MaxId = 1_000_000_000_000;

    private readonly ILibraryService _library;
    private readonly IBlueBillywigClient _client;
    private readonly IAuthorizationService? _authorization;

    public LibraryController(ILibraryService library, IBlueBillywigClient client, IAuthorizationService? authorization = null)
    {
        _library = library;
        _client = client;
        _authorization = authorization;
    }

    /// <summary>What the signed-in user may do, so the UI can hide a Delete button it would only be refused for. The server still enforces it.</summary>
    [HttpGet("library/capabilities")]
    [ProducesResponseType<LibraryCapabilitiesResponseModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Capabilities()
    {
        bool canDelete = _authorization is not null && (await _authorization.AuthorizeAsync(User, BlueBillywigPolicies.Delete)).Succeeded;
        return Ok(new LibraryCapabilitiesResponseModel { CanDelete = canDelete });
    }

    [HttpGet("library/clips")]
    [ProducesResponseType<LibraryClipListResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Clips(string? search, string? folder, string? mediatype, string? from, string? to, int? page, CancellationToken ct)
    {
        // Like the plugin's Media Clips tab, every source type is listed; the livestream tab has its own endpoint with a fixed source type.
        if (!LibraryQuery.TryParse(search, folder, null, mediatype, from, to, page, out LibraryFilter filter, out string error))
        {
            return BadRequest(new { error });
        }

        LibraryResult<LibraryClipListResponseModel> result = await _library.ListClipsAsync(filter, LibraryQuery.MaxPageSize, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    [HttpGet("library/livestreams")]
    [ProducesResponseType<LibraryClipListResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Livestreams(string? search, int? page, CancellationToken ct)
    {
        if (!LibraryQuery.TryParse(search, null, "live", null, null, null, page, out LibraryFilter filter, out string error))
        {
            return BadRequest(new { error });
        }

        LibraryResult<LibraryClipListResponseModel> result = await _library.ListClipsAsync(filter, LibraryQuery.MaxPageSize, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    [HttpGet("library/playlists")]
    [ProducesResponseType<LibraryPlaylistListResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Playlists(string? search, int? page, CancellationToken ct)
    {
        if (!LibraryQuery.TryParse(search, null, null, null, null, null, page, out LibraryFilter filter, out string error))
        {
            return BadRequest(new { error });
        }

        LibraryResult<LibraryPlaylistListResponseModel> result = await _library.ListPlaylistsAsync(filter, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    [HttpGet("library/folders")]
    [ProducesResponseType<IReadOnlyList<FolderResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Folders(CancellationToken ct)
    {
        LibraryResult<IReadOnlyList<FolderResponseModel>> result = await _library.GetFoldersAsync(ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    [HttpGet("library/clips/{id:long}")]
    [ProducesResponseType<LibraryClipResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Clip(long id, CancellationToken ct)
    {
        if (id is <= 0 or >= MaxId)
        {
            return BadRequest(new { error = "Invalid id" });
        }

        LibraryResult<LibraryClipResponseModel> result = await _library.GetClipAsync(id, ct);
        return result.Ok ? Ok(result.Value) : NotFoundOr502(result.Failure!);
    }

    /// <summary>The player URL for the details preview. The token is minted here, on request, from the read-only pair only.</summary>
    [HttpGet("library/clips/{id:long}/preview")]
    [ProducesResponseType<ClipPreviewResponseModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(long id, CancellationToken ct)
    {
        if (id is <= 0 or >= MaxId)
        {
            return BadRequest(new { error = "Invalid id" });
        }

        LibraryResult<ClipPreviewResponseModel> result = await _library.GetPreviewAsync(id, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    [HttpGet("library/clips/{id:long}/embed-code")]
    [ProducesResponseType<EmbedCodeResponseModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EmbedCode(long id, string? playout, CancellationToken ct)
    {
        if (id is <= 0 or >= MaxId)
        {
            return BadRequest(new { error = "Invalid id" });
        }

        LibraryResult<EmbedCodeResponseModel> result = await _library.GetEmbedCodeAsync(id, playout, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    /// <summary>Deletes a clip. A stricter policy than reading: the equivalent of the reference plugin's destructive-action capability (Editor and up).</summary>
    [HttpDelete("library/clips/{id:long}")]
    [Authorize(Policy = BlueBillywigPolicies.Delete)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        if (id is <= 0 or >= MaxId)
        {
            return BadRequest(new { error = "Invalid id" });
        }

        SapiResult result = await _client.DeleteAsync($"/sapi/mediaclip/{id}", ct);
        if (!result.Ok)
        {
            return FromSapi(result);
        }

        _client.ClearCache();
        return Ok(new { ok = true });
    }

    private IActionResult NotFoundOr502(SapiResult failure) =>
        failure.StatusCode == StatusCodes.Status404NotFound ? NotFound() : FromSapi(failure);
}

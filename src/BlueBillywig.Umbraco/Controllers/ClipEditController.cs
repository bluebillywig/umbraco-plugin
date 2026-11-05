using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// Editing clips: metadata, tags, custom fields, subtitles, thumbnails and bulk actions. Reading and editing metadata
/// needs access to the Blue Billywig section (the plugin's Author level). Destructive calls, bulk delete and deleting a
/// subtitle, carry the stricter <see cref="BlueBillywigPolicies.Delete"/> policy (the plugin's Editor level) on the
/// action itself; so does changing a clip's content protection policy, checked in the action because it shares the update and bulk endpoints. A SAPI failure is a 502, a refused input a 400, and nothing is reported as saved that was not.
/// </summary>
[Authorize(Policy = BlueBillywigPolicies.Library)]
public sealed class ClipEditController : BlueBillywigControllerBase
{
    private readonly IClipEditService _edit;
    private readonly IAuthorizationService _authorization;

    public ClipEditController(IClipEditService edit, IAuthorizationService authorization)
    {
        _edit = edit;
        _authorization = authorization;
    }

    /// <summary>
    /// Changing a clip's content protection policy decides who may watch it, so it needs the stricter <see cref="BlueBillywigPolicies.Delete"/>
    /// policy, like deleting does. The endpoint is also used for ordinary edits, so the check is made here, when the request
    /// actually carries a policy change, instead of as an attribute on the action (an attribute would lock editors out of
    /// every edit). Null means the request did not touch the policy.
    /// </summary>
    private async Task<IActionResult?> RequireProtectAsync()
    {
        AuthorizationResult result = await _authorization.AuthorizeAsync(User, BlueBillywigPolicies.Delete);
        return result.Succeeded ? null : Refuse(StatusCodes.Status403Forbidden, "Changing content protection needs the same permission as deleting");
    }

    [HttpGet("library/clips/{id:long}/edit")]
    [ProducesResponseType<ClipEditResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Respond(await _edit.GetAsync(id, ct));

    /// <summary>Saves the fields that are sent. Status and CPP are checked against their allowlists, custom fields against SAPI's definitions.</summary>
    [HttpPut("library/clips/{id:long}")]
    [ProducesResponseType<ClipEditResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(long id, ClipUpdateRequestModel request, CancellationToken ct)
    {
        if (request.Cpp is not null && await RequireProtectAsync() is { } denied)
        {
            return denied;
        }

        return Respond(await _edit.UpdateAsync(id, request, ct));
    }

    /// <summary>Status, folder or CPP on up to 50 clips. Delete is <c>bulk-delete</c>.</summary>
    [HttpPost("library/clips/bulk")]
    [ProducesResponseType<BulkResultResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Bulk(BulkRequestModel request, CancellationToken ct)
    {
        // Delete is not reachable here: it must pass the stricter policy on its own endpoint.
        if (request.Action is null || !ClipEditRules.BulkActions.Contains(request.Action, StringComparer.Ordinal))
        {
            return Refuse(StatusCodes.Status400BadRequest, "Unknown bulk action");
        }

        if (request.Action == "cpp" && await RequireProtectAsync() is { } denied)
        {
            return denied;
        }

        return Respond(await _edit.BulkAsync(request.Action, request.Ids, request.Value, ct));
    }

    [HttpPost("library/clips/bulk-delete")]
    [Authorize(Policy = BlueBillywigPolicies.Delete)]
    [ProducesResponseType<BulkResultResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BulkDelete(BulkDeleteRequestModel request, CancellationToken ct) => Respond(await _edit.BulkAsync("delete", request.Ids, null, ct));

    [HttpGet("library/clips/{id:long}/subtitles")]
    [ProducesResponseType<IReadOnlyList<SubtitleResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Subtitles(long id, CancellationToken ct) => Respond(await _edit.ListSubtitlesAsync(id, ct));

    [HttpPost("library/clips/{id:long}/subtitles")]
    [ProducesResponseType<IReadOnlyList<SubtitleResponseModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadSubtitle(long id, SubtitleUploadRequestModel request, CancellationToken ct) => Respond(await _edit.UploadSubtitleAsync(id, request, ct));

    [HttpDelete("library/clips/{id:long}/subtitles/{subtitleId:long}")]
    [Authorize(Policy = BlueBillywigPolicies.Delete)]
    [ProducesResponseType<IReadOnlyList<SubtitleResponseModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSubtitle(long id, long subtitleId, CancellationToken ct) => Respond(await _edit.DeleteSubtitleAsync(id, subtitleId, ct));

    /// <remarks>Takes the body <c>{"confirm": true}</c> (the CSRF guard of the settings actions, see <see cref="SettingsController.ConfirmRequest"/>).</remarks>
    [HttpPost("library/clips/{id:long}/subtitles/{subtitleId:long}/default")]
    [ProducesResponseType<IReadOnlyList<SubtitleResponseModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultSubtitle(long id, long subtitleId, SettingsController.ConfirmRequest request, CancellationToken ct) =>
        request.Confirm ? Respond(await _edit.SetDefaultSubtitleAsync(id, subtitleId, ct)) : Refuse(StatusCodes.Status400BadRequest, "This action needs the body {\"confirm\": true}");

    [HttpPost("library/clips/{id:long}/thumbnails")]
    [ProducesResponseType<ThumbnailsResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadThumbnail(long id, ThumbnailUploadRequestModel request, CancellationToken ct) => Respond(await _edit.UploadThumbnailAsync(id, request.Data, ct));

    [HttpPost("library/clips/{id:long}/thumbnails/main")]
    [ProducesResponseType<ThumbnailsResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMainThumbnail(long id, ThumbnailMainRequestModel request, CancellationToken ct) => Respond(await _edit.SetMainThumbnailAsync(id, request.Src, ct));

    [HttpGet("library/tags")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Tags(string? q, CancellationToken ct) => Respond(await _edit.SuggestTagsAsync(q, ct));

    [HttpGet("library/custom-field-groups")]
    [ProducesResponseType<IReadOnlyList<CustomFieldGroupResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CustomFieldGroups(CancellationToken ct) => Respond(await _edit.GetCustomFieldGroupsAsync(ct));

    [HttpGet("library/languages")]
    [ProducesResponseType<IReadOnlyList<LanguageResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Languages(CancellationToken ct) => Respond(await _edit.GetLanguagesAsync(ct));

    [HttpGet("library/policies")]
    [ProducesResponseType<IReadOnlyList<PolicyResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Policies(CancellationToken ct) => Respond(await _edit.GetPoliciesAsync(ct));

    [HttpGet("library/playouts")]
    [ProducesResponseType<IReadOnlyList<PlayoutOptionResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Playouts(CancellationToken ct) => Respond(await _edit.GetPlayoutsAsync(ct));

    private IActionResult Respond<T>(EditOutcome<T> outcome) where T : class
    {
        if (outcome.Ok)
        {
            return Ok(outcome.Value);
        }

        if (outcome.Failure is { } failure)
        {
            return failure.StatusCode switch
            {
                StatusCodes.Status404NotFound => NotFound(),
                _ => FromSapi(failure),
            };
        }

        return Refuse(outcome.Status ?? StatusCodes.Status400BadRequest, outcome.Error ?? "The request was refused", outcome.Dropped);
    }

    /// <summary>
    /// A refusal as RFC 7807 problem details. The backoffice's API interceptor keeps a response body only when it has that
    /// shape (anything else becomes a bare status; <c>type</c>, <c>title</c> and <c>status</c> must all be present), so the sentence for the editor travels in <c>detail</c>.
    /// </summary>
    private ObjectResult Refuse(int status, string detail, IReadOnlyList<string>? dropped = null)
    {
        ProblemDetails problem = new() { Type = "BlueBillywig.Refused", Status = status, Title = status switch { 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 422 => "Unprocessable Entity", _ => "Request refused" }, Detail = detail };
        if (dropped is { Count: > 0 })
        {
            problem.Extensions["dropped"] = dropped;
        }

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}

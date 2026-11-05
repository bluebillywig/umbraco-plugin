using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Upload;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// The server side of direct-to-storage upload. These are small JSON calls that coordinate with SAPI: the browser
/// sends the file's parts to the presigned storage URLs itself, and no file bytes reach this server.
/// </summary>
[Authorize(Policy = BlueBillywigPolicies.Library)]
public sealed class UploadController : BlueBillywigControllerBase
{
    private const long MaxId = 1_000_000_000_000;

    private readonly IUploadService _uploads;
    private readonly IBackOfficeUserInfo _users;

    public UploadController(IUploadService uploads, IBackOfficeUserInfo users)
    {
        _uploads = uploads;
        _users = users;
    }

    /// <summary>Creates the clip and starts the multipart upload. Rejects a status outside the allowlist.</summary>
    [HttpPost("upload/init")]
    [ProducesResponseType<InitUploadResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Init(InitUploadRequestModel request, CancellationToken ct)
    {
        if (!UploadValidation.TryParseInit(request, out NewClip clip, out string error))
        {
            return BadRequest(new { error });
        }

        if (_users.GetUserKey(User) is not { } userKey)
        {
            return Forbid();
        }

        UploadOutcome<InitUploadResponseModel> result = await _uploads.InitAsync(clip, userKey, ct);
        return result.Ok ? Ok(result.Value) : FromSapi(result.Failure!);
    }

    /// <summary>A refusal by the service (not the user's upload) is its status and message; a SAPI failure is a 502.</summary>
    private IActionResult Outcome<T>(UploadOutcome<T> result) where T : class =>
        result.Ok ? Ok(result.Value)
        : result.Error is not null ? StatusCode(result.Status ?? StatusCodes.Status403Forbidden, new { error = result.Error })
        : FromSapi(result.Failure!);

    [HttpPost("upload/sign-part")]
    [ProducesResponseType<SignPartResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SignPart(SignPartRequestModel request, CancellationToken ct)
    {
        if (!UploadValidation.TryParseSign(request, out string uploadId, out int partNumber, out string error))
        {
            return BadRequest(new { error });
        }

        if (_users.GetUserKey(User) is not { } userKey)
        {
            return Forbid();
        }

        UploadOutcome<SignPartResponseModel> result = await _uploads.SignPartAsync(uploadId, partNumber, userKey, ct);
        return Outcome(result);
    }

    [HttpPost("upload/complete")]
    [ProducesResponseType<CompleteUploadResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Complete(CompleteUploadRequestModel request, CancellationToken ct)
    {
        if (!UploadValidation.TryParseComplete(request, out string uploadId, out List<(int PartNumber, string ETag)> parts, out string error))
        {
            return BadRequest(new { error });
        }

        if (_users.GetUserKey(User) is not { } userKey)
        {
            return Forbid();
        }

        UploadOutcome<CompleteUploadResponseModel> result = await _uploads.CompleteAsync(uploadId, parts, userKey, ct);
        return Outcome(result);
    }

    /// <summary>Removes the placeholder clip of an upload this user started and has not completed.</summary>
    [HttpPost("upload/abort")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Abort(AbortUploadRequestModel request, CancellationToken ct)
    {
        if (request.MediaclipId is <= 0 or >= MaxId)
        {
            return BadRequest(new { error = "Missing mediaclipId" });
        }

        if (_users.GetUserKey(User) is not { } userKey)
        {
            return Forbid();
        }

        UploadOutcome<object> result = await _uploads.AbortAsync(request.MediaclipId, userKey, ct);
        if (result.Failure is not null)
        {
            return FromSapi(result.Failure);
        }

        return result.Error is not null ? StatusCode(result.Status ?? StatusCodes.Status403Forbidden, new { error = result.Error }) : Ok(new { ok = true });
    }
}

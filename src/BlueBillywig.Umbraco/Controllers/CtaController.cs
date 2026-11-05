using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Cta;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// The call to action of a clip: read, save, delete, the save rules without saving, and the background image upload.
/// The reference plugin guards all four of its actions with one check (<c>publish_posts</c>, the Author level), the same
/// as clip metadata, so they all sit behind <see cref="BlueBillywigPolicies.Library"/>; removal is not given the stricter
/// <see cref="BlueBillywigPolicies.Delete"/> policy because the plugin's <c>delete_cta</c> does not have it either.
/// When the settings turn the feature off every endpoint answers 404. A timeline made by another tool is refused with 409
/// and left alone; a SAPI failure is a 502 without upstream text.
/// </summary>
[Authorize(Policy = BlueBillywigPolicies.Library)]
public sealed class CtaController : BlueBillywigControllerBase
{
    private readonly ICtaService _cta;

    public CtaController(ICtaService cta) => _cta = cta;

    [HttpGet("library/clips/{id:long}/cta")]
    [ProducesResponseType<CtaStateResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCta(long id, CancellationToken ct) => Respond(await _cta.GetAsync(id, ct));

    /// <summary>Creates or updates the clip's call to action. Answers with what is stored.</summary>
    [HttpPut("library/clips/{id:long}/cta")]
    [ProducesResponseType<CtaStateResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SaveCta(long id, CtaConfig request, CancellationToken ct) => Respond(await _cta.SaveAsync(id, request, ct));

    /// <summary>
    /// Removes the call to action the package made. A POST on purpose: the reference plugin's <c>delete_cta</c> needs only
    /// the Author level (the same check as save), so this carries the same policy, and the endpoint sweep requires every
    /// HTTP DELETE to carry the stricter <see cref="BlueBillywigPolicies.Delete"/> policy. To make removal Editor-only,
    /// change this to <c>[HttpDelete("library/clips/{id:long}/cta")]</c> with that policy on the action.
    /// </summary>
    /// <remarks>
    /// Takes the body <c>{"confirm": true}</c>, like the settings actions that used to take none: a required JSON body is the
    /// CSRF guard (a cross-site form cannot send one, a cross-site fetch with a JSON type needs a preflight this API never grants).
    /// </remarks>
    [HttpPost("library/clips/{id:long}/cta/remove")]
    [ProducesResponseType<CtaDeleteResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveCta(long id, SettingsController.ConfirmRequest request, CancellationToken ct) =>
        request.Confirm ? Respond(await _cta.DeleteAsync(id, ct)) : Refuse(StatusCodes.Status400BadRequest, "This action needs the body {\"confirm\": true}");

    /// <summary>Runs the save rules on a config and lists their messages. Writes nothing and does not talk to SAPI.</summary>
    [HttpPost("library/cta/validate")]
    [ProducesResponseType<CtaValidateResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult ValidateCta(CtaConfig request) => Respond(_cta.Validate(request));

    /// <summary>Creates the picture as a creative and answers with its id.</summary>
    [HttpPost("library/cta/images")]
    [ProducesResponseType<CtaImageUploadResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadCtaImage(CtaImageUploadRequestModel request, CancellationToken ct) => Respond(await _cta.UploadImageAsync(request.Data, ct));

    private IActionResult Respond<T>(EditOutcome<T> outcome) where T : class
    {
        if (outcome.Ok)
        {
            return Ok(outcome.Value);
        }

        if (outcome.Failure is { } failure)
        {
            return failure.StatusCode == StatusCodes.Status404NotFound ? Refuse(StatusCodes.Status404NotFound, "Not found") : FromSapi(failure);
        }

        return Refuse(outcome.Status ?? StatusCodes.Status400BadRequest, outcome.Error ?? "The request was refused", outcome.Dropped);
    }

    /// <summary>
    /// A refusal as RFC 7807 problem details, the only error body the backoffice's API client keeps (it needs
    /// <c>type</c>, <c>title</c> and <c>status</c>). The sentence for the editor travels in <c>detail</c>.
    /// </summary>
    private static ObjectResult Refuse(int status, string detail, IReadOnlyList<string>? errors = null)
    {
        var problem = new ProblemDetails
        {
            Type = "BlueBillywig.Refused",
            Status = status,
            Title = status switch { 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict", _ => "Request refused" },
            Detail = detail,
        };
        if (errors is { Count: > 0 })
        {
            problem.Extensions["errors"] = errors;
        }

        if (status == StatusCodes.Status409Conflict)
        {
            problem.Extensions["code"] = "foreign_timeline";
        }

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}

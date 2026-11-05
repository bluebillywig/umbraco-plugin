using Asp.Versioning;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Common.Filters;
using Umbraco.Cms.Web.Common.Routing;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// Base for backoffice endpoints, served under <c>/umbraco/bluebillywig/api/v1</c> and published in the
/// package's own OpenAPI document, which the TypeScript client is generated from.
/// </summary>
[ApiController]
[BackOfficeRoute("bluebillywig/api/v{version:apiVersion}")]
[MapToApi(Constants.ApiName)]
[JsonOptionsName(global::Umbraco.Cms.Core.Constants.JsonOptionsNames.BackOffice)]
[ApiVersion("1.0")]
[ApiExplorerSettings(GroupName = "BlueBillywig.Umbraco")]
public abstract class BlueBillywigControllerBase : ControllerBase
{
    /// <summary>Maps a failed SAPI call to 502, so a SAPI outage is distinct from an empty library.</summary>
    protected IActionResult FromSapi(SapiResult result) =>
        result.Ok
            ? Content(result.RawBody ?? "null", "application/json")
            : StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error });
}

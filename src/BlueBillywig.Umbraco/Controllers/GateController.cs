using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Web.Common.Authorization;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// Content protection in the settings dashboard: the gate's configuration, its error tally (G7) and the policy list (G8).
/// All under the Settings section, like the rest of the settings: the plugin shows the gate error notice to
/// <c>manage_options</c> only. The shared secret is write-only and never appears in a response.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
public sealed class GateController : BlueBillywigControllerBase
{
    private readonly IGateConfigStore _config;
    private readonly GateHealthTally _health;
    private readonly IClipEditService _edit;
    private readonly IBlueBillywigClient _client;
    private readonly IGateMemberGroupSource _groups;

    public GateController(IGateConfigStore config, GateHealthTally health, IClipEditService edit, IBlueBillywigClient client, IGateMemberGroupSource groups)
    {
        _config = config;
        _health = health;
        _edit = edit;
        _client = client;
        _groups = groups;
    }

    [HttpGet("settings/gate")]
    [ProducesResponseType<GateSettingsResponseModel>(StatusCodes.Status200OK)]
    public IActionResult GetGate() => Ok(Current());

    [HttpPut("settings/gate")]
    [ProducesResponseType<GateSettingsResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SaveGate(GateSettingsRequestModel request)
    {
        (GateConfig? config, string? error) = GateConfigRules.Validate(request.Enabled, request.ExpirySeconds, request.MemberGroups, request.Message, request.LoginUrl);
        if (config is null)
        {
            return BadRequest(new { error });
        }

        // A secret pinned by configuration cannot be changed here; typing one is refused rather than silently ignored.
        string? secret = null;
        if (request.ClearSharedSecret)
        {
            secret = "";
        }
        else if (!string.IsNullOrEmpty(request.SharedSecret))
        {
            string trimmed = request.SharedSecret.Trim();
            if (GateConfigRules.ValidateSecret(trimmed) is { } secretError)
            {
                return BadRequest(new { error = secretError });
            }

            secret = trimmed;
        }

        if (secret is not null && _config.SharedSecretPinned)
        {
            return BadRequest(new { error = "The shared secret is set in configuration and cannot be changed here" });
        }

        _config.Save(config, secret);
        return Ok(Current());
    }

    /// <summary>
    /// The content protection policies of the publication (plugin: <c>get_content_protection_policies</c>): code, name and
    /// status only. 503 when the API is not configured, 502 when Blue Billywig fails, as in the plugin.
    /// </summary>
    [HttpGet("settings/gate/policies")]
    [ProducesResponseType<IReadOnlyList<PolicyResponseModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Policies(CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Blue Billywig API not configured" });
        }

        EditOutcome<IReadOnlyList<PolicyResponseModel>> outcome = await _edit.GetPoliciesAsync(ct);
        return outcome.Ok ? Ok(outcome.Value) : FromSapi(outcome.Failure ?? SapiResult.Failure(502, "Blue Billywig did not answer"));
    }

    [HttpGet("settings/gate/member-groups")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MemberGroups(CancellationToken ct) => Ok(await _groups.GetNamesAsync(ct));

    /// <summary>The gate's error tally for the admin banner: counts per reason from the last hour, and whether the pepper could not be saved.</summary>
    [HttpGet("settings/gate/health")]
    [ProducesResponseType<GateHealthResponseModel>(StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        GateHealthSnapshot snapshot = _health.Snapshot();
        return Ok(new GateHealthResponseModel
        {
            Healthy = snapshot.IsHealthy,
            Errors = snapshot.ErrorCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => new GateErrorCountResponseModel { Reason = p.Key, Count = p.Value }).ToList(),
            ErrorTotal = snapshot.ErrorCounts.Values.Sum(),
            LastErrorAt = snapshot.LastErrorAt?.ToString("o"),
            PepperFailed = snapshot.PepperFailedAt is not null,
        });
    }

    private GateSettingsResponseModel Current()
    {
        GateConfig c = _config.Get();
        return new GateSettingsResponseModel
        {
            Enabled = c.Enabled,
            ExpirySeconds = c.ExpirySeconds,
            MemberGroups = c.MemberGroups,
            Message = c.Message,
            LoginUrl = c.LoginUrl,
            HasSharedSecret = _config.HasSharedSecret,
            SharedSecretPinned = _config.SharedSecretPinned,
        };
    }
}

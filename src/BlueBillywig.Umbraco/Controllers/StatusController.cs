using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// Facts the backoffice shows to everyone who works with videos: is the package configured, which publication, where its
/// platform is. Read-only, never a secret. Under <see cref="BlueBillywigPolicies.Overview"/>, so the Content dashboard, the
/// rich text toolbar and the Blue Billywig section can all ask.
/// </summary>
[Authorize(Policy = BlueBillywigPolicies.Overview)]
public sealed class StatusController : BlueBillywigControllerBase
{
    private readonly IBlueBillywigSettingsStore _settings;
    private readonly IBlueBillywigClient _client;

    public StatusController(IBlueBillywigSettingsStore settings, IBlueBillywigClient client)
    {
        _settings = settings;
        _client = client;
    }

    public sealed record StatusResponse(
        bool Configured,
        string Publication,
        string? PublicationName,
        string OvpUrl,
        string DefaultPlayout,
        bool EnableCta,
        bool EnableCustomFields);

    /// <summary>
    /// <c>Configured</c> is the plugin's <c>isConnected()</c>: publication, API ID and API secret are all set, whether they
    /// come from the settings dashboard or from configuration. The OVP address comes from the configured base URL, so a
    /// publication on a development OVP links to that OVP and not to <c>bbvms.com</c>.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<StatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        BlueBillywigSettings s = _settings.Get();
        string? name = null;
        if (s.HasCredentials)
        {
            SapiResult publication = await _client.GetAsync("/sapi/publication", cacheFor: TimeSpan.FromHours(1), ct: ct);
            name = publication.Ok && publication.Json?["name"] is JsonValue v && v.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        }

        return Ok(new StatusResponse(
            s.HasCredentials,
            s.Publication,
            name,
            s.Publication.Length > 0 || !string.IsNullOrWhiteSpace(s.BaseUrl) ? s.ResolveBaseUrl() + "/ovp/" : "",
            s.Playout,
            s.EnableCta,
            s.EnableCustomFields));
    }
}

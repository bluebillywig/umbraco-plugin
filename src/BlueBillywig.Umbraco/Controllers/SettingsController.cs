using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Web.Common.Authorization;

namespace BlueBillywig.Umbraco.Controllers;

[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
public sealed class SettingsController : BlueBillywigControllerBase
{
    private static readonly string[] EmbedTypes = ["javascript", "iframe", "oembed", "url", "amp"];

    private readonly IBlueBillywigSettingsStore _store;
    private readonly IBlueBillywigClient _client;
    private readonly IBlueBillywigCleanup _cleanup;
    private readonly ISapiCredentialTester _tester;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(IBlueBillywigSettingsStore store, IBlueBillywigClient client, IBlueBillywigCleanup cleanup, ISapiCredentialTester tester, ILogger<SettingsController> logger)
    {
        _store = store;
        _client = client;
        _cleanup = cleanup;
        _tester = tester;
        _logger = logger;
    }

    /// <summary>
    /// The reference plugin's publication shape: lowercase ASCII letters, digits, dots and dashes; starts and ends with a letter
    /// or digit; 3 to 63 characters (one DNS label); no two dots in a row, because <c>https://bb..dev.bbvms.com</c> silently fails
    /// to resolve. The input is trimmed, not lowercased: uppercase is refused, as there. The shape is <see cref="PublicationSlug"/>,
    /// shared with rendering and the settings store; what is typed here needs the reference plugin's 3 characters, while a
    /// configured or stored value only needs the shared shape (the dev publication <c>bb</c>).
    /// </summary>
    internal static bool IsValidPublication(string publication) =>
        publication.Length >= PublicationSlug.MinTypedLength && PublicationSlug.IsValid(publication);

    /// <summary>The settings as saved, and whether the SAPI cache was cleared by the save (it is, unless clearing failed).</summary>
    public sealed record SaveSettingsResponse(SettingsResponse Settings, bool CacheCleared);

    public sealed record CleanupResponse(IReadOnlyList<string> Cleared);

    public sealed record SettingsResponse(
        string Publication,
        string? BaseUrl,
        bool BaseUrlRejected,
        string ApiId,
        bool HasApiSecret,
        string ReadOnlyApiId,
        bool HasReadOnlyApiSecret,
        string Playout,
        string Embed,
        int PageCount,
        bool EnableCustomFields,
        bool EnableCta,
        string ConsentMessage,
        IReadOnlyCollection<string> ConfigOverrides);

    /// <summary>
    /// What the dashboard sends. There is no <c>BaseUrl</c> on purpose: the SAPI origin is configuration only
    /// (<c>BlueBillywig:BaseUrl</c>), because every SAPI call carries the read-write token to it. A body that still names one
    /// is bound without it.
    /// </summary>
    public sealed record SettingsRequest(
        string? Publication,
        string? ApiId,
        string? ApiSecret,
        string? ReadOnlyApiId,
        string? ReadOnlyApiSecret,
        string? Playout,
        string? Embed,
        int? PageCount,
        bool EnableCustomFields,
        bool EnableCta,
        string? ConsentMessage = null,
        bool ClearApiSecret = false,
        bool ClearReadOnlyApiSecret = false);

    /// <summary>Secrets are write-only: the response says whether one is set, never what it is.</summary>
    [HttpGet("settings")]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    public IActionResult GetSettings() => Ok(Current());

    private SettingsResponse Current()
    {
        BlueBillywigSettings s = _store.Get();
        return new SettingsResponse(
            s.Publication,
            s.BaseUrl,
            s.BaseUrlRejected,
            s.ApiId,
            s.ApiSecret.Length > 0,
            s.ReadOnlyApiId,
            s.ReadOnlyApiSecret.Length > 0,
            s.Playout,
            s.Embed,
            s.PageCount,
            s.EnableCustomFields,
            s.EnableCta,
            s.ConsentMessage,
            _store.ConfigOverrides());
    }

    /// <summary>
    /// Saves the settings. The credentials are checked against SAPI first, like the reference plugin's settings save, and a
    /// failed check refuses the save: 400, nothing stored, cache untouched. The check runs when publication, API ID and API
    /// secret are all present (typed, else stored; configuration wins). Blank API ID and secret mean "not connected" and save
    /// without a check, so does a partly filled form, as in the plugin. An empty secret keeps the stored one (the UI never
    /// reads secrets back); <c>ClearApiSecret</c> removes it.
    /// </summary>
    [HttpPut("settings")]
    [ProducesResponseType<SaveSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveSettings(SettingsRequest request, CancellationToken ct = default)
    {
        BlueBillywigSettings current = _store.Get();
        IReadOnlyCollection<string> pinned = _store.ConfigOverrides();

        // Config-pinned values are read-only: whatever the form sends for them is ignored, and not validated.
        string publication = pinned.Contains("publication") ? current.Publication : (request.Publication ?? "").Trim();
        if (!pinned.Contains("publication") && publication.Length > 0 && !IsValidPublication(publication))
        {
            return BadRequest(new { error = "Publication must be a lowercase ASCII slug (a-z, 0-9, dots, dashes, no consecutive dots, 3 to 63 characters)" });
        }

        string apiId = pinned.Contains("apiId") ? current.ApiId : (request.ApiId ?? "").Trim();
        string readOnlyApiId = pinned.Contains("roApiId") ? current.ReadOnlyApiId : (request.ReadOnlyApiId ?? "").Trim();
        if ((apiId.Length > 0 && !apiId.All(char.IsAsciiDigit)) || (readOnlyApiId.Length > 0 && !readOnlyApiId.All(char.IsAsciiDigit)))
        {
            return BadRequest(new { error = "API IDs must be numeric" });
        }

        // Validated before anything is written, like the plugin's slug check on the default playout (PlayoutValidationTest).
        string playout = string.IsNullOrWhiteSpace(request.Playout) ? "default" : request.Playout.Trim();
        if (!ClipPlayoutName.IsValid(playout))
        {
            return BadRequest(new { error = "Invalid playout name" });
        }

        // A read-only pair that is the read-write pair would put the read-write token in preview URLs.
        if (readOnlyApiId.Length > 0 && readOnlyApiId == apiId)
        {
            return BadRequest(new { error = "The read-only API ID must be a different key from the API ID" });
        }

        string embed = request.Embed ?? "javascript";
        if (!EmbedTypes.Contains(embed))
        {
            return BadRequest(new { error = "Invalid embed type" });
        }

        int pageCount = request.PageCount ?? 15;
        if (pageCount < 1 || pageCount > LibraryQuery.MaxPageSize)
        {
            return BadRequest(new { error = $"Page count must be between 1 and {LibraryQuery.MaxPageSize}" });
        }

        // Configuration only: whatever the form sends is not even bound (see SettingsRequest).
        string? baseUrl = current.BaseUrl;

        // The secret that will be in effect: pinned, else cleared, else typed, else the stored one.
        string? typedSecret = pinned.Contains("apiSecret") || string.IsNullOrEmpty(request.ApiSecret) ? null : request.ApiSecret;
        string? apiSecret = pinned.Contains("apiSecret") ? null : request.ClearApiSecret ? "" : typedSecret;
        string effectiveSecret = pinned.Contains("apiSecret") ? current.ApiSecret : apiSecret ?? current.ApiSecret;

        // S12: the credentials are checked before anything is written. The plugin does the same when all three are present;
        // an incomplete set is saved as it is (the plugin's "not connected" and "fill in all credentials" states).
        if (publication.Length > 0 && apiId.Length > 0 && effectiveSecret.Length > 0)
        {
            CredentialCheck check = await _tester.TestAsync(
                new BlueBillywigSettings { Publication = publication, BaseUrl = baseUrl, BaseUrlRejected = current.BaseUrlRejected, ApiId = apiId, ApiSecret = effectiveSecret }, ct);
            if (!check.Ok)
            {
                return BadRequest(new { error = "API connection failed: " + check.Error });
            }
        }

        string? readOnlySecret = pinned.Contains("roApiSecret")
            ? null
            : request.ClearReadOnlyApiSecret ? "" : string.IsNullOrEmpty(request.ReadOnlyApiSecret) ? null : request.ReadOnlyApiSecret;

        // The read-only key is checked the same way when it is given (id and a secret, typed or stored): it is what browsers
        // get for the library preview, so a wrong one would otherwise only show up later as broken previews.
        string effectiveReadOnlySecret = pinned.Contains("roApiSecret") ? current.ReadOnlyApiSecret : readOnlySecret ?? current.ReadOnlyApiSecret;
        if (publication.Length > 0 && readOnlyApiId.Length > 0 && effectiveReadOnlySecret.Length > 0)
        {
            CredentialCheck roCheck = await _tester.TestAsync(
                new BlueBillywigSettings { Publication = publication, BaseUrl = baseUrl, BaseUrlRejected = current.BaseUrlRejected, ApiId = readOnlyApiId, ApiSecret = effectiveReadOnlySecret }, ct);
            if (!roCheck.Ok)
            {
                return BadRequest(new { error = "Read-only API connection failed: " + roCheck.Error });
            }
        }

        _store.Save(
            new BlueBillywigSettings
            {
                Publication = publication,
                BaseUrl = baseUrl,
                ApiId = apiId,
                ReadOnlyApiId = readOnlyApiId,
                Playout = playout,
                Embed = embed,
                PageCount = pageCount,
                EnableCustomFields = request.EnableCustomFields,
                EnableCta = request.EnableCta,
                ConsentMessage = pinned.Contains("consentMessage") ? current.ConsentMessage : ConsentMessage.Clean(request.ConsentMessage),
            },
            apiSecret,
            readOnlySecret);

        // The store clears the SAPI cache through its Changed event; doing it here as well makes the result visible, so
        // a clear that failed is told to the editor instead of leaving stale answers behind unnoticed.
        bool cleared = TryClearCache();
        return Ok(new SaveSettingsResponse(Current(), cleared));
    }

    /// <summary>
    /// The body of the actions that otherwise take none (disconnect, clear cache, cleanup). A required JSON body is the CSRF
    /// guard: a cross-site HTML form can only send form-encoded or text bodies, which the JSON binder answers with 415, and a
    /// cross-site <c>fetch</c> with a JSON content type needs a CORS preflight that this API never grants.
    /// </summary>
    public sealed record ConfirmRequest(bool Confirm);

    private const string ConfirmError = "This action needs the body {\"confirm\": true}";

    public sealed record DisconnectResponse(SettingsResponse Settings, bool CacheCleared, IReadOnlyCollection<string> StillConfigured);

    /// <summary>
    /// Disconnects from Blue Billywig (the reference plugin's "clear the secret to disconnect"): empties the stored
    /// publication, API pair and read-only pair, and clears the caches. Values pinned in configuration are not stored here and
    /// stay; they are listed in <c>StillConfigured</c> so the dashboard does not claim a disconnect that did not happen.
    /// </summary>
    [HttpPost("settings/disconnect")]
    [ProducesResponseType<DisconnectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Disconnect(ConfirmRequest request)
    {
        if (!request.Confirm)
        {
            return BadRequest(new { error = ConfirmError });
        }

        _store.Disconnect();
        bool cleared = TryClearCache();
        string[] credentialKeys = ["publication", "apiId", "apiSecret", "roApiId", "roApiSecret"];
        return Ok(new DisconnectResponse(Current(), cleared, _store.ConfigOverrides().Where(credentialKeys.Contains).ToList()));
    }

    /// <summary>
    /// The playouts of the publication, for the default-playout dropdown. It sits under the Settings policy because the
    /// editors' playout list needs the Content section, which a user who only manages settings may not have.
    /// </summary>
    [HttpGet("settings/playouts")]
    [ProducesResponseType<IReadOnlyList<PlayoutOptionResponseModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Playouts(CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync("/sapi/playout/", cacheFor: TimeSpan.FromHours(1), ct: ct);
        return result.Ok ? Ok(ClipPlayoutName.Options(result.Json)) : FromSapi(result);
    }

    public sealed record TestConnectionResponse(bool Ok, string? Publication, string? Error);

    /// <summary>The values currently typed in the form. Blank means "use the stored one", the secret included. No base URL: see <see cref="SettingsRequest"/>.</summary>
    public sealed record TestConnectionRequest(string? Publication, string? ApiId, string? ApiSecret);

    /// <summary>
    /// Tests the values typed in the form before they are saved (the reference plugin's <c>test_connection</c>), with a
    /// throwaway client. Nothing is stored and the live client and its cache are not touched. Any field left blank falls back
    /// to the stored value, so "test" works without retyping the secret (send <c>{}</c>; a body is required, see <see cref="ConfirmRequest"/>); config-pinned values win, as they do for every call.
    /// </summary>
    [HttpPost("settings/test-connection")]
    [ProducesResponseType<TestConnectionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestConnection(TestConnectionRequest request, CancellationToken ct = default)
    {
        BlueBillywigSettings current = _store.Get();
        IReadOnlyCollection<string> pinned = _store.ConfigOverrides();
        string Pick(string key, string? typed, string stored) =>
            pinned.Contains(key) || string.IsNullOrWhiteSpace(typed) ? stored : typed.Trim();

        string publication = Pick("publication", request.Publication, current.Publication);
        if (!pinned.Contains("publication") && publication.Length > 0 && !IsValidPublication(publication))
        {
            return Ok(new TestConnectionResponse(false, null, "Publication must be a lowercase ASCII slug (a-z, 0-9, dots, dashes, no consecutive dots, 3 to 63 characters)"));
        }

        // Configuration only, like on save: a typed origin would make the server send the key to a host of the caller's choice.
        string? baseUrl = current.BaseUrl;

        CredentialCheck check = await _tester.TestAsync(
            new BlueBillywigSettings
            {
                Publication = publication,
                BaseUrl = baseUrl,
                BaseUrlRejected = current.BaseUrlRejected,
                ApiId = Pick("apiId", request.ApiId, current.ApiId),
                ApiSecret = pinned.Contains("apiSecret") || string.IsNullOrEmpty(request.ApiSecret) ? current.ApiSecret : request.ApiSecret,
            },
            ct);
        return Ok(new TestConnectionResponse(check.Ok, check.Publication, check.Error));
    }

    [HttpPost("settings/clear-cache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ClearCache(ConfirmRequest request) =>
        request.Confirm ? Ok(new { ok = TryClearCache() }) : BadRequest(new { error = ConfirmError });

    /// <summary>
    /// The uninstall equivalent: empties every setting and per-clip playout this package stored, by the explicit key list in
    /// <see cref="IBlueBillywigCleanup"/>, and clears the cache. It does not remove the package and does not touch another
    /// package's entries. After it the package is unconfigured.
    /// </summary>
    [HttpPost("settings/cleanup")]
    [ProducesResponseType<CleanupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Cleanup(ConfirmRequest request) =>
        request.Confirm ? Ok(new CleanupResponse(_cleanup.Run())) : BadRequest(new { error = ConfirmError });

    private bool TryClearCache()
    {
        try
        {
            _client.ClearCache();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clearing the Blue Billywig cache failed");
            return false;
        }
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Security;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>A signed-in frontend member as the gate needs them.</summary>
/// <param name="Key">The member's Guid key (<c>N</c> format): stable when the member is renamed or their email changes.</param>
/// <param name="Groups">Names of the member groups the member belongs to (the equivalent of the reference plugin's user roles).</param>
public sealed record GateMember(string Key, IReadOnlyCollection<string> Groups, bool IsApproved, bool IsLockedOut);

/// <summary>Finds the member behind a request. Behind an interface so the mapping is tested without Umbraco's identity stack.</summary>
public interface IGateMemberSource
{
    /// <summary>The signed-in member, or null when the visitor is anonymous (this includes a signed-in backoffice user).</summary>
    Task<GateMember?> GetCurrentAsync(HttpContext http, CancellationToken ct);
}

/// <summary>
/// <see cref="IGateMemberSource"/> over Umbraco's <see cref="IMemberManager"/>. <c>GetCurrentMemberAsync</c> reads the
/// request's principal, which is only a frontend member principal when the member authentication scheme signed in; the
/// backoffice user's cookie belongs to another scheme, so a backoffice user is anonymous here.
/// </summary>
internal sealed class UmbracoGateMemberSource : IGateMemberSource
{
    public async Task<GateMember?> GetCurrentAsync(HttpContext http, CancellationToken ct)
    {
        // IMemberManager is scoped to the request and this source is a singleton (the renderer is), so it is taken from the request.
        IMemberManager members = http.RequestServices.GetRequiredService<IMemberManager>();
        MemberIdentityUser? user = await members.GetCurrentMemberAsync();
        if (user is null)
        {
            return null;
        }

        // The roles are read from the member store on every request, not taken from the sign-in cookie, so a member moved
        // out of a group loses access at the next page view and not when the cookie expires.
        IList<string> roles = await members.GetRolesAsync(user);
        return new GateMember(user.Key.ToString("N"), roles.ToArray(), user.IsApproved, user.IsLockedOut);
    }
}

/// <summary>Resolves the visitor once per request, before the gate runs (the seam <see cref="IGateViewerProvider.GetViewer"/> is synchronous).</summary>
public interface IGateViewerPrimer
{
    Task PrimeAsync(CancellationToken ct);
}

/// <summary>
/// Maps the visitor to a <see cref="GateViewer"/>: anonymous, or a signed-in Umbraco member (frontend members, not
/// backoffice users) with their member groups as the access test.
/// <para>
/// Backoffice users are treated as anonymous visitors. The plugin lists <c>administrator</c> among the roles that may
/// watch (there an administrator is a user like any other), but an Umbraco backoffice login is not a site login: it is
/// a different cookie and principal, and letting it through would make the gate depend on which browser an editor
/// happens to be signed in with. An editor who needs to see protected content signs in as a member, as a visitor would.
/// </para>
/// <para>
/// Fail closed: a member that is not approved or is locked out is anonymous; a lookup that failed is an error (the gate
/// reports it as <c>internal_error</c> and shows the gate message), never "anonymous", never "allowed".
/// </para>
/// <para>
/// Deliberate differences to the reference plugin (G2, G9), pending the owner's decision; this class does not change
/// behaviour for them. The plugin maps the CMS user roles and by default lets administrator, editor, author and subscriber
/// through; here access comes from member groups and nobody is allowed until groups are chosen (see
/// <see cref="GateSettingsStore"/>). The plugin's message is HTML filtered to a safe subset; here it is HTML-encoded plain text
/// (see <see cref="GateMessage"/>). A signed-in backoffice user is not a member and is treated as anonymous.
/// </para>
/// </summary>
internal sealed class MemberGateViewerProvider : IGateViewerProvider, IGateViewerPrimer
{
    private const string ItemsKey = "BlueBillywig.GateViewer";

    private readonly IHttpContextAccessor _http;
    private readonly IGateConfigStore _config;
    private readonly IGateMemberSource _members;
    private readonly ILogger _logger;

    public MemberGateViewerProvider(IHttpContextAccessor http, IGateConfigStore config, IGateMemberSource members, ILogger<MemberGateViewerProvider>? logger = null)
    {
        _http = http;
        _config = config;
        _members = members;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task PrimeAsync(CancellationToken ct)
    {
        HttpContext? http = _http.HttpContext;
        if (http is null || http.Items.ContainsKey(ItemsKey))
        {
            return;
        }

        try
        {
            http.Items[ItemsKey] = new Primed(await _members.GetCurrentAsync(http, ct), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blue Billywig ContentGate: the signed-in member could not be looked up.");
            http.Items[ItemsKey] = new Primed(null, e);
        }
    }

    public GateViewer GetViewer(GateEntity entity)
    {
        HttpContext? http = _http.HttpContext;
        if (http is null)
        {
            // No request, no visitor (a render outside a page view): never a member.
            return GateViewer.Anonymous();
        }

        if (!http.Items.TryGetValue(ItemsKey, out object? stored) || stored is not Primed primed)
        {
            throw new InvalidOperationException("The gate viewer was not resolved for this request.");
        }

        if (primed.Failure is not null)
        {
            throw new InvalidOperationException("The signed-in member could not be looked up.", primed.Failure);
        }

        string? loginUrl = LoginUrl(http);
        if (primed.Member is not { IsApproved: true, IsLockedOut: false } member)
        {
            return GateViewer.Anonymous(loginUrl);
        }

        return new GateViewer(true, member.Key, IsAllowed(member, _config.Get().MemberGroups), loginUrl);
    }

    /// <summary>True only on a positive match; an empty list matches nobody.</summary>
    internal static bool IsAllowed(GateMember member, IReadOnlyList<string> allowedGroups) =>
        allowedGroups.Count > 0 && member.Groups.Any(g => allowedGroups.Contains(g, StringComparer.OrdinalIgnoreCase));

    private string? LoginUrl(HttpContext http)
    {
        string? template = _config.Get().LoginUrl;
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        // The query string can carry tokens or personal data: it goes to the login page only when that page is on this site
        // (a relative login URL). An absolute login URL, which may be another host, gets the path alone.
        string current = http.Request.PathBase + http.Request.Path
            + (template.TrimStart().StartsWith('/') ? http.Request.QueryString.ToString() : "");
        return template.Replace("{returnUrl}", Uri.EscapeDataString(current), StringComparison.Ordinal);
    }

    private sealed record Primed(GateMember? Member, Exception? Failure);
}

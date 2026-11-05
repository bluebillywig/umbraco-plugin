using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BlueBillywig.Umbraco.Embedding;

/// <summary>Who may see the visible marker the renderer adds when it could not prepare the player markup for the page's CSP nonce.</summary>
public enum EmbedProblemAudience
{
    /// <summary>Nobody: the renderer keeps its silent fallback and logs a warning.</summary>
    Nobody,

    /// <summary>Everybody, because the site switched <see cref="BlueBillywigOptions.ShowEmbedProblems"/> on. The output is the same for all visitors, so it may be cached.</summary>
    Everyone,

    /// <summary>The signed-in backoffice user making this request. The output differs per user, so the response must be kept out of shared caches.</summary>
    SignedInUser,
}

/// <summary>
/// Decides who sees the embed problem marker (the reference plugin's CSP failure marker, which it shows to everyone; the policy
/// here is narrower, see ARCHITECTURE.md). A site can replace it (registered with <c>TryAdd</c>), for example to show the marker
/// to its own staff group only.
/// </summary>
public interface IBlueBillywigEmbedDiagnostics
{
    ValueTask<EmbedProblemAudience> GetAudienceAsync(HttpContext? httpContext);
}

/// <summary>
/// The default: the configured switch shows the marker to everybody; otherwise only a request that carries a signed-in
/// backoffice user (the user on the request, else the backoffice cookie scheme) sees it. Any doubt is "nobody".
/// </summary>
internal sealed class BackOfficeEmbedDiagnostics : IBlueBillywigEmbedDiagnostics
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<BlueBillywigOptions> _options;

    // The user lookup is resolved when it is needed, not injected: it comes from Umbraco's authorization helper, and the
    // renderer (a singleton used by sites and tests that never sign anyone in) must be constructible without it.
    public BackOfficeEmbedDiagnostics(IServiceProvider services, IOptionsMonitor<BlueBillywigOptions> options)
    {
        _services = services;
        _options = options;
    }

    public async ValueTask<EmbedProblemAudience> GetAudienceAsync(HttpContext? httpContext)
    {
        if (_options.CurrentValue.ShowEmbedProblems)
        {
            return EmbedProblemAudience.Everyone;
        }

        if (httpContext is null || _services.GetService(typeof(IBackOfficeUserInfo)) is not IBackOfficeUserInfo users)
        {
            return EmbedProblemAudience.Nobody;
        }

        try
        {
            if (users.GetUserKey(httpContext.User) is not null)
            {
                return EmbedProblemAudience.SignedInUser;
            }

            // The front end often does not carry the backoffice identity on the request; ask the backoffice scheme.
            AuthenticateResult result = await httpContext.AuthenticateAsync(global::Umbraco.Cms.Core.Constants.Security.BackOfficeAuthenticationType);
            return result.Succeeded && result.Principal is { } principal && users.GetUserKey(principal) is not null
                ? EmbedProblemAudience.SignedInUser
                : EmbedProblemAudience.Nobody;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // No such scheme on this host, or an unreadable identity: nobody sees the marker.
            return EmbedProblemAudience.Nobody;
        }
    }
}

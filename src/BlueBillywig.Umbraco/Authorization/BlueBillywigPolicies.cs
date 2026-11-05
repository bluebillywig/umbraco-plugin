using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Web.Common.Authorization;
using BlueBillywig.Umbraco.Configuration;

namespace BlueBillywig.Umbraco.Authorization;

/// <summary>
/// The capability model (the reference plugin: Author for metadata and upload, Editor for destructive actions).
/// <list type="bullet">
/// <item><see cref="Library"/>: backoffice access to the Blue Billywig section. Reading the library and uploading.</item>
/// <item><see cref="Delete"/>: the section, and membership of one of the configured user groups.</item>
/// </list>
/// </summary>
public static class BlueBillywigPolicies
{
    public const string Library = "BlueBillywig.Library";
    public const string Delete = "BlueBillywig.Delete";

    /// <summary>
    /// A weaker policy for read-only facts that the Content dashboard, the rich text toolbar and the Blue Billywig section
    /// all need (is the package configured, which publication): access to the Content, Settings or Blue Billywig section.
    /// </summary>
    public const string Overview = "BlueBillywig.Overview";

    /// <summary>The alias of the backoffice section. The client manifest uses the same string.</summary>
    public const string SectionAlias = "BlueBillywig.Section";

    /// <summary>User groups that may delete when nothing is configured: Administrators and Editors (the reference plugin: Editor level and up).</summary>
    public static readonly string[] DefaultDeleteGroups = ["admin", "editor"];
}

/// <summary>Group aliases and the key of the signed-in backoffice user. Behind an interface so the handler needs no host to test.</summary>
public interface IBackOfficeUserInfo
{
    IReadOnlyCollection<string> GetGroupAliases(ClaimsPrincipal principal);

    /// <summary>The sections the user may open, which come from the user's groups.</summary>
    IReadOnlyCollection<string> GetAllowedSections(ClaimsPrincipal principal);

    Guid? GetUserKey(ClaimsPrincipal principal);
}

internal sealed class BackOfficeUserInfo : IBackOfficeUserInfo
{
    private readonly IAuthorizationHelper _helper;

    public BackOfficeUserInfo(IAuthorizationHelper helper) => _helper = helper;

    public IReadOnlyCollection<string> GetGroupAliases(ClaimsPrincipal principal) =>
        _helper.TryGetUmbracoUser(principal, out var user) ? user.Groups.Select(g => g.Alias).ToList() : [];

    public IReadOnlyCollection<string> GetAllowedSections(ClaimsPrincipal principal) =>
        _helper.TryGetUmbracoUser(principal, out var user) ? user.AllowedSections.ToList() : [];

    public Guid? GetUserKey(ClaimsPrincipal principal) =>
        _helper.TryGetUmbracoUser(principal, out var user) ? user.Key : null;
}

/// <summary>Requires the section to be among the signed-in user's allowed sections (Umbraco's own section policies do the same for the core sections).</summary>
public sealed class SectionAccessRequirement(string section) : IAuthorizationRequirement
{
    public string Section { get; } = section;
}

public sealed class SectionAccessHandler : AuthorizationHandler<SectionAccessRequirement>
{
    private readonly IBackOfficeUserInfo _users;

    public SectionAccessHandler(IBackOfficeUserInfo users) => _users = users;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SectionAccessRequirement requirement)
    {
        if (_users.GetAllowedSections(context.User).Contains(requirement.Section, StringComparer.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Requires any one of the sections among the signed-in user's allowed sections.</summary>
public sealed class AnySectionRequirement(params string[] sections) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Sections { get; } = sections;
}

public sealed class AnySectionAccessHandler : AuthorizationHandler<AnySectionRequirement>
{
    private readonly IBackOfficeUserInfo _users;

    public AnySectionAccessHandler(IBackOfficeUserInfo users) => _users = users;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AnySectionRequirement requirement)
    {
        IReadOnlyCollection<string> allowed = _users.GetAllowedSections(context.User);
        if (requirement.Sections.Any(s => allowed.Contains(s, StringComparer.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Requires the user to belong to one of the configured user groups.</summary>
public sealed class UserGroupRequirement : IAuthorizationRequirement;

public sealed class UserGroupHandler : AuthorizationHandler<UserGroupRequirement>
{
    private readonly IBackOfficeUserInfo _users;
    private readonly IOptionsMonitor<BlueBillywigOptions> _options;

    public UserGroupHandler(IBackOfficeUserInfo users, IOptionsMonitor<BlueBillywigOptions> options)
    {
        _users = users;
        _options = options;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, UserGroupRequirement requirement)
    {
        string[] allowed = _options.CurrentValue.DeleteUserGroups is { Length: > 0 } configured
            ? configured
            : BlueBillywigPolicies.DefaultDeleteGroups;
        if (_users.GetGroupAliases(context.User).Any(g => allowed.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Adds the two policies. They copy the authentication scheme of Umbraco's own section policy, so a backoffice
/// token or cookie is accepted exactly as it is for the core sections.
/// </summary>
internal sealed class ConfigureBlueBillywigPolicies : IPostConfigureOptions<AuthorizationOptions>
{
    public void PostConfigure(string? name, AuthorizationOptions options)
    {
        AuthorizationPolicy? core = options.GetPolicy(AuthorizationPolicies.SectionAccessSettings);
        string[] schemes = core?.AuthenticationSchemes.ToArray() ?? [];

        options.AddPolicy(BlueBillywigPolicies.Library, policy =>
        {
            policy.AuthenticationSchemes = schemes;
            policy.Requirements.Add(new SectionAccessRequirement(BlueBillywigPolicies.SectionAlias));
        });
        options.AddPolicy(BlueBillywigPolicies.Overview, policy =>
        {
            policy.AuthenticationSchemes = schemes;
            policy.Requirements.Add(new AnySectionRequirement(
                global::Umbraco.Cms.Core.Constants.Applications.Content,
                global::Umbraco.Cms.Core.Constants.Applications.Settings,
                BlueBillywigPolicies.SectionAlias));
        });
        options.AddPolicy(BlueBillywigPolicies.Delete, policy =>
        {
            policy.AuthenticationSchemes = schemes;
            policy.Requirements.Add(new SectionAccessRequirement(BlueBillywigPolicies.SectionAlias));
            policy.Requirements.Add(new UserGroupRequirement());
        });
    }
}

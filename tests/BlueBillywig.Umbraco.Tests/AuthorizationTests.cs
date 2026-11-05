using System.Security.Claims;
using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Web.Common.Authorization;

namespace BlueBillywig.Umbraco.Tests;

public class AuthorizationTests
{
    private sealed class FakeUsers(string[] groups, string[] sections, Guid? key = null) : IBackOfficeUserInfo
    {
        public IReadOnlyCollection<string> GetGroupAliases(ClaimsPrincipal principal) => groups;

        public IReadOnlyCollection<string> GetAllowedSections(ClaimsPrincipal principal) => sections;

        public Guid? GetUserKey(ClaimsPrincipal principal) => key;
    }

    private sealed class Options(BlueBillywigOptions value) : IOptionsMonitor<BlueBillywigOptions>
    {
        public BlueBillywigOptions CurrentValue => value;

        public BlueBillywigOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<BlueBillywigOptions, string?> listener) => null;
    }

    private static async Task<bool> Succeeds<T>(AuthorizationHandler<T> handler, T requirement) where T : IAuthorizationRequirement
    {
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity("test")), null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task The_section_requirement_needs_the_section_among_the_users_sections()
    {
        Assert.True(await Succeeds(new SectionAccessHandler(new FakeUsers([], ["content", BlueBillywigPolicies.SectionAlias])), new SectionAccessRequirement(BlueBillywigPolicies.SectionAlias)));
        Assert.False(await Succeeds(new SectionAccessHandler(new FakeUsers(["admin"], ["content", "media"])), new SectionAccessRequirement(BlueBillywigPolicies.SectionAlias)));
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("editor", true)]
    [InlineData("Editor", true)]
    [InlineData("writer", false)]
    [InlineData("translator", false)]
    [InlineData("sensitiveData", false)]
    public async Task Delete_defaults_to_administrators_and_editors(string group, bool allowed)
    {
        var handler = new UserGroupHandler(new FakeUsers([group], []), new Options(new BlueBillywigOptions()));

        Assert.Equal(allowed, await Succeeds(handler, new UserGroupRequirement()));
    }

    [Fact]
    public async Task Delete_groups_can_be_configured()
    {
        var options = new Options(new BlueBillywigOptions { DeleteUserGroups = ["writer"] });

        Assert.True(await Succeeds(new UserGroupHandler(new FakeUsers(["writer"], []), options), new UserGroupRequirement()));
        Assert.False(await Succeeds(new UserGroupHandler(new FakeUsers(["admin"], []), options), new UserGroupRequirement()));
    }

    [Fact]
    public async Task A_user_in_no_group_cannot_delete() =>
        Assert.False(await Succeeds(new UserGroupHandler(new FakeUsers([], []), new Options(new BlueBillywigOptions())), new UserGroupRequirement()));

    [Fact]
    public void The_policies_copy_the_scheme_of_the_core_section_policy_and_delete_is_stricter()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy(AuthorizationPolicies.SectionAccessSettings, p => p.AddAuthenticationSchemes("Umbraco.Test.Scheme").RequireAuthenticatedUser());

        new ConfigureBlueBillywigPolicies().PostConfigure(null, options);

        AuthorizationPolicy library = options.GetPolicy(BlueBillywigPolicies.Library)!;
        AuthorizationPolicy delete = options.GetPolicy(BlueBillywigPolicies.Delete)!;
        Assert.Equal(["Umbraco.Test.Scheme"], library.AuthenticationSchemes);
        Assert.Equal(["Umbraco.Test.Scheme"], delete.AuthenticationSchemes);
        Assert.Single(library.Requirements);
        Assert.IsType<SectionAccessRequirement>(library.Requirements[0]);
        Assert.Contains(delete.Requirements, r => r is SectionAccessRequirement);
        Assert.Contains(delete.Requirements, r => r is UserGroupRequirement);
    }

    [Theory]
    [InlineData(typeof(BlueBillywig.Umbraco.Controllers.LibraryController), nameof(BlueBillywig.Umbraco.Controllers.LibraryController.Clips), BlueBillywigPolicies.Library)]
    [InlineData(typeof(BlueBillywig.Umbraco.Controllers.LibraryController), nameof(BlueBillywig.Umbraco.Controllers.LibraryController.Delete), BlueBillywigPolicies.Delete)]
    [InlineData(typeof(BlueBillywig.Umbraco.Controllers.UploadController), nameof(BlueBillywig.Umbraco.Controllers.UploadController.Init), BlueBillywigPolicies.Library)]
    [InlineData(typeof(BlueBillywig.Umbraco.Controllers.UploadController), nameof(BlueBillywig.Umbraco.Controllers.UploadController.Abort), BlueBillywigPolicies.Library)]
    public void Endpoints_carry_the_policy_that_guards_them(Type controller, string action, string policy)
    {
        var onType = controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Select(a => a.Policy);
        var onAction = controller.GetMethod(action)!.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Select(a => a.Policy);

        Assert.Contains(policy, onType.Concat(onAction));
        // Delete is the only action that adds a second policy to the controller's own.
        Assert.Equal(policy == BlueBillywigPolicies.Delete, onAction.Contains(BlueBillywigPolicies.Delete));
    }

    [Fact]
    public void Every_action_of_the_new_controllers_sits_behind_a_policy()
    {
        // I3: no endpoint of the library or upload controllers is reachable without a backoffice policy.
        foreach (Type controller in new[] { typeof(BlueBillywig.Umbraco.Controllers.LibraryController), typeof(BlueBillywig.Umbraco.Controllers.UploadController) })
        {
            Assert.NotEmpty(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true));
            Assert.Empty(controller.GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
        }
    }
}

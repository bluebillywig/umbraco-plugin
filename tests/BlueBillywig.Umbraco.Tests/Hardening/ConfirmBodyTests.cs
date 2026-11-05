using System.Reflection;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Cta;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>
/// The two library actions that used to take no body (remove a call to action, make a subtitle the default) now need the JSON
/// body <c>{"confirm": true}</c>, the same CSRF guard as the settings actions (see <see cref="SettingsCsrfTests"/>): a
/// cross-site HTML form cannot send a JSON body and a cross-site fetch with a JSON type needs a preflight this API never grants.
/// </summary>
public class ConfirmBodyTests
{
    private static readonly (Type Controller, string Action)[] Actions =
    [
        (typeof(CtaController), nameof(CtaController.RemoveCta)),
        (typeof(ClipEditController), nameof(ClipEditController.SetDefaultSubtitle)),
    ];

    [Fact]
    public void Both_actions_are_posts_with_a_required_non_nullable_body_the_json_binder_must_fill()
    {
        foreach ((Type controller, string name) in Actions)
        {
            MethodInfo action = controller.GetMethod(name)!;
            Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
            Assert.True(controller.GetCustomAttribute<ApiControllerAttribute>(true) is not null || controller.BaseType!.GetCustomAttribute<ApiControllerAttribute>(true) is not null);

            ParameterInfo body = action.GetParameters().Single(p => p.ParameterType == typeof(SettingsController.ConfirmRequest));
            Assert.False(body.HasDefaultValue, $"{name}: the body must not default to null");
            Assert.True(new NullabilityInfoContext().Create(body).WriteState == NullabilityState.NotNull, $"{name}: the body must be non-nullable");
            FromBodyAttribute? fromBody = body.GetCustomAttribute<FromBodyAttribute>();
            Assert.True(fromBody is null || fromBody.EmptyBodyBehavior != Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow, name);
        }
    }

    [Fact]
    public void No_post_in_these_two_controllers_is_left_without_a_body_parameter()
    {
        foreach (Type controller in new[] { typeof(CtaController), typeof(ClipEditController) })
        {
            foreach (MethodInfo action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null))
            {
                // Route values (id, subtitleId) and the token do not count: a body is a complex-typed parameter.
                Assert.Contains(action.GetParameters(), p => p.ParameterType != typeof(CancellationToken) && !p.ParameterType.IsPrimitive && p.ParameterType != typeof(string));
            }
        }
    }

    [Fact]
    public async Task Removing_the_call_to_action_without_confirmation_refuses_and_calls_nothing()
    {
        var cta = new CountingCta();
        var controller = new CtaController(cta);

        ObjectResult refused = Assert.IsType<ObjectResult>(await controller.RemoveCta(5, new SettingsController.ConfirmRequest(false), default));

        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(0, cta.Deletes);
    }

    [Fact]
    public async Task Removing_the_call_to_action_with_confirmation_goes_through()
    {
        var cta = new CountingCta();

        Assert.IsType<OkObjectResult>(await new CtaController(cta).RemoveCta(5, new SettingsController.ConfirmRequest(true), default));

        Assert.Equal(1, cta.Deletes);
    }

    [Fact]
    public async Task Making_a_subtitle_the_default_without_confirmation_refuses_and_calls_nothing()
    {
        var edit = new CountingEdit();
        var controller = new ClipEditController(edit, new NoAuthorization());

        ObjectResult refused = Assert.IsType<ObjectResult>(await controller.SetDefaultSubtitle(5, 9, new SettingsController.ConfirmRequest(false), default));

        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(0, edit.Defaults);
    }

    [Fact]
    public async Task Making_a_subtitle_the_default_with_confirmation_goes_through()
    {
        var edit = new CountingEdit();

        Assert.IsType<OkObjectResult>(await new ClipEditController(edit, new NoAuthorization()).SetDefaultSubtitle(5, 9, new SettingsController.ConfirmRequest(true), default));

        Assert.Equal(1, edit.Defaults);
    }

    private sealed class NoAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => throw new NotSupportedException();

        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, string policyName) => throw new NotSupportedException();
    }

    private sealed class CountingCta : ICtaService
    {
        public int Deletes { get; private set; }

        public Task<EditOutcome<CtaStateResponseModel>> GetAsync(long clipId, CancellationToken ct) => throw new NotSupportedException();

        public Task<EditOutcome<CtaStateResponseModel>> SaveAsync(long clipId, CtaConfig config, CancellationToken ct) => throw new NotSupportedException();

        public Task<EditOutcome<CtaDeleteResponseModel>> DeleteAsync(long clipId, CancellationToken ct)
        {
            Deletes++;
            return Task.FromResult(EditOutcome<CtaDeleteResponseModel>.Of(new CtaDeleteResponseModel { Deleted = true }));
        }

        public Task<EditOutcome<CtaImageUploadResponseModel>> UploadImageAsync(string? dataUri, CancellationToken ct) => throw new NotSupportedException();

        public EditOutcome<CtaValidateResponseModel> Validate(CtaConfig config) => throw new NotSupportedException();
    }

    private sealed class CountingEdit : IClipEditService
    {
        public int Defaults { get; private set; }

        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> SetDefaultSubtitleAsync(long id, long subtitleId, CancellationToken ct)
        {
            Defaults++;
            return Task.FromResult(EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Of([]));
        }

        public Task<EditOutcome<ClipEditResponseModel>> GetAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ClipEditResponseModel>> UpdateAsync(long id, ClipUpdateRequestModel request, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<BulkResultResponseModel>> BulkAsync(string action, IReadOnlyList<long>? ids, string? value, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> ListSubtitlesAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> UploadSubtitleAsync(long id, SubtitleUploadRequestModel request, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> DeleteSubtitleAsync(long id, long subtitleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ThumbnailsResponseModel>> UploadThumbnailAsync(long id, string? dataUri, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ThumbnailsResponseModel>> SetMainThumbnailAsync(long id, string? src, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<string>>> SuggestTagsAsync(string? prefix, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<CustomFieldGroupResponseModel>>> GetCustomFieldGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<LanguageResponseModel>>> GetLanguagesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<PolicyResponseModel>>> GetPoliciesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>>> GetPlayoutsAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

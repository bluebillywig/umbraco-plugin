using System.Reflection;
using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// M10: reading and editing metadata need the section, destructive calls need the stricter policy. These pin which
/// action carries which policy, so a new endpoint that forgets one fails here. The live check (a lower-privileged user
/// getting 403 on delete) is in PARITY.md's evidence, not here.
/// </summary>
public class ClipEditAuthorizationTests
{
    private static readonly string[] Destructive = [nameof(ClipEditController.BulkDelete), nameof(ClipEditController.DeleteSubtitle)];

    private static IEnumerable<MethodInfo> Actions() =>
        typeof(ClipEditController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static IEnumerable<string?> Policies(MemberInfo m) => m.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy);

    [Fact]
    public void The_controller_sits_behind_the_section_policy_and_nothing_is_anonymous()
    {
        Assert.Contains(BlueBillywigPolicies.Library, Policies(typeof(ClipEditController)));
        Assert.Empty(Actions().SelectMany(m => m.GetCustomAttributes<AllowAnonymousAttribute>(true)));
        Assert.NotEmpty(Actions());
    }

    [Fact]
    public void Only_the_destructive_actions_add_the_stricter_policy()
    {
        foreach (MethodInfo action in Actions())
        {
            bool strict = Policies(action).Contains(BlueBillywigPolicies.Delete);
            Assert.Equal(Destructive.Contains(action.Name), strict);
        }
    }

    [Fact]
    public void Single_clip_delete_bulk_delete_and_subtitle_delete_share_the_destructive_policy()
    {
        Assert.Contains(BlueBillywigPolicies.Delete, Policies(typeof(LibraryController).GetMethod(nameof(LibraryController.Delete))!));
        Assert.Contains(BlueBillywigPolicies.Delete, Policies(typeof(ClipEditController).GetMethod(nameof(ClipEditController.BulkDelete))!));
        Assert.Contains(BlueBillywigPolicies.Delete, Policies(typeof(ClipEditController).GetMethod(nameof(ClipEditController.DeleteSubtitle))!));
    }

    [Fact]
    public void Every_http_delete_action_is_destructive_so_a_new_one_cannot_slip_past()
    {
        foreach (MethodInfo action in Actions().Where(m => m.GetCustomAttributes<HttpDeleteAttribute>().Any()))
        {
            Assert.Contains(BlueBillywigPolicies.Delete, Policies(action));
        }
    }

    private sealed class RecordingEdit : IClipEditService
    {
        public List<string> Calls { get; } = [];

        public Task<EditOutcome<BulkResultResponseModel>> BulkAsync(string action, IReadOnlyList<long>? ids, string? value, CancellationToken ct)
        {
            Calls.Add(action);
            return Task.FromResult(EditOutcome<BulkResultResponseModel>.Of(new BulkResultResponseModel { Success = 0, Failed = 0, Succeeded = [], Errors = [] }));
        }

        public Task<EditOutcome<ClipEditResponseModel>> GetAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ClipEditResponseModel>> UpdateAsync(long id, ClipUpdateRequestModel request, CancellationToken ct)
        {
            Calls.Add("update");
            return Task.FromResult(EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "stub: the service was reached"));
        }

        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> ListSubtitlesAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> UploadSubtitleAsync(long id, SubtitleUploadRequestModel request, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> DeleteSubtitleAsync(long id, long subtitleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> SetDefaultSubtitleAsync(long id, long subtitleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ThumbnailsResponseModel>> UploadThumbnailAsync(long id, string? dataUri, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ThumbnailsResponseModel>> SetMainThumbnailAsync(long id, string? src, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<string>>> SuggestTagsAsync(string? prefix, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<CustomFieldGroupResponseModel>>> GetCustomFieldGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<LanguageResponseModel>>> GetLanguagesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<PolicyResponseModel>>> GetPoliciesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>>> GetPlayoutsAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("DELETE")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("archive")]
    public async Task The_non_destructive_bulk_endpoint_never_runs_delete(string? action)
    {
        var edit = new RecordingEdit();

        IActionResult result = await new ClipEditController(edit, AllowAll.Instance).Bulk(new BulkRequestModel { Action = action, Ids = [1] }, default);

        ProblemDetails problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal("Unknown bulk action", problem.Detail);
        Assert.Empty(edit.Calls);
    }

    [Fact]
    public async Task The_bulk_delete_endpoint_runs_delete_and_the_others_run_their_own_action()
    {
        var edit = new RecordingEdit();
        var controller = new ClipEditController(edit, AllowAll.Instance);

        await controller.BulkDelete(new BulkDeleteRequestModel { Ids = [1] }, default);
        await controller.Bulk(new BulkRequestModel { Action = "status", Ids = [1], Value = "draft" }, default);

        Assert.Equal(["delete", "status"], edit.Calls);
    }

    [Fact]
    public async Task Failures_map_to_502_and_refusals_keep_their_status_without_upstream_text()
    {
        var failing = new ClipEditController(new FailingEdit(EditOutcome<ClipEditResponseModel>.Fail(Sapi.SapiResult.Failure(500, "SAPI request failed (500)"))), AllowAll.Instance);
        Assert.Equal(502, Assert.IsType<ObjectResult>(await failing.Get(1, default)).StatusCode);

        var missing = new ClipEditController(new FailingEdit(EditOutcome<ClipEditResponseModel>.Fail(Sapi.SapiResult.Failure(404, "SAPI request failed (404)"))), AllowAll.Instance);
        Assert.IsType<NotFoundResult>(await missing.Get(1, default));

        var refused = new ClipEditController(new FailingEdit(EditOutcome<ClipEditResponseModel>.Reject(422, "These custom fields are no longer defined: x", ["x"])), AllowAll.Instance);
        ObjectResult unprocessable = Assert.IsType<ObjectResult>(await refused.Get(1, default));
        Assert.Equal(422, unprocessable.StatusCode);
        // The sentence for the editor travels as problem details, which is the only error body the backoffice client keeps.
        ProblemDetails problem = Assert.IsType<ProblemDetails>(unprocessable.Value);
        Assert.Equal("These custom fields are no longer defined: x", problem.Detail);
        // The backoffice client only keeps a body that has type, title and status.
        Assert.False(string.IsNullOrEmpty(problem.Type));
        Assert.False(string.IsNullOrEmpty(problem.Title));
        Assert.Equal(["x"], (IReadOnlyList<string>)problem.Extensions["dropped"]!);
    }

    private sealed class FailingEdit(EditOutcome<ClipEditResponseModel> outcome) : IClipEditService
    {
        public Task<EditOutcome<ClipEditResponseModel>> GetAsync(long id, CancellationToken ct) => Task.FromResult(outcome);

        public Task<EditOutcome<ClipEditResponseModel>> UpdateAsync(long id, ClipUpdateRequestModel request, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<BulkResultResponseModel>> BulkAsync(string action, IReadOnlyList<long>? ids, string? value, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> ListSubtitlesAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> UploadSubtitleAsync(long id, SubtitleUploadRequestModel request, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> DeleteSubtitleAsync(long id, long subtitleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> SetDefaultSubtitleAsync(long id, long subtitleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ThumbnailsResponseModel>> UploadThumbnailAsync(long id, string? dataUri, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<ThumbnailsResponseModel>> SetMainThumbnailAsync(long id, string? src, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<string>>> SuggestTagsAsync(string? prefix, CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<CustomFieldGroupResponseModel>>> GetCustomFieldGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<LanguageResponseModel>>> GetLanguagesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<PolicyResponseModel>>> GetPoliciesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>>> GetPlayoutsAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    // M1: changing the content protection policy needs the stricter policy ------------------------------------------

    private sealed class AuthorizationFake(bool allowDelete) : IAuthorizationService
    {
        public List<string?> Asked { get; } = [];

        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => throw new NotSupportedException();

        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, string policyName)
        {
            Asked.Add(policyName);
            return Task.FromResult(allowDelete && policyName == BlueBillywigPolicies.Delete ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }

    [Fact]
    public async Task Changing_cpp_in_a_single_edit_needs_the_delete_policy()
    {
        var edit = new RecordingEdit();
        var auth = new AuthorizationFake(allowDelete: false);
        var controller = new ClipEditController(edit, auth);

        IActionResult denied = await controller.Update(1, new ClipUpdateRequestModel { Cpp = "policy1" }, default);
        IActionResult clearing = await controller.Update(1, new ClipUpdateRequestModel { Cpp = "" }, default);

        Assert.Equal(403, Assert.IsType<ObjectResult>(denied).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(clearing).StatusCode); // removing protection is a change as well
        Assert.Empty(edit.Calls);
        Assert.All(auth.Asked, p => Assert.Equal(BlueBillywigPolicies.Delete, p));
    }

    [Fact]
    public async Task An_edit_that_leaves_cpp_alone_needs_only_the_section()
    {
        var edit = new RecordingEdit();
        var auth = new AuthorizationFake(allowDelete: false);

        IActionResult result = await new ClipEditController(edit, auth).Update(1, new ClipUpdateRequestModel { Title = "t", Status = "draft" }, default);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode); // the stub service answered: not 403
        Assert.Equal(["update"], edit.Calls);
        Assert.Empty(auth.Asked);
    }

    [Fact]
    public async Task A_user_who_may_delete_may_change_cpp()
    {
        var edit = new RecordingEdit();

        IActionResult result = await new ClipEditController(edit, new AuthorizationFake(allowDelete: true)).Update(1, new ClipUpdateRequestModel { Cpp = "policy1" }, default);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode); // the stub service answered: not 403
        Assert.Equal(["update"], edit.Calls);
    }

    [Fact]
    public async Task The_bulk_cpp_action_needs_the_delete_policy_and_the_other_bulk_actions_do_not()
    {
        var edit = new RecordingEdit();
        var controller = new ClipEditController(edit, new AuthorizationFake(allowDelete: false));

        IActionResult denied = await controller.Bulk(new BulkRequestModel { Action = "cpp", Ids = [1], Value = "policy1" }, default);
        IActionResult status = await controller.Bulk(new BulkRequestModel { Action = "status", Ids = [1], Value = "draft" }, default);
        IActionResult folder = await controller.Bulk(new BulkRequestModel { Action = "folder", Ids = [1], Value = "f" }, default);

        Assert.Equal(403, Assert.IsType<ObjectResult>(denied).StatusCode);
        Assert.IsType<OkObjectResult>(status);
        Assert.IsType<OkObjectResult>(folder);
        Assert.Equal(["status", "folder"], edit.Calls);
    }

    [Fact]
    public async Task The_bulk_cpp_action_runs_for_a_user_who_may_delete()
    {
        var edit = new RecordingEdit();

        await new ClipEditController(edit, new AuthorizationFake(allowDelete: true)).Bulk(new BulkRequestModel { Action = "cpp", Ids = [1], Value = "policy1" }, default);

        Assert.Equal(["cpp"], edit.Calls);
    }
}

internal sealed class AllowAll : IAuthorizationService
{
    public static readonly AllowAll Instance = new();

    public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Success());

    public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, string policyName) => Task.FromResult(AuthorizationResult.Success());
}

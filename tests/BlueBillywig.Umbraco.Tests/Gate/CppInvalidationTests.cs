using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Tests.Gate;

internal sealed class RecordingCpp : ICppLookup
{
    public List<GateEntity> Invalidated { get; } = [];

    public Task<CppStatus> GetStatusAsync(GateEntity entity, CancellationToken ct = default) => Task.FromResult(CppStatus.NoCpp);

    public void Invalidate(GateEntity entity) => Invalidated.Add(entity);
}

/// <summary>
/// Without this a clip that has just been protected plays ungated until the lookup's five minutes run out. The service must
/// drop the cached decision on every write that can change a policy, and the proof is end to end: a real lookup, a real
/// edit service, a fake SAPI, no clock moved.
/// </summary>
public class CppInvalidationTests
{
    private const string Cpp = "545179f0-1503-46d6-9cc7-041fab63fc48";

    private static (ClipEditService Service, EditRig Rig) Create(ICppLookup cpp)
    {
        var rig = new EditRig();
        return (new ClipEditService(rig.Client, rig.Settings, rig.State, rig.Playouts, cpp), rig);
    }

    [Fact]
    public async Task Setting_a_policy_on_a_clip_makes_the_gate_see_it_at_once_not_after_five_minutes()
    {
        var rig = new EditRig();
        rig.AddClip(42);
        var lookup = new SapiCppLookup(rig.Client, rig.Time);
        var service = new ClipEditService(rig.Client, rig.Settings, rig.State, rig.Playouts, lookup);

        Assert.Equal(CppStatus.NoCpp, await lookup.GetStatusAsync(GateEntity.Clip(42))); // cached now
        EditOutcome<ClipEditResponseModel> saved = await service.UpdateAsync(42, new ClipUpdateRequestModel { Cpp = Cpp }, default);

        Assert.True(saved.Ok);
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(GateEntity.Clip(42)));
    }

    [Fact]
    public async Task Without_the_invalidation_the_stale_decision_would_stay_proving_the_test_has_teeth()
    {
        var rig = new EditRig();
        rig.AddClip(42);
        var lookup = new SapiCppLookup(rig.Client, rig.Time);
        var service = new ClipEditService(rig.Client, rig.Settings, rig.State, rig.Playouts); // no lookup wired

        await lookup.GetStatusAsync(GateEntity.Clip(42));
        await service.UpdateAsync(42, new ClipUpdateRequestModel { Cpp = Cpp }, default);

        Assert.Equal(CppStatus.NoCpp, await lookup.GetStatusAsync(GateEntity.Clip(42)));
    }

    [Fact]
    public async Task Clearing_a_policy_is_seen_at_once_too()
    {
        var rig = new EditRig();
        rig.AddClip(42, $$"""{"id":"42","title":"umbraco-poc-test-42","cpp":"{{Cpp}}","status":"published","thumbnails":[],"subtitles":[]}""");
        var lookup = new SapiCppLookup(rig.Client, rig.Time);
        var service = new ClipEditService(rig.Client, rig.Settings, rig.State, rig.Playouts, lookup);

        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(GateEntity.Clip(42)));
        await service.UpdateAsync(42, new ClipUpdateRequestModel { Cpp = "" }, default);

        Assert.Equal(CppStatus.NoCpp, await lookup.GetStatusAsync(GateEntity.Clip(42)));
    }

    [Fact]
    public async Task The_bulk_policy_action_invalidates_each_clip_it_writes()
    {
        var rig = new EditRig();
        rig.AddClip(1);
        rig.AddClip(2);
        var lookup = new SapiCppLookup(rig.Client, rig.Time);
        var service = new ClipEditService(rig.Client, rig.Settings, rig.State, rig.Playouts, lookup);
        await lookup.GetStatusAsync(GateEntity.Clip(1));
        await lookup.GetStatusAsync(GateEntity.Clip(2));

        EditOutcome<BulkResultResponseModel> result = await service.BulkAsync("cpp", [1, 2], Cpp, default);

        Assert.Equal(2, result.Value!.Success);
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(GateEntity.Clip(1)));
        Assert.Equal(CppStatus.HasCpp, await lookup.GetStatusAsync(GateEntity.Clip(2)));
    }

    [Fact]
    public async Task An_update_that_carries_a_policy_invalidates_exactly_that_clip()
    {
        var recording = new RecordingCpp();
        var (service, rig) = Create(recording);
        rig.AddClip(42);

        await service.UpdateAsync(42, new ClipUpdateRequestModel { Cpp = Cpp }, default);

        Assert.Equal([GateEntity.Clip(42)], recording.Invalidated);
    }

    [Fact]
    public async Task An_update_without_a_policy_leaves_the_cache_alone()
    {
        var recording = new RecordingCpp();
        var (service, rig) = Create(recording);
        rig.AddClip(42);

        await service.UpdateAsync(42, new ClipUpdateRequestModel { Title = "new title" }, default);

        Assert.Empty(recording.Invalidated);
    }

    [Fact]
    public async Task A_write_whose_answer_failed_still_drops_the_decision_because_it_may_have_gone_through()
    {
        var recording = new RecordingCpp();
        var (service, rig) = Create(recording);
        rig.AddClip(42);
        rig.PutAnswer = _ => Sapi.SapiResult.Failure(0, "Could not reach Blue Billywig");

        EditOutcome<ClipEditResponseModel> outcome = await service.UpdateAsync(42, new ClipUpdateRequestModel { Cpp = Cpp }, default);

        Assert.False(outcome.Ok);
        Assert.Equal([GateEntity.Clip(42)], recording.Invalidated);
    }

    [Theory]
    [InlineData("cpp", true)]
    [InlineData("status", false)]
    [InlineData("folder", false)]
    public async Task Only_the_policy_bulk_action_invalidates(string action, bool invalidates)
    {
        var recording = new RecordingCpp();
        var (service, rig) = Create(recording);
        rig.AddClip(1);
        string value = action switch { "cpp" => Cpp, "status" => "published", _ => "" };

        await service.BulkAsync(action, [1], value, default);

        Assert.Equal(invalidates, recording.Invalidated.Contains(GateEntity.Clip(1)));
    }

    [Fact]
    public async Task Deleting_a_clip_drops_its_decision_so_the_gate_does_not_show_a_deleted_clip_as_unprotected()
    {
        var recording = new RecordingCpp();
        var (service, rig) = Create(recording);
        rig.AddClip(1);

        await service.BulkAsync("delete", [1], null, default);

        Assert.Equal([GateEntity.Clip(1)], recording.Invalidated);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.Extensions.Caching.Memory;

namespace BlueBillywig.Umbraco.Tests;

internal sealed class FakePlayouts : IClipPlayoutStore
{
    public Dictionary<long, string> Values { get; } = new();

    public string? Get(long clipId) => Values.TryGetValue(clipId, out string? v) ? v : null;

    public void Set(long clipId, string? playout)
    {
        if (string.IsNullOrEmpty(playout)) Values.Remove(clipId);
        else Values[clipId] = playout;
    }
}

/// <summary>A small stand-in for SAPI: clips by id that PUT merges into, DELETE removes and GET reads back. Anything else is answered from <c>Client.Responses</c>.</summary>
internal sealed class EditRig
{
    public const string Groups = """
        {"items":[
          {"id":"2","title":"werkt","entity":["mediaclip"],"customFields":{"mediaclip":[{"name":"colour","label":"Colour","type":"string","itype":"string"},{"name":"Notes","type":"text","itype":"text"}]}},
          {"id":"3","title":"scalar","entity":"mediaclip","customFields":{"other":[{"name":"scalar_field"}]}},
          {"id":"4","title":"for playlists","entity":["cliplist"],"customFields":{"cliplist":[{"name":"not_for_clips"}]}},
          {"id":"5","title":"empty","entity":["mediaclip"],"customFields":{"mediaclip":[]}}
        ]}
        """;

    public FakeClient Client { get; } = new();
    public FakeSettings Settings { get; } = new();
    public FakeTime Time { get; } = new();
    public ThumbnailState State { get; }
    public FakePlayouts Playouts { get; } = new();
    public ClipEditService Service { get; }
    public Dictionary<long, JsonObject> Clips { get; } = new();

    /// <summary>When set, answers every PUT to a clip instead of the merge.</summary>
    public Func<long, SapiResult>? PutAnswer { get; set; }

    /// <summary>Statuses SAPI answers 200 for and ignores, like "archived" and "weird" on bb.dev.</summary>
    public HashSet<string> IgnoredStatuses { get; } = [];

    /// <summary>SAPI answers a subtitle PUT with 200 and changes nothing, as bb.dev does for the default flag.</summary>
    public bool IgnoreSubtitleDefault { get; set; }

    /// <summary>bb.dev: an empty embargo date is ignored (200, no change) and a non-date turns into the past marker. <see cref="EmbargoNeverClears"/> keeps both from working.</summary>
    public bool EmbargoIgnoresEmpty { get; set; }

    public bool EmbargoNeverClears { get; set; }

    public EditRig()
    {
        State = new ThumbnailState(new MemoryCache(new MemoryCacheOptions()), Time);
        Client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        Client.Json("/sapi/customfieldgroup", Groups);
        Client.Json("/sapi/language", """{"items":[{"id":"35","name":"Dansk","isocode":"da"},{"id":"13","name":"English","languagename":"English (EN)","isocode":"en"},{"name":"broken"}]}""");
        Client.Handler = Handle;
        Service = new ClipEditService(Client, Settings, State, Playouts);
    }

    public JsonObject AddClip(long id, string? json = null)
    {
        JsonObject clip = JsonNode.Parse(json ?? $$"""{"id":"{{id}}","title":"umbraco-poc-test-{{id}}","description":"d","cat":["a"],"status":"published","width":1920,"height":1080,"thumbnails":[],"subtitles":[]}""")!.AsObject();
        Clips[id] = clip;
        return clip;
    }

    public IEnumerable<(string Method, string Path, SapiRequest? Request)> Writes => Client.Calls.Where(c => c.Method is "PUT" or "POST" or "DELETE");

    public JsonObject PutBody(int index = 0) => (JsonObject)JsonSerializer.SerializeToNode(Writes.Where(w => w.Method == "PUT").ElementAt(index).Request!.Json)!;

    private SapiResult Handle(string method, string path, SapiRequest? request)
    {
        if (path.StartsWith("/sapi/subtitle/", StringComparison.Ordinal) && method == "PUT" && !IgnoreSubtitleDefault)
        {
            // The default flag: the one that is set wins, a cleared one is cleared.
            long subtitleId = long.Parse(path["/sapi/subtitle/".Length..]);
            JsonObject sent = JsonSerializer.SerializeToNode(request!.Json)!.AsObject();
            foreach (JsonObject sub in Clips.Values.SelectMany(c => (c["subtitles"] as JsonArray ?? []).OfType<JsonObject>()).Where(x => long.Parse((string)x["id"]!) == subtitleId))
            {
                sub["default"] = (string?)sent["default"] == "true";
            }
        }

        if (!path.StartsWith("/sapi/mediaclip/", StringComparison.Ordinal))
        {
            return method == "GET" ? SapiResult.Failure(404, "not found") : FakeClient.Ok("""{"id":"1"}""");
        }

        long id = long.Parse(path["/sapi/mediaclip/".Length..]);
        switch (method)
        {
            case "GET":
                return Clips.TryGetValue(id, out JsonObject? c) ? FakeClient.Ok(c.ToJsonString()) : SapiResult.Failure(404, "SAPI request failed (404)");
            case "PUT":
                if (PutAnswer is not null) return PutAnswer(id);
                // SAPI re-creates a deleted clip on PUT. The fake does too, so a test notices a PUT that should not have happened.
                JsonObject clip = Clips.TryGetValue(id, out JsonObject? existing) ? existing : (Clips[id] = new JsonObject { ["id"] = id.ToString() });
                foreach (KeyValuePair<string, JsonNode?> p in JsonSerializer.SerializeToNode(request!.Json)!.AsObject())
                {
                    if (p.Key == "status" && IgnoredStatuses.Contains((string?)p.Value ?? ""))
                    {
                        continue;
                    }

                    if (p.Key is "embargoFrom" or "embargoTo" && (EmbargoIgnoresEmpty || EmbargoNeverClears))
                    {
                        string written = (string?)p.Value ?? "";
                        if (written.Length == 0 || EmbargoNeverClears && !written.StartsWith("20", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (written == "0")
                        {
                            clip[p.Key] = "2001-12-02T23:00:00Z";
                            continue;
                        }
                    }

                    clip[p.Key] = p.Value?.DeepClone();
                }

                return FakeClient.Ok(clip.ToJsonString());
            case "DELETE":
                Clips.Remove(id);
                return FakeClient.Ok("""{"type":"Response","code":"200","error":false}""");
            default:
                return SapiResult.Failure(405, "no");
        }
    }
}

/// <summary>
/// Ports of the plugin's UpdateClipMetadataAllowlistTest, CustomFieldGroupsEntityTest, MaintSafeBatchTest and
/// ThumbnailAjaxHandlersTest, plus the checks the plugin's own tests could not make because its SAPI client was a stub.
/// </summary>
public class ClipEditServiceTests
{
    private const string Cpp = "545179f0-1503-46d6-9cc7-041fab63fc48";

    // ---- update ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_sends_only_the_fields_that_were_sent_in_one_put()
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Title = "New <b>title</b>", Tags = ["x", "X", "y,z"], Status = "draft", Cpp = Cpp, FolderCode = "abc", EmbargoFrom = "2030-01-01T10:00:00Z", EmbargoTo = "" }, default);

        Assert.True(result.Ok);
        Assert.Equal("New title", result.Value!.Title);
        Assert.Equal("d", result.Value.Description); // not sent, not blanked
        Assert.Single(rig.Writes);
        JsonObject body = rig.PutBody();
        Assert.Equal("7", (string?)body["id"]);
        Assert.Equal("New title", (string?)body["title"]);
        Assert.Equal(["x", "y z"], body["cat"]!.AsArray().Select(t => (string?)t));
        Assert.Equal("draft", (string?)body["status"]);
        Assert.Equal(Cpp, (string?)body["cpp"]);
        Assert.Equal("abc", (string?)body["foldercode"]);
        Assert.Equal("", (string?)body["embargoTo"]);
        Assert.False(body.ContainsKey("description"));
        Assert.False(body.ContainsKey("languageid"));
    }

    [Fact]
    public async Task Publish_now_from_the_embed_editor_banner_sends_the_status_and_nothing_else()
    {
        // The banner's "Publish now" (the plugin's update_clip_status with a status) reuses the ordinary update.
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Status = "published" }, default);

        Assert.True(result.Ok);
        JsonObject body = rig.PutBody();
        Assert.Equal(["id", "status"], body.Select(p => p.Key).Order().ToArray());
        Assert.Equal("published", (string?)body["status"]);
    }

    [Fact]
    public async Task Remove_embargo_from_the_banner_clears_both_dates_and_nothing_else()
    {
        // The banner's "Remove embargo" (the plugin's update_clip_status with empty dates) is the ordinary update with both
        // dates empty: no extra endpoint, and the same Library policy as any other metadata edit.
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { EmbargoFrom = "", EmbargoTo = "" }, default);

        Assert.True(result.Ok);
        JsonObject body = rig.PutBody();
        Assert.Equal(["embargoFrom", "embargoTo", "id"], body.Select(p => p.Key).Order().ToArray());
        Assert.Equal("", (string?)body["embargoFrom"]);
        Assert.Equal("", (string?)body["embargoTo"]);
    }

    [Fact]
    public async Task Remove_embargo_writes_the_fallback_when_sapi_ignores_empty_dates_and_reports_the_cleared_clip()
    {
        // bb.dev: "" is answered 200 and ignored; the embargo only goes with a non-date. The read-back decides.
        var rig = new EditRig { EmbargoIgnoresEmpty = true };
        rig.AddClip(7, """{"id":"7","title":"t","status":"published","embargoFrom":"2030-01-01T10:00:00Z","embargoTo":"2030-02-01T10:00:00Z"}""");

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { EmbargoFrom = "", EmbargoTo = "" }, default);

        Assert.True(result.Ok);
        Assert.Null(result.Value!.EmbargoFrom);
        Assert.Null(result.Value.EmbargoTo);
        Assert.Equal("", (string?)rig.PutBody(0)["embargoFrom"]);
        Assert.Equal(ClipEditService.EmbargoClearFallback, (string?)rig.PutBody(1)["embargoFrom"]);
        Assert.Equal(ClipEditService.EmbargoClearFallback, (string?)rig.PutBody(1)["embargoTo"]);
    }

    [Fact]
    public async Task Remove_embargo_is_a_failure_when_the_read_back_still_shows_the_dates()
    {
        var rig = new EditRig { EmbargoNeverClears = true };
        rig.AddClip(7, """{"id":"7","title":"t","status":"published","embargoFrom":"2030-01-01T10:00:00Z","embargoTo":"2030-02-01T10:00:00Z"}""");

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { EmbargoFrom = "", EmbargoTo = "" }, default);

        Assert.False(result.Ok);
        Assert.Equal(502, result.Failure!.StatusCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_platform_that_clears_on_the_empty_value_gets_no_second_write()
    {
        var rig = new EditRig();
        rig.AddClip(7, """{"id":"7","title":"t","status":"published","embargoFrom":"2030-01-01T10:00:00Z","embargoTo":"2030-02-01T10:00:00Z"}""");

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { EmbargoFrom = "", EmbargoTo = "" }, default);

        Assert.True(result.Ok);
        Assert.Single(rig.Writes, w => w.Method == "PUT");
    }

    [Theory]
    [InlineData("weird")]
    [InlineData("")]
    [InlineData("PUBLISHED")]
    public async Task A_status_outside_the_allowlist_is_a_400_and_nothing_is_sent(string status)
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Status = status }, default);

        Assert.Equal(400, result.Status);
        Assert.Equal("Invalid status value", result.Error);
        Assert.Empty(rig.Client.Calls);
    }

    [Theory]
    [InlineData("70")]
    [InlineData("not-a-uuid")]
    public async Task A_cpp_that_is_not_a_uuid_is_a_400_and_nothing_is_sent(string cpp)
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Cpp = cpp }, default);

        Assert.Equal(400, result.Status);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task A_status_that_sapi_ignores_is_a_failure_while_the_other_fields_stay_saved()
    {
        // Checked against bb.dev: PUT status=archived answers 200 and the clip keeps its status.
        var rig = new EditRig();
        rig.IgnoredStatuses.Add("archived");
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Title = "kept", Status = "archived" }, default);

        Assert.False(result.Ok);
        Assert.Equal(502, result.Failure!.StatusCode);
        Assert.Equal("published", (string?)rig.Clips[7]["status"]);
        Assert.Equal("kept", (string?)rig.Clips[7]["title"]); // the rest of the save did go through
        Assert.Equal(1, rig.Client.Cleared);
    }

    [Fact]
    public async Task A_status_that_sapi_takes_is_a_success()
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Status = "draft" }, default);

        Assert.True(result.Ok);
        Assert.Equal("draft", result.Value!.Status);
    }

    [Fact]
    public async Task A_bulk_status_that_sapi_ignores_is_reported_as_a_failure_per_clip()
    {
        var rig = new EditRig();
        rig.IgnoredStatuses.Add("archived");
        rig.AddClip(1);
        rig.AddClip(2);

        EditOutcome<BulkResultResponseModel> result = await rig.Service.BulkAsync("status", [1, 2], "archived", default);

        Assert.Equal(0, result.Value!.Success);
        Assert.Equal(2, result.Value.Failed);

        EditOutcome<BulkResultResponseModel> ok = await rig.Service.BulkAsync("status", [1, 2], "draft", default);
        Assert.Equal(2, ok.Value!.Success);
    }

    [Fact]
    public async Task An_empty_cpp_is_sent_to_clear_it()
    {
        var rig = new EditRig();
        rig.AddClip(7).AsObject()["cpp"] = Cpp;

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Cpp = "" }, default);

        Assert.True(result.Ok);
        Assert.Equal("", (string?)rig.PutBody()["cpp"]);
        Assert.Null(result.Value!.Cpp);
    }

    [Theory]
    [MemberData(nameof(BadInputs))]
    public async Task Other_bad_input_is_a_400_before_any_sapi_call(ClipUpdateRequestModel request)
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, request, default);

        Assert.Equal(400, result.Status);
        Assert.Empty(rig.Client.Calls);
    }

    public static IEnumerable<object[]> BadInputs() =>
    [
        [new ClipUpdateRequestModel { FolderCode = "a/b" }],
        [new ClipUpdateRequestModel { EmbargoFrom = "tomorrow" }],
        [new ClipUpdateRequestModel { EmbargoTo = "2030-13-01'; x" }],
        [new ClipUpdateRequestModel { Playout = "../../sapi/admintoken" }],
        [new ClipUpdateRequestModel { Title = "   " }],
        [new ClipUpdateRequestModel { Language = "<script>" }],
        [new ClipUpdateRequestModel()],
    ];

    [Fact]
    public async Task A_language_must_exist_in_the_publications_language_list()
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> unknown = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Language = "xx" }, default);
        Assert.Equal(400, unknown.Status);
        Assert.Empty(rig.Writes);

        EditOutcome<ClipEditResponseModel> known = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Language = "EN" }, default);
        Assert.True(known.Ok);
        Assert.Equal("13", (string?)rig.PutBody()["languageid"]);
    }

    [Fact]
    public async Task The_playout_is_kept_here_and_never_sent_to_sapi()
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Playout = "my-playout" }, default);

        Assert.True(result.Ok);
        Assert.Empty(rig.Writes);
        Assert.Equal("my-playout", rig.Playouts.Get(7));
        Assert.Equal("my-playout", result.Value!.Playout);

        await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Playout = "" }, default);
        Assert.Null(rig.Playouts.Get(7));
    }

    [Fact]
    public async Task Custom_fields_are_written_in_the_same_put_under_the_fields_real_name()
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Title = "t", CustomFields = new Dictionary<string, string> { ["colour"] = "red <i>x</i>", ["notes"] = "n", ["scalar_field"] = "s" } }, default);

        Assert.True(result.Ok);
        Assert.Single(rig.Writes); // the plugin sends a second PUT for these
        JsonObject body = rig.PutBody();
        Assert.Equal("red x", (string?)body["colour"]);
        Assert.Equal("n", (string?)body["Notes"]); // written under the name SAPI defines, not the lower-cased key
        Assert.Equal("s", (string?)body["scalar_field"]);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("cpp")]
    [InlineData("foldercode")]
    [InlineData("id")]
    [InlineData("not_for_clips")]
    [InlineData("nope")]
    public async Task A_custom_field_that_sapi_does_not_define_for_clips_is_a_422_and_nothing_is_written(string key)
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Title = "fine", CustomFields = new Dictionary<string, string> { ["colour"] = "red", [key] = "x" } }, default);

        Assert.Equal(422, result.Status);
        Assert.Equal([key], result.Dropped);
        Assert.Contains(key, result.Error);
        Assert.Empty(rig.Writes); // even the valid fields of the same save are not written
    }

    [Fact]
    public async Task When_the_definitions_cannot_be_fetched_the_save_is_a_502_not_a_silent_drop()
    {
        var rig = new EditRig();
        rig.AddClip(7);
        rig.Client.Responses["/sapi/customfieldgroup"] = SapiResult.Failure(500, "SAPI request failed (500)");

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { CustomFields = new Dictionary<string, string> { ["colour"] = "red" } }, default);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Failure!.StatusCode);
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public void Custom_field_groups_accept_a_scalar_entity_and_skip_other_entities()
    {
        // CustomFieldGroupsEntityTest: SAPI may return `entity` as a string; in_array on a string was a fatal TypeError in PHP 8.
        IReadOnlyList<CustomFieldGroupResponseModel> groups = CustomFieldDefinitions.ParseGroups(JsonNode.Parse(EditRig.Groups)!["items"]);

        Assert.Equal(["werkt", "scalar"], groups.Select(g => g.Title)); // playlist-only and empty groups are gone
        Assert.Equal(["colour", "Notes"], groups[0].Fields.Select(f => f.Name));
        Assert.Equal("Colour", groups[0].Fields[0].Label);
        Assert.Equal("Notes", groups[0].Fields[1].Label); // label falls back to the name
        Assert.Equal("text", groups[0].Fields[1].IType);
        Assert.Equal(["scalar_field"], groups[1].Fields.Select(f => f.Name)); // fields under the first entity key when mediaclip has none
        Assert.Empty(CustomFieldDefinitions.ParseGroups(null));
        Assert.Empty(CustomFieldDefinitions.ParseGroups(JsonNode.Parse("""[{"entity":42,"customFields":"x"},{"entity":null}]""")));
    }

    [Fact]
    public void A_group_whose_custom_fields_is_an_empty_list_instead_of_an_object_is_skipped()
    {
        // Seen on bb.dev with custom fields switched on: PHP encodes an empty map as [], and indexing that threw a 500.
        JsonNode items = JsonNode.Parse("""[{"id":"9","entity":["mediaclip"],"customFields":[]},{"id":"10","entity":["mediaclip"],"customFields":null},{"id":"11","entity":"mediaclip","customFields":"x"},{"id":"12","entity":["mediaclip"],"customFields":{"mediaclip":[{"name":"ok"}]}}]""")!;

        IReadOnlyList<CustomFieldGroupResponseModel> groups = CustomFieldDefinitions.ParseGroups(items);

        Assert.Equal([12L], groups.Select(g => g.Id));
    }

    [Fact]
    public async Task A_deleted_clip_is_never_written_to()
    {
        // SAPI answers a PUT to a deleted id with 200 and creates an empty clip (checked against bb.dev). The edit
        // reads the clip first, so the PUT is never sent.
        var rig = new EditRig();

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(99, new ClipUpdateRequestModel { Status = "draft" }, default);

        Assert.False(result.Ok);
        Assert.Equal(404, result.Failure!.StatusCode);
        Assert.Empty(rig.Writes);
        Assert.False(rig.Clips.ContainsKey(99));
    }

    [Theory]
    [MemberData(nameof(RejectedPuts))]
    public async Task A_put_that_sapi_rejects_is_a_failure_never_a_silent_success(SapiResult answer)
    {
        var rig = new EditRig { PutAnswer = _ => answer };
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Title = "t" }, default);

        Assert.False(result.Ok);
        Assert.NotNull(result.Failure);
        Assert.DoesNotContain("secret", result.Failure!.Error ?? "");
        Assert.Equal(0, rig.Client.Cleared); // nothing changed, so nothing is invalidated
    }

    public static IEnumerable<object[]> RejectedPuts() =>
    [
        [SapiResult.Failure(500, "SAPI request failed (500)")],
        [SapiResult.Failure(0, "Could not reach Blue Billywig")],
        [FakeClient.Ok("""{"Error":"internal path /srv/x"}""")],
        [FakeClient.Ok("""{"error":true,"body":"internal path /srv/x"}""")],
        [SapiResult.Success(200, null, "")],
        [FakeClient.Ok("[]")],
    ];

    [Fact]
    public async Task A_successful_update_versions_the_player_url_and_clears_the_cache()
    {
        // The title-edit bug of the plugin: a browser kept the cached player data, so the new title did not show.
        var rig = new EditRig();
        rig.AddClip(7);
        string html = "<script src=\"https://bb.dev.ovp.bluebillywig.eu/p/default/c/7.js\"></script>";
        Assert.Equal(html, rig.State.VersionEmbedHtml(html, 7));

        await rig.Service.UpdateAsync(7, new ClipUpdateRequestModel { Title = "renamed" }, default);

        Assert.Contains(".js?v=", rig.State.VersionEmbedHtml(html, 7));
        Assert.Equal(1, rig.Client.Cleared);
    }

    [Fact]
    public async Task The_editor_reads_custom_field_values_and_treats_null_as_empty()
    {
        var rig = new EditRig();
        rig.Settings.Value = rig.Settings.Value with { EnableCustomFields = true };
        rig.AddClip(7, """{"id":"7","title":"t","colour":null,"Notes":["a","b"],"status":"published","isocode":"da","embargoFrom":"2030-01-01T10:00:00Z"}""");

        EditOutcome<ClipEditResponseModel> result = await rig.Service.GetAsync(7, default);

        Assert.True(result.Ok);
        Assert.True(result.Value!.CustomFieldsEnabled);
        Assert.Equal("", result.Value.CustomFields["colour"]); // not the text "null"
        Assert.Equal("a, b", result.Value.CustomFields["Notes"]);
        Assert.Equal("da", result.Value.Language);
        Assert.Equal("2030-01-01T10:00:00Z", result.Value.EmbargoFrom);
    }

    [Fact]
    public async Task The_value_sapi_leaves_after_an_embargo_is_cleared_is_shown_as_empty()
    {
        // bb.dev cannot clear an embargo; any non-date makes both fields 2001-12-02T23:00:00Z, a window in the past.
        var rig = new EditRig();
        rig.AddClip(7, """{"id":"7","title":"t","embargoFrom":"2001-12-02T23:00:00Z","embargoTo":"2001-12-02T23:00:00Z"}""");

        EditOutcome<ClipEditResponseModel> result = await rig.Service.GetAsync(7, default);

        Assert.Null(result.Value!.EmbargoFrom);
        Assert.Null(result.Value.EmbargoTo);
        Assert.Equal("2031-01-01T10:00:00Z", ClipEditRules.EmbargoOrNull("2031-01-01T10:00:00Z"));
    }

    [Fact]
    public async Task Custom_fields_are_not_read_when_the_setting_is_off()
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<ClipEditResponseModel> result = await rig.Service.GetAsync(7, default);

        Assert.False(result.Value!.CustomFieldsEnabled);
        Assert.Empty(result.Value.CustomFields);
        Assert.DoesNotContain("/sapi/customfieldgroup", rig.Client.Requests);
    }

    // ---- bulk -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_is_capped_at_fifty_clips_and_a_larger_request_does_nothing()
    {
        var rig = new EditRig();

        EditOutcome<BulkResultResponseModel> result = await rig.Service.BulkAsync("status", Enumerable.Range(1, 51).Select(i => (long)i).ToList(), "draft", default);

        Assert.Equal(400, result.Status);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task Bulk_counts_distinct_ids_and_handles_each_once()
    {
        var rig = new EditRig();
        rig.AddClip(1);
        rig.AddClip(2);

        EditOutcome<BulkResultResponseModel> result = await rig.Service.BulkAsync("status", [1, 1, 2, 2, 0, -5], "archived", default);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Success);
        Assert.Equal(2, rig.Writes.Count());
        Assert.All(rig.Writes, w => Assert.Equal("archived", (string?)((JsonObject)JsonSerializer.SerializeToNode(w.Request!.Json)!)["status"]));
    }

    [Theory]
    [InlineData("status", "weird")]
    [InlineData("status", "")]
    [InlineData("cpp", "70")]
    [InlineData("folder", "a b")]
    [InlineData("nothing", "x")]
    public async Task Bulk_values_are_validated_before_anything_is_sent(string action, string value)
    {
        var rig = new EditRig();
        rig.AddClip(1);

        EditOutcome<BulkResultResponseModel> result = await rig.Service.BulkAsync(action, [1], value, default);

        Assert.Equal(400, result.Status);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task A_partial_failure_is_reported_per_clip_and_does_not_stop_the_rest()
    {
        // MaintSafeBatchTest: one failing item does not abort the batch, and the counts add up.
        var rig = new EditRig();
        rig.AddClip(1);
        rig.AddClip(3);
        rig.PutAnswer = id => id == 3 ? SapiResult.Failure(500, "SAPI request failed (500)") : FakeClient.Ok($$"""{"id":"{{id}}"}""");

        // 2 does not exist: it must be reported, and must not be created by a PUT.
        EditOutcome<BulkResultResponseModel> result = await rig.Service.BulkAsync("folder", [1, 2, 3], "", default);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Value!.Success);
        Assert.Equal(2, result.Value.Failed);
        Assert.Equal([1L], result.Value.Succeeded);
        Assert.Equal([2L, 3L], result.Value.Errors.Select(e => e.Id));
        Assert.Equal("Clip not found", result.Value.Errors[0].Error);
        Assert.DoesNotContain("500", result.Value.Errors[1].Error); // no upstream detail
        Assert.DoesNotContain(rig.Writes, w => w.Path.EndsWith("/2"));
        Assert.False(rig.Clips.ContainsKey(2));
        Assert.Equal("", (string?)rig.PutBody()["foldercode"]); // empty means "move to root"
        Assert.Equal(1, rig.Client.Cleared);
    }

    [Fact]
    public async Task Bulk_delete_reports_a_missing_clip_instead_of_counting_it_as_deleted()
    {
        var rig = new EditRig();
        rig.AddClip(1);
        rig.AddClip(2);
        rig.Playouts.Set(1, "p");

        EditOutcome<BulkResultResponseModel> result = await rig.Service.BulkAsync("delete", [1, 77, 2], null, default);

        Assert.Equal(2, result.Value!.Success);
        Assert.Equal([77L], result.Value.Errors.Select(e => e.Id));
        Assert.Equal(["/sapi/mediaclip/1", "/sapi/mediaclip/2"], rig.Writes.Select(w => w.Path));
        Assert.Empty(rig.Clips);
        Assert.Null(rig.Playouts.Get(1));
    }

    [Fact]
    public async Task Bulk_without_credentials_is_a_failure_not_an_empty_success()
    {
        var rig = new EditRig();
        rig.Client.IsConnected = false;

        EditOutcome<BulkResultResponseModel> result = await rig.Service.BulkAsync("status", [1], "draft", default);

        Assert.False(result.Ok);
        Assert.Empty(rig.Client.Calls);
    }

    // ---- subtitles ------------------------------------------------------------------------------------------

    private const string Vtt = "WEBVTT\n\n00:00.000 --> 00:01.000\nHello\n";
    private const string Srt = "1\n00:00:00,000 --> 00:00:01,000\nHello\n";

    private static SubtitleUploadRequestModel Upload(string file, string content, string iso = "en") => new() { IsoCode = iso, Name = "English", FileName = file, Content = content };

    [Theory]
    [InlineData("notes.txt", Vtt, "Only .vtt and .srt files are supported")]
    [InlineData("noext", Vtt, "Only .vtt and .srt files are supported")]
    [InlineData("a.vtt", "hello world", "File content does not look like a valid VTT subtitle")]
    [InlineData("a.srt", Vtt, "File content does not look like a valid SRT subtitle")]
    [InlineData("a.vtt", "", "No subtitle file uploaded")]
    public async Task A_subtitle_must_be_a_vtt_or_srt_file_that_looks_like_one(string file, string content, string message)
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.UploadSubtitleAsync(7, Upload(file, content), default);

        Assert.Equal(400, result.Status);
        Assert.Equal(message, result.Error);
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public async Task A_subtitle_over_one_megabyte_is_refused_by_its_real_size()
    {
        var rig = new EditRig();
        rig.AddClip(7);
        string big = "WEBVTT\n\n" + new string('x', SubtitleRules.MaxBytes);

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.UploadSubtitleAsync(7, Upload("a.vtt", big), default);

        Assert.Equal(400, result.Status);
        Assert.Equal("Subtitle file too large (max 1 MB)", result.Error);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task A_subtitle_in_an_unknown_language_is_refused()
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.UploadSubtitleAsync(7, Upload("a.vtt", Vtt, "xx"), default);

        Assert.Equal(400, result.Status);
        Assert.Equal("Unknown language isocode: xx", result.Error);
        Assert.Empty(rig.Writes);
    }

    [Theory]
    [InlineData("a.vtt", Vtt)]
    [InlineData("A.SRT", Srt)]
    [InlineData("a.vtt", "﻿\n  " + Vtt)]
    public async Task A_valid_subtitle_is_one_post_with_the_text_in_the_body(string file, string content)
    {
        var rig = new EditRig();
        rig.AddClip(7);

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.UploadSubtitleAsync(7, new SubtitleUploadRequestModel { IsoCode = "da", Name = "Dansk <b>x</b>", Default = true, Publish = true, FileName = file, Content = content }, default);

        Assert.True(result.Ok);
        (string method, string path, SapiRequest? request) = Assert.Single(rig.Writes);
        Assert.Equal(("POST", "/sapi/subtitle"), (method, path));
        JsonObject body = (JsonObject)JsonSerializer.SerializeToNode(request!.Json)!;
        Assert.Equal("7", (string?)body["mediaclipid"]);
        Assert.Equal(35, (int?)body["languageid"]);
        Assert.Equal("Dansk x", (string?)body["name"]);
        Assert.True((bool)body["default"]!);
        Assert.Equal("published", (string?)body["status"]);
        Assert.Equal("subtitle", (string?)body["subtype"]);
        Assert.Equal(content, (string?)body["srt"]);
    }

    [Fact]
    public async Task A_subtitle_that_sapi_answers_with_an_error_body_is_a_failure()
    {
        var rig = new EditRig();
        rig.AddClip(7);
        rig.Client.Handler = (m, p, r) => m == "POST" ? FakeClient.Ok("""{"error":true,"body":"duplicate language"}""") : rig.Clips.TryGetValue(7, out JsonObject? c) ? FakeClient.Ok(c.ToJsonString()) : SapiResult.Failure(404, "x");

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.UploadSubtitleAsync(7, Upload("a.vtt", Vtt), default);

        Assert.False(result.Ok);
        Assert.DoesNotContain("duplicate", result.Failure!.Error);
    }

    private static EditRig WithSubtitles()
    {
        var rig = new EditRig();
        rig.AddClip(7, """{"id":"7","title":"t","subtitles":[{"id":"10","name":"English","isocode":"en","languagename":"English","default":true,"status":"published"},{"id":"11","name":"Dansk","isocode":"da","default":false,"status":"draft"}]}""");
        rig.AddClip(8, """{"id":"8","title":"other","subtitles":[{"id":"20","name":"foreign"}]}""");
        return rig;
    }

    [Fact]
    public async Task Subtitles_are_listed_from_the_clip()
    {
        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await WithSubtitles().Service.ListSubtitlesAsync(7, default);

        Assert.Equal([10L, 11L], result.Value!.Select(s => s.Id));
        Assert.True(result.Value![0].Default);
        Assert.False(result.Value[1].Default);
        Assert.Equal("da", result.Value[1].IsoCode);
    }

    [Fact]
    public async Task A_subtitle_is_deleted_only_through_the_clip_it_belongs_to()
    {
        // The plugin deletes whatever subtitle id it is sent; here an id of another clip is a 404 and nothing is deleted.
        EditRig rig = WithSubtitles();

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> foreign = await rig.Service.DeleteSubtitleAsync(7, 20, default);
        Assert.Equal(404, foreign.Status);
        Assert.Empty(rig.Writes);

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> own = await rig.Service.DeleteSubtitleAsync(7, 11, default);
        Assert.True(own.Ok);
        Assert.Equal("/sapi/subtitle/11", Assert.Single(rig.Writes).Path);
    }

    [Fact]
    public async Task Set_default_sets_the_new_one_then_clears_the_previous_one()
    {
        EditRig rig = WithSubtitles();

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.SetDefaultSubtitleAsync(7, 11, default);

        Assert.True(result.Ok);
        Assert.Equal(["/sapi/subtitle/11", "/sapi/subtitle/10"], rig.Writes.Select(w => w.Path));
        Assert.Equal("true", (string?)((JsonObject)JsonSerializer.SerializeToNode(rig.Writes.First().Request!.Json)!)["default"]);
        Assert.Equal("false", (string?)((JsonObject)JsonSerializer.SerializeToNode(rig.Writes.Last().Request!.Json)!)["default"]);
    }

    [Fact]
    public async Task A_default_that_sapi_acknowledges_but_does_not_apply_is_a_failure()
    {
        // bb.dev answers 200 "metadata has been updated" and the flag stays on the subtitle of the publication's default language.
        EditRig rig = WithSubtitles();
        rig.IgnoreSubtitleDefault = true;

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.SetDefaultSubtitleAsync(7, 11, default);

        Assert.False(result.Ok);
        Assert.Equal(502, result.Failure!.StatusCode);
    }

    [Fact]
    public async Task Set_default_for_a_subtitle_of_another_clip_is_a_404_and_writes_nothing()
    {
        EditRig rig = WithSubtitles();

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.SetDefaultSubtitleAsync(7, 20, default);

        Assert.Equal(404, result.Status);
        Assert.Empty(rig.Writes);
    }

    [Fact]
    public async Task A_partly_updated_default_is_a_502()
    {
        EditRig rig = WithSubtitles();
        rig.Client.Handler = (m, p, r) => m switch
        {
            "GET" => FakeClient.Ok(rig.Clips[7].ToJsonString()),
            _ => p.EndsWith("/10") ? SapiResult.Failure(500, "SAPI request failed (500)") : FakeClient.Ok("""{"id":"11"}"""),
        };

        EditOutcome<IReadOnlyList<SubtitleResponseModel>> result = await rig.Service.SetDefaultSubtitleAsync(7, 11, default);

        Assert.False(result.Ok);
        Assert.Equal(502, result.Failure!.StatusCode);
    }

    // ---- thumbnails -----------------------------------------------------------------------------------------

    private static string Png(int w = 1920, int h = 1080) => Img.Uri("png", Img.Png(w, h));

    private static EditRig WithThumbnails()
    {
        var rig = new EditRig();
        rig.AddClip(7, """{"id":"7","title":"t","width":1920,"height":1080,"updateddate":"2026-11-04T10:00:00Z","thumbnails":[{"src":"/a.jpg","main":true,"crops":{"landscape":{"main":true}}},{"src":"/b.jpg","main":false,"crops":{"landscape":{"main":false}}}]}""");
        return rig;
    }

    [Fact]
    public async Task An_invalid_image_is_a_400_and_nothing_is_written()
    {
        EditRig rig = WithThumbnails();

        foreach (string data in new[] { Img.Uri("gif", Img.Gif(16, 9)), "nonsense", Png(100, 100) })
        {
            EditOutcome<ThumbnailsResponseModel> result = await rig.Service.UploadThumbnailAsync(7, data, default);
            Assert.Equal(400, result.Status);
        }

        Assert.Empty(rig.Writes);
    }

    [Fact]
    public async Task An_image_over_ten_megabytes_is_refused_before_any_sapi_call()
    {
        EditRig rig = WithThumbnails();

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.UploadThumbnailAsync(7, "data:image/png;base64," + new string('A', ThumbnailValidator.MaxDataLength), default);

        Assert.Equal(400, result.Status);
        Assert.Equal("Image too large (max 10 MB)", result.Error);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task An_upload_is_a_put_of_the_whole_list_with_the_new_image_as_main()
    {
        EditRig rig = WithThumbnails();
        rig.PutAnswer = _ => FakeClient.Ok("""{"id":"7","updateddate":"2026-11-04T11:00:00Z","thumbnails":[{"src":"/new.png","main":true,"crops":{"landscape":{"main":true}}},{"src":"/a.jpg","main":false,"crops":{"landscape":{"main":false}}},{"src":"/b.jpg","main":false,"crops":{"landscape":{"main":false}}}]}""");

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.UploadThumbnailAsync(7, Png(), default);

        Assert.True(result.Ok);
        JsonArray sent = rig.PutBody()["thumbnails"]!.AsArray();
        Assert.Equal(3, sent.Count);
        Assert.False((bool)sent[0]!["main"]!); // old main demoted in the video's own crop set
        Assert.True((bool)sent[2]!["main"]!);
        Assert.StartsWith("data:image/png;base64,", (string?)sent[2]!["data"]);
        Assert.Equal(["https://cdn.example/new.png", "https://cdn.example/a.jpg", "https://cdn.example/b.jpg"], result.Value!.Thumbnails.Select(t => t.Url));
        Assert.True(result.Value.Thumbnails[0].Main);
        Assert.Null(result.Value.Notice);
        // SAPI's read lags: the next edit builds on the list the write confirmed, not on the read.
        Assert.Equal("/new.png", rig.State.ClipMainSrc(7, rig.Clips[7]));
    }

    [Fact]
    public async Task An_upload_without_a_list_in_the_reply_counts_as_done_with_a_notice_and_keeps_the_remembered_list()
    {
        EditRig rig = WithThumbnails();
        rig.State.Remember(7, JsonNode.Parse("""[{"src":"/r.jpg","main":true}]""")!.AsArray());
        rig.PutAnswer = _ => FakeClient.Ok("""{"id":"7"}""");

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.UploadThumbnailAsync(7, Png(), default);

        Assert.True(result.Ok);
        Assert.NotNull(result.Value!.Notice);
        Assert.Contains("did not confirm", result.Value.Notice);
        Assert.Equal("/r.jpg", rig.State.ClipMainSrc(7, rig.Clips[7])); // unchanged
        // The upload built on the remembered list, not on the lagging read.
        Assert.Equal("/r.jpg", (string?)rig.PutBody()["thumbnails"]![0]!["src"]);
    }

    [Theory]
    [MemberData(nameof(RejectedPuts))]
    public async Task A_thumbnail_put_that_sapi_rejects_is_a_failure_and_changes_nothing(SapiResult answer)
    {
        // ThumbnailAjaxHandlersTest: a rejected PUT is a 502 and remembers nothing.
        EditRig rig = WithThumbnails();
        rig.PutAnswer = _ => answer;

        EditOutcome<ThumbnailsResponseModel> upload = await rig.Service.UploadThumbnailAsync(7, Png(), default);
        EditOutcome<ThumbnailsResponseModel> main = await rig.Service.SetMainThumbnailAsync(7, "/b.jpg", default);

        Assert.False(upload.Ok);
        Assert.False(main.Ok);
        Assert.NotNull(upload.Failure);
        Assert.Equal("/a.jpg", rig.State.ClipMainSrc(7, rig.Clips[7]));
        Assert.Equal(0, rig.Client.Cleared);
    }

    [Fact]
    public async Task Set_main_for_a_src_that_is_gone_is_a_409_and_writes_nothing()
    {
        EditRig rig = WithThumbnails();

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.SetMainThumbnailAsync(7, "/gone.jpg", default);

        Assert.Equal(409, result.Status);
        Assert.Empty(rig.Writes);
        Assert.Equal("/a.jpg", rig.State.ClipMainSrc(7, rig.Clips[7]));
    }

    [Fact]
    public async Task Set_main_finds_the_src_in_the_remembered_list_not_the_lagging_read()
    {
        EditRig rig = WithThumbnails();
        rig.State.Remember(7, JsonNode.Parse("""[{"src":"/a.jpg","main":true},{"src":"/c.jpg","main":false}]""")!.AsArray());
        rig.PutAnswer = _ => FakeClient.Ok("""{"id":"7","thumbnails":[{"src":"/a.jpg","main":false},{"src":"/c.jpg","main":true}]}""");

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.SetMainThumbnailAsync(7, "/c.jpg", default);

        Assert.True(result.Ok);
        Assert.Equal(["/a.jpg", "/c.jpg"], rig.PutBody()["thumbnails"]!.AsArray().Select(t => (string?)t!["src"]));
        Assert.Equal("/c.jpg", result.Value!.Thumbnails.Single(t => t.Main).Src);
    }

    [Fact]
    public async Task Set_main_moves_both_flags_and_remembers_the_list_of_the_reply()
    {
        EditRig rig = WithThumbnails();
        rig.PutAnswer = _ => FakeClient.Ok("""{"id":"7","updateddate":"2026-11-04T11:00:00Z","thumbnails":[{"src":"/a.jpg","main":false,"crops":{"landscape":{"main":false}}},{"src":"/b.jpg","main":true,"crops":{"landscape":{"main":true}}}]}""");

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.SetMainThumbnailAsync(7, "/b.jpg", default);

        Assert.True(result.Ok);
        JsonArray sent = rig.PutBody()["thumbnails"]!.AsArray();
        Assert.True((bool)sent[1]!["main"]!);
        Assert.True((bool)sent[1]!["crops"]!["landscape"]!["main"]!);
        Assert.False((bool)sent[0]!["crops"]!["landscape"]!["main"]!);
        Assert.Equal("/b.jpg", rig.State.ClipMainSrc(7, rig.Clips[7])); // the read still says /a.jpg
        Assert.Equal(1, rig.Client.Cleared);
    }

    [Fact]
    public async Task A_put_reply_that_carries_the_date_from_before_the_write_does_not_let_the_stale_read_win()
    {
        // Seen on bb.dev: the PUT answer has the new thumbnails but the entity's updateddate from before the write. That
        // date equals the one on the read the edit started from. The editor must show the new list, not the old one.
        EditRig rig = WithThumbnails();
        rig.PutAnswer = _ => FakeClient.Ok("""{"id":"7","updateddate":"2026-11-04T10:00:00Z","thumbnails":[{"src":"/a.jpg","main":false,"crops":{"landscape":{"main":false}}},{"src":"/b.jpg","main":true,"crops":{"landscape":{"main":true}}}]}""");

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.SetMainThumbnailAsync(7, "/b.jpg", default);

        Assert.Equal("/b.jpg", result.Value!.Thumbnails.Single(t => t.Main).Src);
        Assert.Equal("/b.jpg", rig.State.ClipMainSrc(7, rig.Clips[7]));
    }

    [Fact]
    public async Task Set_main_without_a_list_in_the_reply_remembers_the_list_that_was_sent()
    {
        EditRig rig = WithThumbnails();
        rig.PutAnswer = _ => FakeClient.Ok("""{"id":"7"}""");

        EditOutcome<ThumbnailsResponseModel> result = await rig.Service.SetMainThumbnailAsync(7, "/b.jpg", default);

        Assert.True(result.Ok);
        Assert.Null(result.Value!.Notice);
        Assert.Equal("/b.jpg", rig.State.ClipMainSrc(7, rig.Clips[7]));
    }

    [Fact]
    public async Task The_real_bb_dev_shape_with_webp_thumbnails_and_a_default_crop_set_can_be_uploaded_and_set_main()
    {
        var rig = new EditRig();
        rig.AddClip(7, """{"id":"7","title":"t","width":320,"height":180,"thumbnails":[{"src":"/a.webp","main":false,"crops":{"default":{"x":0,"y":0,"width":1,"height":1,"main":false}}},{"src":"/b.webp","main":false,"crops":{"default":{"x":0,"y":0,"width":1,"height":1,"main":false}}}]}""");

        await rig.Service.UploadThumbnailAsync(7, Png(320, 180), default);
        JsonArray sent = rig.PutBody()["thumbnails"]!.AsArray();
        Assert.Equal(3, sent.Count);
        Assert.True((bool)sent[2]!["main"]!);

        EditOutcome<ThumbnailsResponseModel> main = await rig.Service.SetMainThumbnailAsync(7, "/b.webp", default);
        Assert.True(main.Ok);
        JsonArray moved = rig.PutBody(1)["thumbnails"]!.AsArray();
        Assert.True((bool)moved.Single(t => ThumbnailLists.Src(t) == "/b.webp")!["main"]!);
        Assert.True((bool)moved.Single(t => ThumbnailLists.Src(t) == "/b.webp")!["crops"]!["default"]!["main"]!);
    }

    // ---- lookups --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tag_suggestions_match_a_prefix_without_case_and_list_each_tag_once()
    {
        var rig = new EditRig();
        string items = string.Join(',', Enumerable.Range(1, 30).Select(i => $$"""{"cat":["Alpha{{i:00}}","alpha{{i:00}}"," "]}""")) + ",{\"cat\":null},{\"cat\":[\"Beta\",\"bravo\"]}";
        rig.Client.Json("/sapi/mediaclip", "{\"items\":[" + items + "]}");

        EditOutcome<IReadOnlyList<string>> alpha = await rig.Service.SuggestTagsAsync("ALP", default);
        EditOutcome<IReadOnlyList<string>> b = await rig.Service.SuggestTagsAsync(" b", default);
        EditOutcome<IReadOnlyList<string>> all = await rig.Service.SuggestTagsAsync(null, default);

        Assert.Equal(20, alpha.Value!.Count); // capped
        Assert.Equal("Alpha01", alpha.Value[0]);
        Assert.All(alpha.Value, t => Assert.StartsWith("Alpha", t)); // first spelling wins
        Assert.Equal(["Beta", "bravo"], b.Value);
        Assert.Equal(20, all.Value!.Count);
        Assert.Equal("limit=500", string.Join(',', rig.Client.Calls.First(c => c.Path == "/sapi/mediaclip").Request!.Query!.Where(q => q.Key == "limit").Select(q => $"{q.Key}={q.Value}")));
    }

    [Fact]
    public async Task A_tag_lookup_that_fails_is_a_failure_not_an_empty_list()
    {
        var rig = new EditRig();
        rig.Client.Responses["/sapi/mediaclip"] = SapiResult.Failure(500, "SAPI request failed (500)");

        EditOutcome<IReadOnlyList<string>> result = await rig.Service.SuggestTagsAsync("a", default);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Languages_map_the_name_with_the_plugins_fallbacks_and_skip_broken_entries()
    {
        var rig = new EditRig();

        EditOutcome<IReadOnlyList<LanguageResponseModel>> result = await rig.Service.GetLanguagesAsync(default);

        Assert.Equal(["da", "en"], result.Value!.Select(l => l.IsoCode));
        Assert.Equal(["Dansk", "English (EN)"], result.Value!.Select(l => l.Name)); // languagename wins over name
    }

    [Fact]
    public async Task Policies_expose_only_the_code_name_and_status()
    {
        var rig = new EditRig();
        rig.Client.Json("/sapi/contentprotectionpolicy", $$"""{"items":[{"id":"70","code":"{{Cpp}}","name":"new","status":"active","description":"secret internal text","hideContent":false},{"id":"71","code":"bad","name":"x"},{"id":"72","code":"11111111-2222-3333-4444-555555555555","status":"active"}]}""");

        EditOutcome<IReadOnlyList<PolicyResponseModel>> result = await rig.Service.GetPoliciesAsync(default);

        Assert.Equal([Cpp, "11111111-2222-3333-4444-555555555555"], result.Value!.Select(p => p.Code));
        Assert.Equal("11111111-2222-3333-4444-555555555555", result.Value![1].Name); // name falls back to the code
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task Playouts_fall_back_to_default_when_none_are_valid()
    {
        var rig = new EditRig();
        rig.Client.Json("/sapi/playout/", """{"items":[{"name":"Bad","label":"../x"},{"name":"My playout","label":"my-playout"},{"label":"nameless"}]}""");

        EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>> result = await rig.Service.GetPlayoutsAsync(default);

        Assert.Equal(["my-playout", "nameless"], result.Value!.Select(p => p.Value));
        Assert.Equal("nameless", result.Value![1].Label);

        rig.Client.Json("/sapi/playout/", """{"items":[]}""");
        Assert.Equal("default", (await rig.Service.GetPlayoutsAsync(default)).Value!.Single().Value);
    }
}

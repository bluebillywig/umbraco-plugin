using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Cta;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests.Cta;

/// <summary>A small SAPI: clips with timelines, soft-deleted timelines, and an id counter. Records what was written.</summary>
internal sealed class FakeTimelineSapi
{
    public FakeClient Client { get; } = new();

    public Dictionary<long, JsonObject> Timelines { get; } = new();

    public Dictionary<long, List<long>> ClipTimelines { get; } = new() { [42] = [] };

    public double ClipLength { get; set; } = 60;

    /// <summary>When set, reading this timeline id fails.</summary>
    public long? FailTimelineRead { get; set; }

    public bool FailWrites { get; set; }

    public bool WritesWithoutId { get; set; }

    private long _next = 100;

    public FakeTimelineSapi()
    {
        Client.Handler = Handle;
    }

    public IEnumerable<(string Method, string Path, SapiRequest? Request)> Writes => Client.Calls.Where(c => c.Method != "GET");

    private static long Id(string path) => long.Parse(path[(path.LastIndexOf('/') + 1)..]);

    private SapiResult Handle(string method, string path, SapiRequest? request)
    {
        if (method == "GET" && path.StartsWith("/sapi/mediaclip/"))
        {
            long id = Id(path);
            if (!ClipTimelines.TryGetValue(id, out List<long>? ids))
            {
                return SapiResult.Failure(404, "not found");
            }

            var clip = new JsonObject { ["id"] = id.ToString(), ["length"] = ClipLength.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (request?.Query?.ContainsKey("expand") == true)
            {
                clip["timelines"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["id"] = i.ToString() }).ToArray());
            }

            return Wrap(clip);
        }

        if (method == "GET" && path.StartsWith("/sapi/timeline/"))
        {
            long id = Id(path);
            return FailTimelineRead == id || !Timelines.TryGetValue(id, out JsonObject? t) ? SapiResult.Failure(500, "boom") : Wrap(t);
        }

        if (method is "POST" or "PUT" && path.StartsWith("/sapi/timeline"))
        {
            if (FailWrites)
            {
                return SapiResult.Failure(500, "boom");
            }

            var body = (JsonObject)JsonNode.Parse(CtaTranslator.Serialize((JsonNode)request!.Json!))!;
            long id = method == "POST" ? _next++ : Id(path);
            body["id"] = id.ToString();
            Timelines[id] = body;
            if (method == "POST")
            {
                ClipTimelines[long.Parse((string)body["parentEntityId"]!)].Add(id);
            }

            if (WritesWithoutId)
            {
                return Wrap(new JsonObject { ["ok"] = true });
            }

            return Wrap(body);
        }

        if (method == "DELETE" && path.StartsWith("/sapi/timeline/"))
        {
            if (FailWrites)
            {
                return SapiResult.Failure(500, "boom");
            }

            Timelines[Id(path)]["status"] = "deleted";
            return Wrap(new JsonObject());
        }

        if (method == "POST" && path == "/sapi/commonclip")
        {
            return FailWrites ? SapiResult.Failure(500, "boom") : Wrap(new JsonObject { ["id"] = WritesWithoutId ? "abc" : "900" });
        }

        return SapiResult.Failure(404, "not found");
    }

    private static SapiResult Wrap(JsonNode node) => SapiResult.Success(200, node, node.ToJsonString());

    /// <summary>Puts a timeline on a clip the way another tool would.</summary>
    public long AddTimeline(long clipId, JsonObject timeline)
    {
        long id = _next++;
        timeline["id"] = id.ToString();
        Timelines[id] = timeline;
        ClipTimelines[clipId].Add(id);
        return id;
    }
}

internal sealed class NoThumbnails : IThumbnailState
{
    public List<long> Touched { get; } = [];

    public void Remember(long clipId, JsonArray thumbnails, string updated = "") { }

    public void Touch(long clipId) => Touched.Add(clipId);

    public void Clear() { }

    public ResolvedThumbnails ResolveClip(long clipId, JsonNode? clip) => throw new NotSupportedException();

    public ResolvedThumbnails Resolve(long clipId, JsonArray readThumbnails, string readUpdated = "") => throw new NotSupportedException();

    public string? ClipMainSrc(long clipId, JsonNode? clip) => null;

    public string VersionEmbedHtml(string html, long clipId) => html;
}

public class CtaServiceTests
{
    private static readonly CtaConfig Link = new() { Title = "Promo", Headline = "Big news", Body = "Details", ButtonLabel = "Go", Url = "https://example.com/offer", OpenInNewTab = true, EndAt = 0, Xpos = 25, Ypos = 75, Width = 50, Height = 12 };

    private static (CtaService Service, FakeTimelineSapi Sapi, FakeSettings Settings, NoThumbnails Thumbs) Create(bool enabled = true)
    {
        var sapi = new FakeTimelineSapi();
        var settings = new FakeSettings();
        settings.Value = settings.Value with { EnableCta = enabled };
        var thumbs = new NoThumbnails();
        return (new CtaService(sapi.Client, settings, thumbs, new SeqIds(), TimeProvider.System, NullLogger<CtaService>.Instance), sapi, settings, thumbs);
    }

    private static T Value<T>(EditOutcome<T> outcome) where T : class
    {
        Assert.True(outcome.Ok, outcome.Error ?? outcome.Failure?.Error);
        return outcome.Value!;
    }

    private static JsonObject Interactive() => JsonNode.Parse("""
        {"label":"Quiz","status":"published","widgets":[
          {"id":"w1","label":"Question","widgetdefinitiontype":"choice","statesets":[]},
          {"id":"w2","label":"Next","widgetdefinitiontype":"button","statesets":[]},
          {"id":"w3","label":"Result","widgetdefinitiontype":"textLabel","statesets":[]}]}
        """)!.AsObject();

    // ---- S6 --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_operation_answers_404_and_touches_nothing_when_the_feature_is_off()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create(enabled: false);

        Assert.Equal(404, (await service.GetAsync(42, default)).Status);
        Assert.Equal(404, (await service.SaveAsync(42, Link, default)).Status);
        Assert.Equal(404, (await service.DeleteAsync(42, default)).Status);
        Assert.Equal(404, (await service.UploadImageAsync("data:image/png;base64,AAAA", default)).Status);
        Assert.Equal(404, service.Validate(Link).Status);
        Assert.Empty(sapi.Client.Calls);
    }

    [Fact]
    public async Task The_toggle_is_read_on_every_call()
    {
        (CtaService service, _, FakeSettings settings, _) = Create(enabled: false);
        Assert.Equal(404, (await service.GetAsync(42, default)).Status);

        settings.Value = settings.Value with { EnableCta = true };
        Assert.True((await service.GetAsync(42, default)).Ok);
    }

    // ---- save, reload, save (C2, C7) --------------------------------------------------------------------------------

    [Fact]
    public async Task Save_creates_one_timeline_and_then_updates_it_with_identical_bytes()
    {
        (CtaService service, FakeTimelineSapi sapi, _, NoThumbnails thumbs) = Create();

        CtaStateResponseModel first = Value(await service.SaveAsync(42, Link, default));
        Assert.Equal("POST", sapi.Writes.Single().Method);
        Assert.Single(sapi.ClipTimelines[42]);
        Assert.Equal(60, first.ClipLength);

        // Reload as the editor does, then save what it shows.
        CtaStateResponseModel loaded = Value(await service.GetAsync(42, default));
        string stored1 = CtaTranslator.Serialize(sapi.Timelines.Values.Single());
        Value(await service.SaveAsync(42, loaded.Cta!, default));
        string stored2 = CtaTranslator.Serialize(sapi.Timelines.Values.Single());
        loaded = Value(await service.GetAsync(42, default));
        Value(await service.SaveAsync(42, loaded.Cta!, default));
        string stored3 = CtaTranslator.Serialize(sapi.Timelines.Values.Single());

        Assert.Equal(stored1, stored2);
        Assert.Equal(stored2, stored3);
        // Still one timeline, and the second and third save were PUTs.
        Assert.Single(sapi.Timelines);
        Assert.Single(sapi.ClipTimelines[42]);
        Assert.Equal(["POST", "PUT", "PUT"], sapi.Writes.Select(w => w.Method));
        Assert.All(thumbs.Touched, id => Assert.Equal(42, id));
        Assert.Equal(3, thumbs.Touched.Count);
    }

    [Fact]
    public async Task Until_end_uses_the_length_SAPI_reports_and_not_the_one_the_browser_sends()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        sapi.ClipLength = 75;
        await service.SaveAsync(42, Link with { ClipLength = 5 }, default);
        Assert.Equal(75, (double)sapi.Timelines.Values.Single()["order"]![0]!["inoutpoints"]![0]!["outpoint"]!);
    }

    [Fact]
    public async Task Until_end_is_refused_when_the_clip_length_is_unknown_and_nothing_is_written()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        sapi.ClipLength = 0;
        EditOutcome<CtaStateResponseModel> outcome = await service.SaveAsync(42, Link, default);
        Assert.Equal(400, outcome.Status);
        Assert.Contains("Clip duration not available yet", outcome.Dropped!);
        Assert.Empty(sapi.Writes);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("H", "", "")]
    [InlineData("", "B", "")]
    [InlineData("", "", "L")]
    [InlineData("H", "B", "")]
    [InlineData("H", "", "L")]
    [InlineData("", "B", "L")]
    [InlineData("H", "B", "L")]
    public async Task Text_slots_come_back_in_the_slot_they_were_saved_in_with_and_without_an_image(string headline, string body, string button)
    {
        foreach (string image in new[] { "", "555" })
        {
            (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
            // A link CTA needs a button label or an image; the image case covers the text-over-image widget.
            CtaConfig cfg = Link with { Headline = headline, Body = body, ButtonLabel = button, BackgroundImageId = image };
            if (image.Length == 0 && button.Length == 0)
            {
                Assert.Equal(400, (await service.SaveAsync(42, cfg, default)).Status);
                continue;
            }

            Value(await service.SaveAsync(42, cfg, default));
            for (int i = 0; i < 2; i++)
            {
                CtaConfig back = Value(await service.GetAsync(42, default)).Cta!;
                Assert.Equal((headline, body, button), (back.Headline, back.Body, back.ButtonLabel));
                Assert.Equal(image, back.BackgroundImageId);
                Value(await service.SaveAsync(42, back, default));
            }
        }
    }

    [Fact]
    public async Task A_form_cta_round_trips_with_its_endpoint()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        var form = new CtaConfig { Title = "Capture", CtaType = "form", FormEmailLabel = "Your email", FormSubmitLabel = "Join", FormThankYou = "Thanks a lot", FormSubmitEndpoint = "https://example.com/hook", EndAt = 30 };
        Value(await service.SaveAsync(42, form, default));
        CtaConfig back = Value(await service.GetAsync(42, default)).Cta!;
        Assert.Equal(("form", "Your email", "Join", "Thanks a lot", "https://example.com/hook"), (back.CtaType, back.FormEmailLabel, back.FormSubmitLabel, back.FormThankYou, back.FormSubmitEndpoint));
        string before = CtaTranslator.Serialize(sapi.Timelines.Values.Single());
        Value(await service.SaveAsync(42, back, default));
        Assert.Equal(before, CtaTranslator.Serialize(sapi.Timelines.Values.Single()));
    }

    // ---- refusals --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_foreign_timeline_is_refused_with_409_and_nothing_is_written()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        JsonObject foreign = Interactive();
        long id = sapi.AddTimeline(42, foreign);
        string before = CtaTranslator.Serialize(sapi.Timelines[id]);

        EditOutcome<CtaStateResponseModel> save = await service.SaveAsync(42, Link, default);
        EditOutcome<CtaDeleteResponseModel> delete = await service.DeleteAsync(42, default);
        CtaStateResponseModel state = Value(await service.GetAsync(42, default));

        Assert.Equal(409, save.Status);
        Assert.Equal(409, delete.Status);
        Assert.Empty(sapi.Writes);
        Assert.Equal(before, CtaTranslator.Serialize(sapi.Timelines[id]));
        // Reading is not an error: the editor locks itself.
        Assert.True(state.ForeignTimeline);
        Assert.Equal(id.ToString(), state.ForeignTimelineId);
        Assert.Null(state.Cta);
    }

    [Fact]
    public async Task A_failed_timeline_lookup_is_never_a_create()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        long unreadable = sapi.AddTimeline(42, Interactive());
        sapi.FailTimelineRead = unreadable;

        EditOutcome<CtaStateResponseModel> save = await service.SaveAsync(42, Link, default);
        EditOutcome<CtaDeleteResponseModel> delete = await service.DeleteAsync(42, default);

        Assert.NotNull(save.Failure);
        Assert.Equal(502, save.Failure!.StatusCode);
        Assert.NotNull(delete.Failure);
        Assert.Empty(sapi.Writes);
        Assert.Single(sapi.ClipTimelines[42]);
    }

    [Fact]
    public async Task A_clip_that_does_not_exist_is_404_and_nothing_is_written()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        EditOutcome<CtaStateResponseModel> outcome = await service.SaveAsync(77, Link, default);
        Assert.Equal(404, outcome.Failure!.StatusCode);
        Assert.Empty(sapi.Writes);
    }

    [Fact]
    public async Task Invalid_input_is_400_with_the_rule_messages_and_nothing_is_written()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        EditOutcome<CtaStateResponseModel> outcome = await service.SaveAsync(42, Link with { Url = "javascript:alert(1)", TextColor = "zzz" }, default);
        Assert.Equal(400, outcome.Status);
        Assert.Contains("URL must be a valid http(s) URL", outcome.Dropped!);
        Assert.Contains(outcome.Dropped!, m => m.StartsWith("textColor"));
        Assert.Empty(sapi.Writes);
    }

    [Fact]
    public async Task Oversized_text_is_refused()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        Assert.Equal(400, (await service.SaveAsync(42, Link with { Body = new string('x', 5001) }, default)).Status);
        Assert.Empty(sapi.Writes);
    }

    [Fact]
    public async Task A_SAPI_write_error_is_a_failure_and_a_2xx_without_an_id_is_too()
    {
        (CtaService service, FakeTimelineSapi sapi, _, NoThumbnails thumbs) = Create();
        sapi.FailWrites = true;
        Assert.Equal(500, (await service.SaveAsync(42, Link, default)).Failure!.StatusCode);

        sapi.FailWrites = false;
        sapi.WritesWithoutId = true;
        Assert.Equal(502, (await service.SaveAsync(42, Link, default)).Failure!.StatusCode);
        Assert.Empty(thumbs.Touched);
    }

    // ---- delete ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_only_our_timeline()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        Value(await service.SaveAsync(42, Link, default));
        // Another clip's timeline must not be touched either.
        sapi.ClipTimelines[43] = [];
        long other = sapi.AddTimeline(43, Interactive());

        long ours = sapi.ClipTimelines[42].Single();
        Assert.True(Value(await service.DeleteAsync(42, default)).Deleted);

        Assert.Single(sapi.Writes, w => w.Method == "DELETE");
        Assert.Equal($"/sapi/timeline/{ours}", sapi.Writes.Single(w => w.Method == "DELETE").Path);
        Assert.Equal("deleted", (string?)sapi.Timelines[ours]["status"]);
        Assert.Equal("published", (string?)sapi.Timelines[other]["status"]);
        // Gone for the editor.
        CtaStateResponseModel state = Value(await service.GetAsync(42, default));
        Assert.Null(state.Cta);
        Assert.False(Value(await service.DeleteAsync(42, default)).Deleted);
    }

    [Fact]
    public async Task Delete_with_no_timeline_is_a_success_that_says_nothing_was_deleted()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        Assert.False(Value(await service.DeleteAsync(42, default)).Deleted);
        Assert.Empty(sapi.Writes);
    }

    // ---- image (C4) ------------------------------------------------------------------------------------------

    private static string Png() => "data:image/png;base64," + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);

    [Fact]
    public async Task An_image_becomes_a_published_commercial_creative_with_the_plugins_payload()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        string data = Png();
        Assert.Equal("900", Value(await service.UploadImageAsync(data, default)).ImageId);

        var call = Assert.Single(sapi.Writes);
        Assert.Equal(("POST", "/sapi/commonclip"), (call.Method, call.Path));
        var body = (JsonObject)call.Request!.Json!;
        Assert.Equal(("commercial", "image", "published"), ((string?)body["usetype"], (string?)body["mediatype"], (string?)body["status"]));
        Assert.StartsWith("CTA background ", (string?)body["title"]);
        Assert.Equal(data, (string?)body["thumbnails"]![0]!["data"]);
        Assert.True((bool)body["thumbnails"]![0]!["main"]!);
        Assert.Equal(1, (int)body["thumbnails"]![0]!["crops"]!["landscape"]!["width"]!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a data uri")]
    [InlineData("data:image/svg+xml;base64,AAAA")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("data:image/png;base64,@@@@")]
    // Declares PNG but the bytes are a GIF.
    [InlineData("data:image/png;base64,R0lGODlhAQABAAAAACw=")]
    public async Task Bad_image_data_is_refused_before_anything_is_sent(string? data)
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        EditOutcome<CtaImageUploadResponseModel> outcome = await service.UploadImageAsync(data, default);
        Assert.Equal(400, outcome.Status);
        Assert.Equal("Invalid image data", outcome.Error);
        Assert.Empty(sapi.Client.Calls);
    }

    [Fact]
    public async Task An_image_over_10_MB_is_refused()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        string big = "data:image/png;base64," + new string('A', CtaImageValidator.MaxDataLength);
        EditOutcome<CtaImageUploadResponseModel> outcome = await service.UploadImageAsync(big, default);
        Assert.Equal(400, outcome.Status);
        Assert.Equal("Image too large (max 10 MB)", outcome.Error);
        Assert.Empty(sapi.Client.Calls);
    }

    [Theory]
    [InlineData("jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    public void Jpeg_and_gif_are_accepted(string type, byte[] bytes) =>
        Assert.Null(CtaImageValidator.Validate($"data:image/{type};base64," + Convert.ToBase64String(bytes)));

    [Fact]
    public void Webp_is_accepted_when_the_bytes_are_riff_webp()
    {
        byte[] webp = [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBP"u8.ToArray()];
        Assert.Null(CtaImageValidator.Validate("data:image/webp;base64," + Convert.ToBase64String(webp)));
        Assert.NotNull(CtaImageValidator.Validate("data:image/webp;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public async Task A_creative_answer_with_a_non_numeric_id_is_a_failure()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        sapi.WritesWithoutId = true;
        Assert.Equal(502, (await service.UploadImageAsync(Png(), default)).Failure!.StatusCode);
    }

    // ---- validate --------------------------------------------------------------------------------------------

    [Fact]
    public void Validate_lists_the_save_rules_without_calling_SAPI()
    {
        (CtaService service, FakeTimelineSapi sapi, _, _) = Create();
        Assert.Empty(Value(service.Validate(Link with { ClipLength = 10 })).Errors);
        Assert.Contains("URL is required", Value(service.Validate(Link with { Url = "", ClipLength = 10 })).Errors);
        Assert.Empty(sapi.Client.Calls);
    }
}

/// <summary>The endpoints: status mapping, and who may call them.</summary>
public class CtaControllerTests
{
    private sealed class StubCta(Func<object> outcome) : ICtaService
    {
        public Task<EditOutcome<CtaStateResponseModel>> GetAsync(long clipId, CancellationToken ct) => Task.FromResult((EditOutcome<CtaStateResponseModel>)outcome());

        public Task<EditOutcome<CtaStateResponseModel>> SaveAsync(long clipId, CtaConfig config, CancellationToken ct) => Task.FromResult((EditOutcome<CtaStateResponseModel>)outcome());

        public Task<EditOutcome<CtaDeleteResponseModel>> DeleteAsync(long clipId, CancellationToken ct) => Task.FromResult((EditOutcome<CtaDeleteResponseModel>)outcome());

        public Task<EditOutcome<CtaImageUploadResponseModel>> UploadImageAsync(string? dataUri, CancellationToken ct) => Task.FromResult((EditOutcome<CtaImageUploadResponseModel>)outcome());

        public EditOutcome<CtaValidateResponseModel> Validate(CtaConfig config) => (EditOutcome<CtaValidateResponseModel>)outcome();
    }

    [Fact]
    public async Task Statuses_map_to_404_409_400_and_502_with_problem_details_that_the_backoffice_keeps()
    {
        var disabled = new CtaController(new StubCta(() => EditOutcome<CtaStateResponseModel>.Reject(404, "Call to action is turned off in the settings")));
        ObjectResult notFound = Assert.IsType<ObjectResult>(await disabled.GetCta(1, default));
        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal(404, Assert.IsType<ProblemDetails>(notFound.Value).Status);

        var foreign = new CtaController(new StubCta(() => EditOutcome<CtaStateResponseModel>.Reject(409, "This clip already has an interactive timeline created by another tool.")));
        ObjectResult conflict = Assert.IsType<ObjectResult>(await foreign.SaveCta(1, new CtaConfig(), default));
        ProblemDetails problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal(409, conflict.StatusCode);
        Assert.False(string.IsNullOrEmpty(problem.Type));
        Assert.False(string.IsNullOrEmpty(problem.Title));
        Assert.Equal("foreign_timeline", problem.Extensions["code"]);
        Assert.Contains("another tool", problem.Detail);

        var invalid = new CtaController(new StubCta(() => EditOutcome<CtaStateResponseModel>.Reject(400, "URL is required", ["URL is required"])));
        ProblemDetails bad = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(await invalid.SaveCta(1, new CtaConfig(), default)).Value);
        Assert.Equal(["URL is required"], (IReadOnlyList<string>)bad.Extensions["errors"]!);

        var failing = new CtaController(new StubCta(() => EditOutcome<CtaStateResponseModel>.Fail(SapiResult.Failure(500, "SAPI request failed (500) with secret internal text"))));
        ObjectResult gateway = Assert.IsType<ObjectResult>(await failing.SaveCta(1, new CtaConfig(), default));
        Assert.Equal(502, gateway.StatusCode);

        var missingClip = new CtaController(new StubCta(() => EditOutcome<CtaStateResponseModel>.Fail(SapiResult.Failure(404, "SAPI request failed (404)"))));
        Assert.Equal(404, Assert.IsType<ObjectResult>(await missingClip.GetCta(1, default)).StatusCode);
    }

    [Fact]
    public async Task Success_is_200()
    {
        var ok = new CtaController(new StubCta(() => EditOutcome<CtaDeleteResponseModel>.Of(new CtaDeleteResponseModel { Deleted = true })));
        Assert.IsType<OkObjectResult>(await ok.RemoveCta(1, new SettingsController.ConfirmRequest(true), default));
    }

    [Fact]
    public void Every_endpoint_has_the_clip_metadata_policy_including_delete_like_the_plugin()
    {
        Assert.Contains(Authorization.BlueBillywigPolicies.Library, typeof(CtaController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Select(a => a.Policy));
        // No action relaxes it or swaps in another policy.
        foreach (var method in typeof(CtaController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
        {
            Assert.Empty(method.GetCustomAttributes(typeof(AuthorizeAttribute), true));
            Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
        }
    }
}

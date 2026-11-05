using System.Text;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.Upload;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests;

public class UploadValidationTests
{
    private static NewClip Init(string? filename = "my video.mp4", long size = 1000, string? title = null, string? status = null, string? tags = null, string? description = null, string? type = null)
    {
        Assert.True(UploadValidation.TryParseInit(new InitUploadRequestModel { Filename = filename, Filesize = size, Title = title, Status = status, Tags = tags, Description = description, ContentType = type }, out NewClip clip, out string error), error);
        return clip;
    }

    private static string Reject(InitUploadRequestModel request)
    {
        Assert.False(UploadValidation.TryParseInit(request, out _, out string error));
        return error;
    }

    [Theory]
    [InlineData("published")]
    [InlineData("draft")]
    [InlineData("archived")]
    public void Allowed_statuses_pass(string status) => Assert.Equal(status, Init(status: status).Status);

    [Theory]
    [InlineData("deleted")]
    [InlineData("Published")]
    [InlineData("published; drop")]
    [InlineData("1")]
    public void Statuses_outside_the_allowlist_are_rejected_not_dropped(string status) =>
        Assert.Equal("Invalid status value", Reject(new InitUploadRequestModel { Filename = "a.mp4", Filesize = 1, Status = status }));

    [Fact]
    public void No_status_means_published() => Assert.Equal("published", Init().Status);

    [Fact]
    public void Title_falls_back_to_the_filename_without_its_extension()
    {
        Assert.Equal("umbraco-poc-test-clip", Init("umbraco-poc-test-clip.mp4").Title);
        Assert.Equal("umbraco-poc-test-clip", Init("umbraco-poc-test-clip.mp4", title: "   ").Title);
        Assert.Equal("My title", Init("a.mp4", title: " My   title ").Title);
    }

    [Fact]
    public void Title_and_description_have_tags_and_control_characters_removed()
    {
        NewClip clip = Init(title: "<b>Hi</b>\u0000 there", description: "line1\r\nline2 <script>x</script>");

        Assert.Equal("Hi there", clip.Title);
        Assert.Equal("line1\nline2 x", clip.Description);
    }

    [Fact]
    public void Title_is_bounded() => Assert.Equal(255, Init(title: new string('a', 600)).Title.Length);

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\videos\\clip.mp4", "clip.mp4")]
    [InlineData("a<b>:c|d.mp4", "abcd.mp4")]
    [InlineData(".hidden.mp4", "hidden.mp4")]
    public void File_names_lose_directories_and_unsafe_characters(string input, string expected) => Assert.Equal(expected, UploadValidation.SanitizeFileName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_filename_is_rejected(string? name) => Assert.Equal("Missing filename or filesize", Reject(new InitUploadRequestModel { Filename = name, Filesize = 10 }));

    [Fact]
    public void A_missing_or_oversized_file_is_rejected()
    {
        Assert.Equal("Missing filename or filesize", Reject(new InitUploadRequestModel { Filename = "a.mp4", Filesize = 0 }));
        Assert.Equal("File is too large", Reject(new InitUploadRequestModel { Filename = "a.mp4", Filesize = UploadValidation.MaxFileSize + 1 }));
    }

    [Fact]
    public void Content_type_is_checked_for_shape() 
    {
        Assert.Equal("video/mp4", Init(type: "video/mp4").ContentType);
        Assert.Equal("application/octet-stream", Init(type: "video/mp4\r\nX-Evil: 1").ContentType);
        Assert.Equal("application/octet-stream", Init(type: null).ContentType);
    }

    [Fact]
    public void Tags_are_trimmed_deduplicated_and_bounded()
    {
        Assert.Equal(["a", "B", "c d"], UploadValidation.ParseTags(" a, B ,, A,c d "));
        Assert.Empty(UploadValidation.ParseTags(null));
        Assert.Equal(50, UploadValidation.ParseTags(string.Join(',', Enumerable.Range(0, 80))).Count);
        Assert.DoesNotContain(UploadValidation.ParseTags(new string('x', 65) + ",ok"), t => t.Length > 64);
    }

    [Fact]
    public void Sign_part_needs_an_upload_id_and_a_part_number_in_range()
    {
        Assert.True(UploadValidation.TryParseSign(new SignPartRequestModel { UploadId = "abc-123", PartNumber = 1 }, out string id, out int part, out _));
        Assert.Equal(("abc-123", 1), (id, part));
        Assert.False(UploadValidation.TryParseSign(new SignPartRequestModel { UploadId = "abc", PartNumber = 0 }, out _, out _, out _));
        Assert.False(UploadValidation.TryParseSign(new SignPartRequestModel { UploadId = "abc", PartNumber = 10_001 }, out _, out _, out _));
        Assert.False(UploadValidation.TryParseSign(new SignPartRequestModel { UploadId = "", PartNumber = 1 }, out _, out _, out _));
        Assert.False(UploadValidation.TryParseSign(new SignPartRequestModel { UploadId = "a b\r\n", PartNumber = 1 }, out _, out _, out _));
    }

    [Fact]
    public void Complete_needs_well_formed_unique_parts()
    {
        Assert.True(UploadValidation.TryParseComplete(Complete("u1", (1, "\"e1\""), (2, "\"e2\"")), out string id, out var parts, out _));
        Assert.Equal("u1", id);
        Assert.Equal(2, parts.Count);

        Assert.False(UploadValidation.TryParseComplete(Complete("u1"), out _, out _, out string empty));
        Assert.Equal("Missing uploadId or parts", empty);
        Assert.False(UploadValidation.TryParseComplete(Complete("u1", (1, "")), out _, out _, out _));
        Assert.False(UploadValidation.TryParseComplete(Complete("u1", (0, "x")), out _, out _, out _));
        Assert.False(UploadValidation.TryParseComplete(Complete("u1", (1, "a"), (1, "b")), out _, out _, out _));
        Assert.False(UploadValidation.TryParseComplete(new CompleteUploadRequestModel { UploadId = "u1" }, out _, out _, out _));
    }

    [Fact]
    public void Tus_metadata_is_base64_pairs() =>
        Assert.Equal("filename " + Convert.ToBase64String(Encoding.UTF8.GetBytes("é.mp4")) + ",mediaclipId " + Convert.ToBase64String("42"u8.ToArray()),
            UploadValidation.EncodeTusMetadata([new("filename", "é.mp4"), new("mediaclipId", "42")]));

    private static CompleteUploadRequestModel Complete(string id, params (int, string)[] parts) =>
        new() { UploadId = id, Parts = parts.Select(p => new UploadPartModel { PartNumber = p.Item1, ETag = p.Item2 }).ToList() };
}

public class UploadServiceTests
{
    private const string TusReply = """{"tusUploadId":"tus-1","s3":{"uploadId":"s3-1","key":"k/1","partSize":5242880,"presignedUrls":[{"partNumber":1,"url":"https://storage.example/part1?sig=a"},{"partNumber":2,"url":"javascript:alert(1)"}]}}""";

    private static (UploadService Service, FakeClient Client, PendingUploads Pending) Rig(Func<string, string, SapiRequest?, SapiResult> handler)
    {
        var client = new FakeClient { Handler = handler };
        var pending = new PendingUploads(new MemoryCache(new MemoryCacheOptions()));
        return (new UploadService(client, pending, NullLogger<UploadService>.Instance), client, pending);
    }

    private static NewClip Clip() => new("umbraco-poc-test-a.mp4", 1234, "video/mp4", "umbraco-poc-test-a", "d", ["t1"], "draft");

    private static SapiResult Happy(string method, string path, SapiRequest? r) => (method, path) switch
    {
        ("PUT", "/sapi/mediaclip") => FakeClient.Ok("""{"id":"555"}"""),
        ("POST", "/sapi/tus") => FakeClient.Ok(TusReply),
        _ => SapiResult.Failure(404, "unexpected " + method + " " + path),
    };

    [Fact]
    public async Task Init_creates_the_clip_then_starts_the_upload_with_tus_headers()
    {
        (UploadService service, FakeClient client, _) = Rig(Happy);

        UploadOutcome<InitUploadResponseModel> result = await service.InitAsync(Clip(), Guid.NewGuid(), default);

        Assert.True(result.Ok);
        Assert.Equal(555, result.Value!.MediaclipId);
        Assert.Equal("tus-1", result.Value.UploadId);
        Assert.Equal(5242880, result.Value.PartSize);
        Assert.Equal("k/1", result.Value.Key);
        // The javascript: URL is not a storage URL and is dropped.
        Assert.Equal([1], result.Value.PresignedUrls.Select(p => p.PartNumber));

        (string method, string path, SapiRequest? create) = client.Calls[0];
        Assert.Equal(("PUT", "/sapi/mediaclip"), (method, path));
        Assert.Equal("false", create!.Query!["softsave"]);
        string json = System.Text.Json.JsonSerializer.Serialize(create.Json);
        Assert.Contains("\"status\":\"draft\"", json);
        Assert.Contains("\"originalfilename\":\"umbraco-poc-test-a.mp4\"", json);
        Assert.Contains("\"cat\":[\"t1\"]", json);

        SapiRequest tus = client.Calls[1].Request!;
        Assert.Equal("1234", tus.Headers!["Upload-Length"]);
        Assert.Equal("1.0.0", tus.Headers["Tus-Resumable"]);
        Assert.Contains("mediaclipId " + Convert.ToBase64String("555"u8.ToArray()), tus.Headers["Upload-Metadata"]);
    }

    [Fact]
    public async Task A_failed_tus_start_deletes_the_clip_it_just_created()
    {
        (UploadService service, FakeClient client, _) = Rig((m, p, r) => (m, p) switch
        {
            ("PUT", "/sapi/mediaclip") => FakeClient.Ok("""{"id":"555"}"""),
            ("POST", "/sapi/tus") => SapiResult.Failure(500, "SAPI request failed (500)"),
            ("DELETE", "/sapi/mediaclip/555") => FakeClient.Ok("{}"),
            _ => SapiResult.Failure(404, "x"),
        });

        UploadOutcome<InitUploadResponseModel> result = await service.InitAsync(Clip(), Guid.NewGuid(), default);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Failure!.StatusCode);
        Assert.Contains(client.Calls, c => c.Method == "DELETE" && c.Path == "/sapi/mediaclip/555");
    }

    [Fact]
    public async Task An_unusable_tus_answer_also_deletes_the_clip()
    {
        (UploadService service, FakeClient client, _) = Rig((m, p, r) => (m, p) switch
        {
            ("PUT", "/sapi/mediaclip") => FakeClient.Ok("""{"id":"555"}"""),
            ("POST", "/sapi/tus") => FakeClient.Ok("{}"),
            ("DELETE", _) => FakeClient.Ok("{}"),
            _ => SapiResult.Failure(404, "x"),
        });

        UploadOutcome<InitUploadResponseModel> result = await service.InitAsync(Clip(), Guid.NewGuid(), default);

        Assert.False(result.Ok);
        Assert.Contains(client.Calls, c => c.Method == "DELETE" && c.Path == "/sapi/mediaclip/555");
    }

    [Fact]
    public async Task A_failed_clip_create_makes_no_other_call()
    {
        (UploadService service, FakeClient client, _) = Rig((m, p, r) => SapiResult.Failure(401, "SAPI request failed (401)"));

        UploadOutcome<InitUploadResponseModel> result = await service.InitAsync(Clip(), Guid.NewGuid(), default);

        Assert.Equal(401, result.Failure!.StatusCode);
        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData("http://storage.example/part1?sig=a")]
    [InlineData("HTTP://storage.example/part1?sig=a")]
    [InlineData("ftp://storage.example/part1")]
    [InlineData("//storage.example/part1")]
    [InlineData("/relative/part1")]
    public async Task Presigned_part_urls_that_are_not_https_are_dropped_at_init(string url)
    {
        string reply = TusReply.Replace("https://storage.example/part1?sig=a", url);
        (UploadService service, _, _) = Rig((m, p, r) => (m, p) switch
        {
            ("PUT", "/sapi/mediaclip") => FakeClient.Ok("""{"id":"555"}"""),
            ("POST", "/sapi/tus") => FakeClient.Ok(reply),
            _ => SapiResult.Failure(404, "unexpected"),
        });

        UploadOutcome<InitUploadResponseModel> result = await service.InitAsync(Clip(), Guid.NewGuid(), default);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!.PresignedUrls);
    }

    [Theory]
    [InlineData("http://storage.example/p?sig=b")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task A_sign_part_answer_that_is_not_https_is_a_bad_gateway(string url)
    {
        var user = Guid.NewGuid();
        (UploadService service, _, PendingUploads pending) = Rig((m, p, r) => FakeClient.Ok("{\"url\":\"" + url + "\"}"));
        pending.Register(555, "tus-1", user);

        UploadOutcome<SignPartResponseModel> result = await service.SignPartAsync("tus-1", 1, user, default);

        Assert.Null(result.Value);
        Assert.Equal(502, result.Failure!.StatusCode);
    }

    [Theory]
    [InlineData("http://storage.example/obj")]
    [InlineData("javascript:alert(1)")]
    public async Task A_completed_upload_location_that_is_not_https_is_not_returned(string location)
    {
        var user = Guid.NewGuid();
        (UploadService service, _, PendingUploads pending) = Rig((m, p, r) => p.EndsWith("/complete") ? FakeClient.Ok("{\"location\":\"" + location + "\"}") : Happy(m, p, r));
        pending.Register(555, "tus-1", user);

        UploadOutcome<CompleteUploadResponseModel> result = await service.CompleteAsync("tus-1", [(1, "\"e1\"")], user, default);

        Assert.Null(result.Value!.Location);
    }

    [Fact]
    public void Only_https_counts_as_a_storage_url()
    {
        Assert.True(UploadService.IsHttpsUrl("https://storage.example/p?sig=b"));
        Assert.True(UploadService.IsHttpsUrl("HTTPS://storage.example/p"));
        Assert.False(UploadService.IsHttpsUrl("http://storage.example/p"));
        Assert.False(UploadService.IsHttpsUrl("//storage.example/p"));
        Assert.False(UploadService.IsHttpsUrl(""));
    }

    [Fact]
    public async Task Sign_part_encodes_the_upload_id_into_the_path()
    {
        var user = Guid.NewGuid();
        (UploadService service, FakeClient client, PendingUploads pending) = Rig((m, p, r) => FakeClient.Ok("""{"url":"https://storage.example/p?sig=b"}"""));
        pending.Register(555, "a/b c", user);

        UploadOutcome<SignPartResponseModel> result = await service.SignPartAsync("a/b c", 3, user, default);

        Assert.Equal("https://storage.example/p?sig=b", result.Value!.Url);
        Assert.Equal("/sapi/tus/a%2Fb%20c/sign/3", client.Calls[0].Path);
    }

    [Fact]
    public async Task Complete_posts_the_parts_and_marks_the_clip_complete()
    {
        var user = Guid.NewGuid();
        (UploadService service, FakeClient client, PendingUploads pending) = Rig((m, p, r) => p.EndsWith("/complete") ? FakeClient.Ok("""{"location":"https://storage.example/obj"}""") : Happy(m, p, r));
        pending.Register(555, "tus-1", user);

        UploadOutcome<CompleteUploadResponseModel> result = await service.CompleteAsync("tus-1", [(1, "\"e1\"")], user, default);

        Assert.Equal("https://storage.example/obj", result.Value!.Location);
        Assert.Equal("/sapi/tus/tus-1/complete", client.Calls[0].Path);
        Assert.Contains("\"PartNumber\":1", System.Text.Json.JsonSerializer.Serialize(client.Calls[0].Request!.Json));
        // A completed upload is processing; abort can no longer remove it. (Found by upload id: SAPI's complete answer names no clip.)
        Assert.False(pending.TryTakeForAbort(555, user, out _));
    }

    [Fact]
    public async Task Abort_deletes_only_a_clip_this_user_started()
    {
        var owner = Guid.NewGuid();
        (UploadService service, FakeClient client, _) = Rig(new Func<string, string, SapiRequest?, SapiResult>(Happy).Plus("DELETE", "/sapi/mediaclip/555", FakeClient.Ok("{}")));
        await service.InitAsync(Clip(), owner, default);
        client.Calls.Clear();

        UploadOutcome<object> other = await service.AbortAsync(555, Guid.NewGuid(), default);
        Assert.Equal(403, other.Status);
        Assert.DoesNotContain(client.Calls, c => c.Method == "DELETE");

        UploadOutcome<object> mine = await service.AbortAsync(555, owner, default);
        Assert.True(mine.Ok);
        Assert.Contains(client.Calls, c => c is { Method: "DELETE", Path: "/sapi/mediaclip/555" });
    }

    [Fact]
    public async Task Abort_of_a_clip_nobody_started_is_refused_and_deletes_nothing()
    {
        (UploadService service, FakeClient client, _) = Rig(Happy);

        UploadOutcome<object> result = await service.AbortAsync(11226, Guid.NewGuid(), default);

        Assert.Equal(403, result.Status);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task A_failed_abort_can_be_retried()
    {
        var owner = Guid.NewGuid();
        int deletes = 0;
        (UploadService service, _, _) = Rig((m, p, r) => m == "DELETE" ? (++deletes == 1 ? SapiResult.Failure(502, "down") : FakeClient.Ok("{}")) : Happy(m, p, r));
        await service.InitAsync(Clip(), owner, default);

        Assert.False((await service.AbortAsync(555, owner, default)).Ok);
        Assert.True((await service.AbortAsync(555, owner, default)).Ok);
    }
}

/// <summary>Signing a part and completing an upload belong to the user who started it, as abort does.</summary>
public class UploadOwnershipTests
{
    private static (UploadService Service, FakeClient Client) Rig(out PendingUploads pending)
    {
        var client = new FakeClient
        {
            Handler = (m, p, r) => (m, p) switch
            {
                ("GET", "/sapi/tus/tus-1/sign/1") => FakeClient.Ok("""{"url":"https://storage.example/p?sig=b"}"""),
                ("POST", "/sapi/tus/tus-1/complete") => FakeClient.Ok("""{"location":"https://storage.example/o"}"""),
                _ => SapiResult.Failure(404, "x"),
            },
        };
        pending = new PendingUploads(new MemoryCache(new MemoryCacheOptions()));
        return (new UploadService(client, pending, NullLogger<UploadService>.Instance), client);
    }

    [Fact]
    public async Task Another_user_cannot_sign_a_part_of_an_upload_they_did_not_start()
    {
        (UploadService service, FakeClient client) = Rig(out PendingUploads pending);
        var owner = Guid.NewGuid();
        pending.Register(555, "tus-1", owner);

        UploadOutcome<SignPartResponseModel> other = await service.SignPartAsync("tus-1", 1, Guid.NewGuid(), default);
        UploadOutcome<SignPartResponseModel> mine = await service.SignPartAsync("tus-1", 1, owner, default);

        Assert.Equal(403, other.Status);
        Assert.Null(other.Value);
        Assert.True(mine.Ok);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Another_user_cannot_complete_an_upload_and_so_cannot_take_away_the_owners_abort()
    {
        (UploadService service, FakeClient client) = Rig(out PendingUploads pending);
        var owner = Guid.NewGuid();
        pending.Register(555, "tus-1", owner);

        UploadOutcome<CompleteUploadResponseModel> other = await service.CompleteAsync("tus-1", [(1, "\"e\"")], Guid.NewGuid(), default);

        Assert.Equal(403, other.Status);
        Assert.Empty(client.Calls);
        Assert.True(pending.TryTakeForAbort(555, owner, out string uploadId)); // still the owner's to abort
        Assert.Equal("tus-1", uploadId);
    }

    [Fact]
    public async Task An_upload_this_server_does_not_know_belongs_to_nobody()
    {
        (UploadService service, FakeClient client) = Rig(out _);

        Assert.Equal(403, (await service.SignPartAsync("tus-1", 1, Guid.NewGuid(), default)).Status);
        Assert.Equal(403, (await service.CompleteAsync("tus-1", [(1, "\"e\"")], Guid.NewGuid(), default)).Status);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task An_upload_id_that_only_resembles_the_owners_is_not_theirs()
    {
        (UploadService service, _) = Rig(out PendingUploads pending);
        var owner = Guid.NewGuid();
        pending.Register(555, "tus-1", owner);

        Assert.False(pending.IsOwnedBy("tus-1 ", owner));
        Assert.False(pending.IsOwnedBy("TUS-1", owner));
        Assert.True(pending.IsOwnedBy("tus-1", owner));
        await Task.CompletedTask;
    }
}

internal static class HandlerExtensions
{
    public static Func<string, string, SapiRequest?, SapiResult> Plus(this Func<string, string, SapiRequest?, SapiResult> inner, string method, string path, SapiResult result) =>
        (m, p, r) => m == method && p == path ? result : inner(m, p, r);
}

public class UploadAfterCompleteTests
{
    [Fact]
    public async Task Abort_after_a_completed_upload_is_refused_and_the_processing_clip_is_not_deleted()
    {
        // The exact flow that deleted a processing clip on bb.dev before the upload id link existed.
        var owner = Guid.NewGuid();
        var client = new FakeClient
        {
            Handler = (m, p, r) => (m, p) switch
            {
                ("PUT", "/sapi/mediaclip") => FakeClient.Ok("""{"id":"555"}"""),
                ("POST", "/sapi/tus") => FakeClient.Ok("""{"tusUploadId":"tus-1","s3":{"partSize":5242880,"presignedUrls":[{"partNumber":1,"url":"https://storage.example/p"}]}}"""),
                ("POST", "/sapi/tus/tus-1/complete") => FakeClient.Ok("""{"location":"https://storage.example/o"}"""),
                _ => SapiResult.Failure(404, "x"),
            },
        };
        var service = new UploadService(client, new PendingUploads(new MemoryCache(new MemoryCacheOptions())), NullLogger<UploadService>.Instance);
        await service.InitAsync(new NewClip("a.mp4", 10, "video/mp4", "a", null, [], "draft"), owner, default);
        await service.CompleteAsync("tus-1", [(1, "\"e\"")], owner, default);

        UploadOutcome<object> abort = await service.AbortAsync(555, owner, default);

        Assert.Equal(403, abort.Status);
        Assert.DoesNotContain(client.Calls, c => c.Method == "DELETE");
    }
}

using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Tests;

public class LibraryServiceTests
{
    private const string Clips = """
        {"numfound":204,"offset":15,"count":2,"items":[
          {"id":"11","title":"A <b>bold</b> title","description":"d","cat":["x","y"],"status":"published","mediatype":"video","sourcetype":"on_demand","length":"90","createddate":"2026-11-01T15:39:12Z","updateddate":"2026-11-01T15:41:23Z",
           "thumbnails":[{"src":"/t/1.webp","main":true}],"movingThumbnails":[{"src":"/m/1.mp4"}]},
          {"id":"12","originalfilename":"file.mp4","thumbnails":[],"cat":[]}
        ]}
        """;

    private static (LibraryService Service, FakeClient Client, FakeSettings Settings) Rig()
    {
        var client = new FakeClient();
        client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        var settings = new FakeSettings();
        return (new LibraryService(client, settings), client, settings);
    }

    private static LibraryFilter Filter(int page = 0)
    {
        LibraryQuery.TryParse(null, null, null, null, null, null, page, out LibraryFilter filter, out _);
        return filter;
    }

    [Fact]
    public async Task Clips_come_with_the_total_from_numfound_not_the_page_count()
    {
        (LibraryService service, FakeClient client, _) = Rig();
        client.Json("/sapi/mediaclip", Clips);

        LibraryResult<LibraryClipListResponseModel> result = await service.ListClipsAsync(Filter(1), 100, default);

        Assert.True(result.Ok);
        Assert.Equal(204, result.Value!.Total);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(1, result.Value.Page);
        LibraryClipResponseModel first = result.Value.Items[0];
        Assert.Equal("https://cdn.example/t/1.webp", first.ThumbnailUrl);
        Assert.Equal("https://cdn.example/m/1.mp4", first.PreviewUrl);
        Assert.Equal(["x", "y"], first.Tags);
        Assert.Equal(90, first.Length);
        Assert.Equal("file.mp4", result.Value.Items[1].Title); // title falls back to the original filename
    }

    [Fact]
    public async Task A_page_is_never_larger_than_the_cap()
    {
        (LibraryService service, FakeClient client, FakeSettings settings) = Rig();
        settings.Value = settings.Value with { PageCount = 80 };
        client.Json("/sapi/mediaclip", Clips);

        LibraryResult<LibraryClipListResponseModel> result = await service.ListClipsAsync(Filter(), 15, default);

        Assert.Equal(15, result.Value!.PageSize);
        Assert.Equal("15", client.Calls.First(c => c.Path == "/sapi/mediaclip").Request!.Query!["limit"]);
    }

    [Fact]
    public async Task A_page_of_200_reaches_SAPI_and_a_larger_setting_is_cut_to_200()
    {
        (LibraryService service, FakeClient client, FakeSettings settings) = Rig();
        client.Json("/sapi/mediaclip", Clips);

        settings.Value = settings.Value with { PageCount = 200 };
        LibraryResult<LibraryClipListResponseModel> result = await service.ListClipsAsync(Filter(), LibraryQuery.MaxPageSize, default);
        Assert.Equal(200, result.Value!.PageSize);
        Assert.Equal("200", client.Calls.Last(c => c.Path == "/sapi/mediaclip").Request!.Query!["limit"]);

        settings.Value = settings.Value with { PageCount = 5000 }; // a stored value from before validation
        result = await service.ListClipsAsync(Filter(), LibraryQuery.MaxPageSize, default);
        Assert.Equal(200, result.Value!.PageSize);
        Assert.Equal("200", client.Calls.Last(c => c.Path == "/sapi/mediaclip").Request!.Query!["limit"]);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    [InlineData(0)]
    public async Task A_sapi_failure_is_a_failure_and_never_an_empty_library(int status)
    {
        (LibraryService service, FakeClient client, _) = Rig();
        client.Responses["/sapi/mediaclip"] = SapiResult.Failure(status, "SAPI request failed");

        LibraryResult<LibraryClipListResponseModel> result = await service.ListClipsAsync(Filter(), 100, default);

        Assert.False(result.Ok);
        Assert.Null(result.Value);
        Assert.Equal(status, result.Failure!.StatusCode);
    }

    [Fact]
    public async Task An_answer_without_items_is_a_failure_not_an_empty_library()
    {
        (LibraryService service, FakeClient client, _) = Rig();
        client.Json("/sapi/mediaclip", """{"numfound":0}""");

        Assert.False((await service.ListClipsAsync(Filter(), 100, default)).Ok);
    }

    [Fact]
    public async Task A_truly_empty_library_is_a_success_with_zero_items()
    {
        (LibraryService service, FakeClient client, _) = Rig();
        client.Json("/sapi/mediaclip", """{"numfound":0,"count":0,"items":[]}""");

        LibraryResult<LibraryClipListResponseModel> result = await service.ListClipsAsync(Filter(), 100, default);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(0, result.Value.Total);
    }

    [Fact]
    public async Task Playlists_embed_codes_use_the_configured_player_host_and_embed_type()
    {
        (LibraryService service, FakeClient client, FakeSettings settings) = Rig();
        settings.Value = settings.Value with { BaseUrl = "https://player.example", Embed = "iframe" };
        client.Json("/sapi/cliplist", """{"numfound":1,"items":[{"id":"1792607060424376","title":"P"}]}""");

        LibraryResult<LibraryPlaylistListResponseModel> result = await service.ListPlaylistsAsync(Filter(), default);

        Assert.Equal(1792607060424376, result.Value!.Items[0].Id);
        Assert.Contains("src=\"https://player.example/p/default/l/1792607060424376.html\"", result.Value.Items[0].EmbedCode);
        Assert.DoesNotContain("bbvms.com", result.Value.Items[0].EmbedCode);
    }

    [Fact]
    public async Task Folders_without_a_usable_code_are_dropped_and_children_kept()
    {
        (LibraryService service, FakeClient client, _) = Rig();
        client.Json("/sapi/folder/tree", """{"items":[{"code":"a","name":"A","children":[{"code":"b c","name":"bad"},{"code":"b","name":"B"}]},{"code":"x\" OR","name":"evil"}]}""");

        LibraryResult<IReadOnlyList<FolderResponseModel>> result = await service.GetFoldersAsync(default);

        FolderResponseModel only = Assert.Single(result.Value!);
        Assert.Equal("A", only.Name);
        Assert.Equal(["b"], only.Children.Select(c => c.Code));
    }

    [Fact]
    public async Task Preview_has_no_token_without_a_read_only_pair()
    {
        (LibraryService service, FakeClient client, _) = Rig();
        client.ReadOnlyToken = null;

        LibraryResult<ClipPreviewResponseModel> result = await service.GetPreviewAsync(11, default);

        Assert.False(result.Value!.Authenticated);
        Assert.Equal("https://demo.bbvms.com/p/default/c/11.html", result.Value.Url);
    }

    [Fact]
    public async Task Preview_uses_the_read_only_token_and_never_the_read_write_secret()
    {
        (LibraryService service, FakeClient client, FakeSettings settings) = Rig();
        client.ReadOnlyToken = "7-roHOTP";

        LibraryResult<ClipPreviewResponseModel> result = await service.GetPreviewAsync(11, default);

        Assert.True(result.Value!.Authenticated);
        Assert.EndsWith("?useSession=true&rpctoken=7-roHOTP", result.Value.Url);
        Assert.DoesNotContain(settings.Value.ApiSecret, result.Value.Url);
        // The only token source the library calls is GetReadOnlyToken: the client has no other way to mint one for a browser.
        Assert.DoesNotContain(client.Calls, c => c.Path.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Embed_code_segments_are_encoded()
    {
        (LibraryService service, FakeClient client, _) = Rig();
        client.Json("/sapi/embedcode/11/default/javascript", """{"body":"<script src=\"x\"></script>"}""");

        LibraryResult<EmbedCodeResponseModel> ok = await service.GetEmbedCodeAsync(11, "../x", default);

        // An invalid playout is replaced by the default; it never reaches the path.
        Assert.True(ok.Ok);
        Assert.DoesNotContain(client.Requests, r => r.Contains(".."));
    }

    // Controllers: the 502 mapping. ------------------------------------------------------------------------

    private sealed class FailingLibrary(SapiResult failure) : ILibraryService
    {
        public Task<LibraryResult<LibraryClipListResponseModel>> ListClipsAsync(LibraryFilter filter, int maxPageSize, CancellationToken ct) => Task.FromResult(new LibraryResult<LibraryClipListResponseModel>(null, failure));

        public Task<LibraryResult<LibraryPlaylistListResponseModel>> ListPlaylistsAsync(LibraryFilter filter, CancellationToken ct) => Task.FromResult(new LibraryResult<LibraryPlaylistListResponseModel>(null, failure));

        public Task<LibraryResult<IReadOnlyList<FolderResponseModel>>> GetFoldersAsync(CancellationToken ct) => Task.FromResult(new LibraryResult<IReadOnlyList<FolderResponseModel>>(null, failure));

        public Task<LibraryResult<LibraryClipResponseModel>> GetClipAsync(long id, CancellationToken ct) => Task.FromResult(new LibraryResult<LibraryClipResponseModel>(null, failure));

        public Task<LibraryResult<EmbedCodeResponseModel>> GetEmbedCodeAsync(long id, string? playout, CancellationToken ct) => Task.FromResult(new LibraryResult<EmbedCodeResponseModel>(null, failure));

        public Task<LibraryResult<ClipPreviewResponseModel>> GetPreviewAsync(long id, CancellationToken ct) => Task.FromResult(new LibraryResult<ClipPreviewResponseModel>(null, failure));

        public Task<LibraryResult<ClipPreviewResponseModel>> GetEmbedPreviewAsync(BlueBillywig.Umbraco.Embedding.BlueBillywigVideoType type, long id, string? playout, string? publication, CancellationToken ct) => Task.FromResult(new LibraryResult<ClipPreviewResponseModel>(null, failure));
    }

    [Fact]
    public async Task Controllers_answer_a_sapi_failure_with_502_and_the_generic_message()
    {
        var controller = new LibraryController(new FailingLibrary(SapiResult.Failure(401, "SAPI request failed (401)")), new FakeClient());

        foreach (IActionResult result in new[]
        {
            await controller.Clips(null, null, null, null, null, null, default),
            await controller.Livestreams(null, null, default),
            await controller.Playlists(null, null, default),
            await controller.Folders(default),
            await controller.Clip(5, default),
            await controller.EmbedCode(5, null, default),
            await controller.Preview(5, default),
        })
        {
            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(502, status.StatusCode);
            Assert.Contains("SAPI request failed (401)", status.Value!.ToString());
        }
    }

    [Fact]
    public async Task A_missing_clip_is_404_not_502()
    {
        var controller = new LibraryController(new FailingLibrary(SapiResult.Failure(404, "SAPI request failed (404)")), new FakeClient());

        Assert.IsType<NotFoundResult>(await controller.Clip(5, default));
    }

    [Fact]
    public async Task Invalid_filters_are_400_and_never_reach_sapi()
    {
        var client = new FakeClient();
        var controller = new LibraryController(new LibraryService(client, new FakeSettings()), client);

        Assert.IsType<BadRequestObjectResult>(await controller.Clips(null, "a b", null, null, null, null, default));
        Assert.IsType<BadRequestObjectResult>(await controller.Clips(null, null, "movie", null, null, null, default));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Delete_maps_a_sapi_failure_to_502_and_success_clears_the_cache()
    {
        var failing = new FakeClient { Handler = (_, _, _) => SapiResult.Failure(500, "SAPI request failed (500)") };
        Assert.Equal(502, Assert.IsType<ObjectResult>(await new LibraryController(new FailingLibrary(SapiResult.Failure(0, "x")), failing).Delete(5, default)).StatusCode);
        Assert.Equal(0, failing.Cleared);

        var ok = new FakeClient { Handler = (_, _, _) => FakeClient.Ok("{}") };
        Assert.IsType<OkObjectResult>(await new LibraryController(new FailingLibrary(SapiResult.Failure(0, "x")), ok).Delete(5, default));
        Assert.Equal("/sapi/mediaclip/5", ok.Calls.Single().Path);
        Assert.Equal(1, ok.Cleared);
    }

    [Fact]
    public async Task Upload_init_rejects_a_bad_status_with_400_and_creates_nothing()
    {
        var client = new FakeClient();
        var controller = new UploadController(new Upload.UploadService(client, new Upload.PendingUploads(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())), Microsoft.Extensions.Logging.Abstractions.NullLogger<Upload.UploadService>.Instance), new KeyedUser());

        IActionResult result = await controller.Init(new InitUploadRequestModel { Filename = "a.mp4", Filesize = 5, Status = "weird" }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(client.Calls);
    }

    private sealed class KeyedUser : Authorization.IBackOfficeUserInfo
    {
        public IReadOnlyCollection<string> GetGroupAliases(System.Security.Claims.ClaimsPrincipal principal) => [];

        public IReadOnlyCollection<string> GetAllowedSections(System.Security.Claims.ClaimsPrincipal principal) => [];

        public Guid? GetUserKey(System.Security.Claims.ClaimsPrincipal principal) => Guid.NewGuid();
    }
}

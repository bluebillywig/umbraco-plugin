using System.Text.Json;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.Upload;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// E21: the video picker and the library picker modal tell a failed fetch from "no results". That distinction lives in
/// the status the server answers (502 for a failure, 200 with an empty list for no results); the client shows an error
/// with a retry for the first and "No clips found" only for the second. The client half is checked in the browser.
/// </summary>
public class PickerErrorTests
{
    private static VideoController Controller(FakeClient client) => new(client, new LibraryService(client, new FakeSettings()));

    [Fact]
    public async Task A_failed_search_is_a_502_and_an_empty_search_is_a_200_with_no_items()
    {
        var down = new FakeClient { Handler = (_, _, _) => SapiResult.Failure(0, "SAPI request failed") };
        var failed = Assert.IsType<ObjectResult>(await Controller(down).Clips("cats", null, null, null, null, null, 0, default));
        Assert.Equal(502, failed.StatusCode);

        var up = new FakeClient();
        up.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        up.Json("/sapi/mediaclip", """{"numfound":0,"offset":0,"count":0,"items":[]}""");
        var empty = Assert.IsType<OkObjectResult>(await Controller(up).Clips("cats", null, null, null, null, null, 0, default));
        Assert.Empty(Assert.IsType<LibraryClipListResponseModel>(empty.Value).Items);
    }

    [Fact]
    public async Task An_answer_without_items_is_an_error_not_an_empty_picker()
    {
        var client = new FakeClient();
        client.Json("/sapi/publication", """{"defaultMediaAssetPath":"https://cdn.example"}""");
        client.Json("/sapi/mediaclip", """{"unexpected":true}""");

        var result = Assert.IsType<ObjectResult>(await Controller(client).Clips(null, null, null, null, null, null, 0, default));

        Assert.Equal(502, result.StatusCode);
    }

    [Fact]
    public async Task Every_picker_list_endpoint_answers_a_failure_with_502_and_no_upstream_text()
    {
        var down = new FakeClient { Handler = (_, _, _) => SapiResult.Failure(500, "SAPI request failed (500)") };
        VideoController controller = Controller(down);

        foreach (IActionResult result in new[]
        {
            await controller.Folders(default),
            await controller.Channels(default),
            await controller.Shorts(default),
            await controller.Playouts(default),
            await controller.Clip(5, default),
        })
        {
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(502, obj.StatusCode);
            Assert.DoesNotContain("<", JsonSerializer.Serialize(obj.Value));
        }
    }

    [Fact]
    public async Task No_channels_is_a_200_with_an_empty_list()
    {
        var client = new FakeClient();
        client.Json("/sapi/channel", """{"items":[]}""");

        var ok = Assert.IsType<OkObjectResult>(await Controller(client).Channels(default));

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<EntityOptionResponseModel>>(ok.Value));
    }
}

/// <summary>
/// E22: the upload limits are the reference plugin's: 20 GB, one file at a time, these audio and video types. The
/// server half (size) is tested here; the type and one-file limits are enforced by the upload view and observed in the UI.
/// </summary>
public class UploadLimitTests
{
    [Fact]
    public void Exactly_20_GB_is_accepted_and_one_byte_more_is_refused()
    {
        Assert.Equal(20L * 1024 * 1024 * 1024, UploadValidation.MaxFileSize);
        Assert.True(UploadValidation.TryParseInit(new InitUploadRequestModel { Filename = "a.mp4", Filesize = UploadValidation.MaxFileSize }, out _, out _));
        Assert.False(UploadValidation.TryParseInit(new InitUploadRequestModel { Filename = "a.mp4", Filesize = UploadValidation.MaxFileSize + 1 }, out _, out string error));
        Assert.Equal("File is too large", error);
    }

    // The view's constants live in upload-limits.ts. Reading the source keeps the two halves from drifting apart.
    [Fact]
    public void The_upload_views_size_limit_equals_the_servers()
    {
        string source = File.ReadAllText(FindClientSource("library", "upload-limits.ts"));
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(source, @"MAX_FILE_SIZE = ([\d_]+);");

        Assert.True(m.Success);
        Assert.Equal(UploadValidation.MaxFileSize, long.Parse(m.Groups[1].Value.Replace("_", "")));
    }

    [Fact]
    public void The_upload_views_file_types_are_the_reference_plugins_list()
    {
        string source = File.ReadAllText(FindClientSource("library", "upload-limits.ts"));
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(source, @"ALLOWED_TYPES = \[([^\]]+)\]");

        string[] types = m.Groups[1].Value.Split(',').Select(t => t.Trim().Trim('"')).ToArray();

        Assert.Equal(
            [".mp4", ".mov", ".avi", ".webm", ".ogg", ".mxf", ".mpg", ".mpeg", ".mkv", ".m4v", ".flv", ".wmv", ".mp3", ".wav", ".aac", ".flac", ".m4a"],
            types);
    }

    private static string FindClientSource(params string[] parts)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine([dir.FullName, "src", "BlueBillywig.Umbraco", "Client", "src", .. parts]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}

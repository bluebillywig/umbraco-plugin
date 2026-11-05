using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Library;

/// <summary>A value, or the failed SAPI call that stopped it. A failure is never turned into an empty value.</summary>
public sealed record LibraryResult<T>(T? Value, SapiResult? Failure) where T : class
{
    public bool Ok => Failure is null;
}

public interface ILibraryService
{
    Task<LibraryResult<LibraryClipListResponseModel>> ListClipsAsync(LibraryFilter filter, int maxPageSize, CancellationToken ct);

    Task<LibraryResult<LibraryPlaylistListResponseModel>> ListPlaylistsAsync(LibraryFilter filter, CancellationToken ct);

    Task<LibraryResult<IReadOnlyList<FolderResponseModel>>> GetFoldersAsync(CancellationToken ct);

    Task<LibraryResult<LibraryClipResponseModel>> GetClipAsync(long id, CancellationToken ct);

    Task<LibraryResult<EmbedCodeResponseModel>> GetEmbedCodeAsync(long id, string? playout, CancellationToken ct);

    /// <summary>The player URL for the details preview. The token is minted per call from the read-only key pair only.</summary>
    Task<LibraryResult<ClipPreviewResponseModel>> GetPreviewAsync(long id, CancellationToken ct);

    /// <summary>
    /// The player URL for the property editor's "Load preview": a clip, channel or shorts as the editor has configured it
    /// (<paramref name="playout"/> for a clip; <paramref name="publication"/> when the stored value names another one). The
    /// read-only token is added only for the configured publication.
    /// </summary>
    Task<LibraryResult<ClipPreviewResponseModel>> GetEmbedPreviewAsync(BlueBillywigVideoType type, long id, string? playout, string? publication, CancellationToken ct);
}

internal sealed class LibraryService : ILibraryService
{
    private readonly IBlueBillywigClient _client;
    private readonly IBlueBillywigSettingsStore _settings;

    private readonly IThumbnailState? _thumbnails;
    private readonly IClipPlayoutStore? _playouts;

    public LibraryService(IBlueBillywigClient client, IBlueBillywigSettingsStore settings, IThumbnailState? thumbnails = null, IClipPlayoutStore? playouts = null)
    {
        _client = client;
        _settings = settings;
        _thumbnails = thumbnails;
        _playouts = playouts;
    }

    public async Task<LibraryResult<LibraryClipListResponseModel>> ListClipsAsync(LibraryFilter filter, int maxPageSize, CancellationToken ct)
    {
        int pageSize = Math.Clamp(Math.Min(_settings.Get().PageCount, maxPageSize), 1, LibraryQuery.MaxPageSize);
        SapiResult result = await _client.GetAsync("/sapi/mediaclip", LibraryQuery.ToSapiQuery(filter, pageSize), ct: ct);
        if (!result.Ok)
        {
            return new(null, result);
        }

        // An answer without items is a malformed answer, not an empty library.
        if (result.Json?["items"] is not JsonArray items)
        {
            return new(null, SapiResult.Failure(502, "Unexpected response from Blue Billywig"));
        }

        string assetBase = await ClipThumbnails.AssetBaseAsync(_client, ct);
        List<LibraryClipResponseModel> clips = items.Select(i => LibraryMapper.ToClip(i, assetBase, _thumbnails)).OfType<LibraryClipResponseModel>().Take(pageSize).ToList();

        // numfound is the total for the query. `count` is only this page, and using it would collapse the pager.
        int total = (int)Math.Clamp(LibraryMapper.Int(result.Json["numfound"]), 0, int.MaxValue);
        return new(new LibraryClipListResponseModel { Items = clips, Total = total, Page = filter.Page, PageSize = pageSize }, null);
    }

    public async Task<LibraryResult<LibraryPlaylistListResponseModel>> ListPlaylistsAsync(LibraryFilter filter, CancellationToken ct)
    {
        BlueBillywigSettings settings = _settings.Get();
        int pageSize = Math.Clamp(settings.PageCount, 1, LibraryQuery.MaxPageSize);
        SapiResult result = await _client.GetAsync("/sapi/cliplist", LibraryQuery.ToPlaylistQuery(filter, pageSize), ct: ct);
        if (!result.Ok)
        {
            return new(null, result);
        }

        if (result.Json?["items"] is not JsonArray items)
        {
            return new(null, SapiResult.Failure(502, "Unexpected response from Blue Billywig"));
        }

        string playout = BlueBillywigVideo.IsValidPlayout(settings.Playout) ? settings.Playout : BlueBillywigVideo.DefaultClipPlayout;
        List<LibraryPlaylistResponseModel> lists = items
            .Select(i => LibraryMapper.ToPlaylist(i, LibraryMapper.PlaylistEmbedCode(settings.ResolveBaseUrl(), playout, LibraryMapper.Int(i?["id"]), settings.Embed)))
            .OfType<LibraryPlaylistResponseModel>()
            .Take(pageSize)
            .ToList();
        int total = (int)Math.Clamp(LibraryMapper.Int(result.Json["numfound"]), 0, int.MaxValue);
        return new(new LibraryPlaylistListResponseModel { Items = lists, Total = total, Page = filter.Page, PageSize = pageSize }, null);
    }

    public async Task<LibraryResult<IReadOnlyList<FolderResponseModel>>> GetFoldersAsync(CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync("/sapi/folder/tree", cacheFor: TimeSpan.FromMinutes(5), ct: ct);
        if (!result.Ok)
        {
            return new(null, result);
        }

        return result.Json?["items"] is JsonArray items
            ? new(LibraryMapper.ToFolders(items), null)
            : new(null, SapiResult.Failure(502, "Unexpected response from Blue Billywig"));
    }

    public async Task<LibraryResult<LibraryClipResponseModel>> GetClipAsync(long id, CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync($"/sapi/mediaclip/{id}", ct: ct);
        if (!result.Ok)
        {
            return new(null, result);
        }

        LibraryClipResponseModel? clip = LibraryMapper.ToClip(result.Json, await ClipThumbnails.AssetBaseAsync(_client, ct), _thumbnails);
        return clip is null ? new(null, SapiResult.Failure(502, "Unexpected response from Blue Billywig")) : new(clip, null);
    }

    public async Task<LibraryResult<EmbedCodeResponseModel>> GetEmbedCodeAsync(long id, string? playout, CancellationToken ct)
    {
        BlueBillywigSettings settings = _settings.Get();
        // The playout named in the call, then the one chosen for this clip in the editor, then the default (the plugin's order).
        string? chosen = _playouts?.Get(id);
        string usePlayout = BlueBillywigVideo.IsValidPlayout(playout) ? playout!
            : BlueBillywigVideo.IsValidPlayout(chosen) ? chosen!
            : BlueBillywigVideo.IsValidPlayout(settings.Playout) ? settings.Playout : BlueBillywigVideo.DefaultClipPlayout;
        SapiResult result = await _client.GetAsync(
            $"/sapi/embedcode/{id}/{BlueBillywigUrls.Segment(usePlayout)}/{BlueBillywigUrls.Segment(settings.Embed)}",
            cacheFor: TimeSpan.FromHours(1),
            ct: ct);
        if (!result.Ok)
        {
            return new(null, result);
        }

        string? body = result.Json is JsonObject obj && obj["body"] is JsonValue v && v.TryGetValue(out string? s) ? s : result.Json is null ? result.RawBody : null;
        return string.IsNullOrWhiteSpace(body)
            ? new(null, SapiResult.Failure(502, "Unexpected response from Blue Billywig"))
            : new(new EmbedCodeResponseModel { Code = _thumbnails?.VersionEmbedHtml(body.Trim(), id) ?? body.Trim() }, null);
    }

    public Task<LibraryResult<ClipPreviewResponseModel>> GetPreviewAsync(long id, CancellationToken ct) =>
        GetEmbedPreviewAsync(BlueBillywigVideoType.Clip, id, null, null, ct);

    public Task<LibraryResult<ClipPreviewResponseModel>> GetEmbedPreviewAsync(BlueBillywigVideoType type, long id, string? playout, string? publication, CancellationToken ct)
    {
        BlueBillywigSettings settings = _settings.Get();
        bool configured = string.IsNullOrEmpty(publication) || publication.Equals(settings.Publication, StringComparison.OrdinalIgnoreCase);
        string baseUrl = configured ? settings.ResolveBaseUrl() : BlueBillywigUrls.PublicationBaseUrl(publication!.ToLowerInvariant());

        // The playout named by the editor, then the one chosen for this clip in the library (same publication only), then the default.
        string? chosen = configured ? _playouts?.Get(id) : null;
        string usePlayout = BlueBillywigVideo.IsValidPlayout(playout) ? playout!
            : BlueBillywigVideo.IsValidPlayout(chosen) ? chosen!
            : BlueBillywigVideo.IsValidPlayout(settings.Playout) ? settings.Playout : BlueBillywigVideo.DefaultClipPlayout;
        string url = type switch
        {
            BlueBillywigVideoType.Channel => BlueBillywigUrls.ChannelPage(baseUrl, id),
            BlueBillywigVideoType.Shorts => BlueBillywigUrls.ShortsPage(baseUrl, id),
            _ => BlueBillywigUrls.ClipPage(baseUrl, usePlayout, id),
        };

        // The read-only pair only, and only for the configured publication (a token is worth nothing to another tenant and
        // something to this one). Without one the preview is the public player, which plays published clips and shows nothing
        // for drafts. The read-write token is never an option here: this URL reaches the browser.
        string? token = configured ? _client.GetReadOnlyToken() : null;
        if (!string.IsNullOrEmpty(token))
        {
            url += "?useSession=true&rpctoken=" + Uri.EscapeDataString(token);
        }

        return Task.FromResult(new LibraryResult<ClipPreviewResponseModel>(new ClipPreviewResponseModel { Url = url, Authenticated = !string.IsNullOrEmpty(token) }, null));
    }
}

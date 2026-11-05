using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BlueBillywig.Umbraco.Upload;

/// <summary>
/// Remembers which clips this server created for an upload, and for whom. Abort deletes a clip, so it is only
/// allowed for a clip that the same user started and that has not been completed. (The reference plugin deletes
/// whatever id it is sent, which lets anyone who can upload delete any clip.)
/// Held in memory: a restart forgets pending uploads, and those clips then have to be removed in the OVP.
/// </summary>
public interface IPendingUploads
{
    void Register(long clipId, string uploadId, Guid userKey);

    /// <summary>True, and forgets the entry, when this user started the upload and it is not completed.</summary>
    bool TryTakeForAbort(long clipId, Guid userKey, out string uploadId);

    /// <summary>Called when the upload is completed. Found by upload id, because SAPI's complete answer does not name the clip.</summary>
    void MarkCompleted(string uploadId);

    /// <summary>
    /// True when this user started the pending upload with this id. Signing a part and completing an upload act on someone's
    /// placeholder clip, so they are limited to the user who started it, like abort. An upload this server does not know (it
    /// was completed, expired, or a restart forgot it) belongs to nobody and is refused too.
    /// </summary>
    bool IsOwnedBy(string uploadId, Guid userKey);
}

internal sealed class PendingUploads : IPendingUploads
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly IMemoryCache _cache;

    public PendingUploads(IMemoryCache cache) => _cache = cache;

    private sealed record Entry(Guid UserKey, string UploadId);

    public bool IsOwnedBy(string uploadId, Guid userKey) =>
        _cache.TryGetValue(UploadKey(uploadId), out long clipId)
        && _cache.TryGetValue(ClipKey(clipId), out Entry? entry)
        && entry is not null
        && entry.UserKey == userKey
        && entry.UploadId == uploadId;

    public void Register(long clipId, string uploadId, Guid userKey)
    {
        _cache.Set(ClipKey(clipId), new Entry(userKey, uploadId), Lifetime);
        _cache.Set(UploadKey(uploadId), clipId, Lifetime);
    }

    public bool TryTakeForAbort(long clipId, Guid userKey, out string uploadId)
    {
        uploadId = "";
        if (_cache.TryGetValue(ClipKey(clipId), out Entry? entry) && entry is not null && entry.UserKey == userKey)
        {
            uploadId = entry.UploadId;
            _cache.Remove(ClipKey(clipId));
            _cache.Remove(UploadKey(entry.UploadId));
            return true;
        }

        return false;
    }

    public void MarkCompleted(string uploadId)
    {
        if (_cache.TryGetValue(UploadKey(uploadId), out long clipId))
        {
            _cache.Remove(ClipKey(clipId));
            _cache.Remove(UploadKey(uploadId));
        }
    }

    private static string ClipKey(long clipId) => "bb:upload:clip:" + clipId;

    private static string UploadKey(string uploadId) => "bb:upload:id:" + uploadId;
}

public sealed record UploadOutcome<T>(T? Value, SapiResult? Failure, int? Status = null, string? Error = null) where T : class
{
    public bool Ok => Failure is null && Error is null;
}

public interface IUploadService
{
    Task<UploadOutcome<InitUploadResponseModel>> InitAsync(NewClip clip, Guid userKey, CancellationToken ct);

    Task<UploadOutcome<SignPartResponseModel>> SignPartAsync(string uploadId, int partNumber, Guid userKey, CancellationToken ct);

    Task<UploadOutcome<CompleteUploadResponseModel>> CompleteAsync(string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts, Guid userKey, CancellationToken ct);

    Task<UploadOutcome<object>> AbortAsync(long clipId, Guid userKey, CancellationToken ct);
}

/// <summary>
/// Ports <c>SapiClient::initUpload</c>, <c>signUploadPart</c> and <c>completeUpload</c>. SAPI hands out presigned S3
/// multipart URLs; the browser PUTs the parts straight to storage, so no file bytes pass through this server.
/// </summary>
internal sealed class UploadService : IUploadService
{
    private static readonly IReadOnlyDictionary<string, string> TusHeaders = new Dictionary<string, string> { ["Tus-Resumable"] = "1.0.0" };

    private readonly IBlueBillywigClient _client;
    private readonly IPendingUploads _pending;
    private readonly ILogger<UploadService> _logger;

    public UploadService(IBlueBillywigClient client, IPendingUploads pending, ILogger<UploadService> logger)
    {
        _client = client;
        _pending = pending;
        _logger = logger;
    }

    public async Task<UploadOutcome<InitUploadResponseModel>> InitAsync(NewClip clip, Guid userKey, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = clip.Title,
            ["status"] = clip.Status,
            ["originalfilename"] = clip.Filename,
        };
        if (clip.Description is not null) body["description"] = clip.Description;
        if (clip.Tags.Count > 0) body["cat"] = clip.Tags;

        // The SDK's create is PUT /sapi/mediaclip with softsave=false.
        SapiResult created = await _client.SendAsync("PUT", "/sapi/mediaclip", new SapiRequest { Json = body, Query = new Dictionary<string, string> { ["softsave"] = "false" } }, ct);
        if (!created.Ok)
        {
            return new(null, created);
        }

        long clipId = LibraryMapper.Int(created.Json?["id"]);
        if (clipId <= 0)
        {
            return new(null, SapiResult.Failure(502, "Failed to create mediaclip"));
        }

        // From here the clip exists, so every failure removes it again: no orphan clip is left behind.
        var headers = new Dictionary<string, string>(TusHeaders)
        {
            ["Upload-Length"] = clip.Filesize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Upload-Metadata"] = UploadValidation.EncodeTusMetadata(
            [
                new("filename", clip.Filename),
                new("filetype", clip.ContentType),
                new("mediaclipId", clipId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]),
        };

        SapiResult tus;
        try
        {
            tus = await _client.SendAsync("POST", "/sapi/tus", new SapiRequest { Headers = headers }, ct);
        }
        catch
        {
            await RemoveOrphanAsync(clipId);
            throw;
        }

        InitUploadResponseModel? response = tus.Ok ? ToInitResponse(clipId, tus.Json) : null;
        if (response is null)
        {
            await RemoveOrphanAsync(clipId);
            return new(null, tus.Ok ? SapiResult.Failure(502, "Upload could not be initialised") : tus);
        }

        _pending.Register(clipId, response.UploadId, userKey);
        return new(response, null);
    }

    public async Task<UploadOutcome<SignPartResponseModel>> SignPartAsync(string uploadId, int partNumber, Guid userKey, CancellationToken ct)
    {
        if (!_pending.IsOwnedBy(uploadId, userKey))
        {
            return new(null, null, 403, "This upload cannot be changed");
        }

        SapiResult result = await _client.SendAsync(
            "GET",
            $"/sapi/tus/{BlueBillywigUrls.Segment(uploadId)}/sign/{partNumber}",
            new SapiRequest { Headers = TusHeaders },
            ct);
        if (!result.Ok)
        {
            return new(null, result);
        }

        string url = LibraryMapper.Str(result.Json?["url"]);
        if (url.Length == 0) url = LibraryMapper.Str(result.Json?["presignedUrl"]);
        return IsHttpsUrl(url)
            ? new(new SignPartResponseModel { Url = url }, null)
            : new(null, SapiResult.Failure(502, "Unexpected response from Blue Billywig"));
    }

    public async Task<UploadOutcome<CompleteUploadResponseModel>> CompleteAsync(string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts, Guid userKey, CancellationToken ct)
    {
        if (!_pending.IsOwnedBy(uploadId, userKey))
        {
            return new(null, null, 403, "This upload cannot be changed");
        }

        var body = new { parts = parts.Select(p => new { PartNumber = p.PartNumber, ETag = p.ETag }).ToList() };
        SapiResult result = await _client.SendAsync("POST", $"/sapi/tus/{BlueBillywigUrls.Segment(uploadId)}/complete", new SapiRequest { Json = body, Headers = TusHeaders }, ct);
        if (!result.Ok)
        {
            return new(null, result);
        }

        // After this the clip is being processed and abort must no longer delete it. SAPI's answer does not carry the
        // clip id (checked against bb.dev), so the upload id is what links the two.
        _pending.MarkCompleted(uploadId);

        string location = LibraryMapper.Str(result.Json?["location"]);
        return new(new CompleteUploadResponseModel { Location = IsHttpsUrl(location) ? location : null }, null);
    }

    public async Task<UploadOutcome<object>> AbortAsync(long clipId, Guid userKey, CancellationToken ct)
    {
        if (!_pending.TryTakeForAbort(clipId, userKey, out string uploadId))
        {
            return new(null, null, 403, "This upload cannot be aborted");
        }

        SapiResult result = await _client.DeleteAsync($"/sapi/mediaclip/{clipId}", ct);
        if (!result.Ok)
        {
            // Put it back so the caller can try again. A clip that was not removed is an orphan.
            _pending.Register(clipId, uploadId, userKey);
            return new(null, result);
        }

        return new(new object(), null);
    }

    private async Task RemoveOrphanAsync(long clipId)
    {
        SapiResult cleanup = await _client.DeleteAsync($"/sapi/mediaclip/{clipId}");
        if (!cleanup.Ok)
        {
            _logger.LogError("Failed to clean up orphan mediaclip {Id}: {Error}", clipId, cleanup.Error);
        }
    }

    private static InitUploadResponseModel? ToInitResponse(long clipId, JsonNode? tus)
    {
        if (tus is not JsonObject root)
        {
            return null;
        }

        JsonObject? s3 = root["s3"] as JsonObject;
        string uploadId = FirstNonEmpty(root["tusUploadId"], s3?["uploadId"], root["uploadId"]);
        long partSize = LibraryMapper.Int(s3?["partSize"] ?? root["partSize"]);
        JsonArray urls = (s3?["presignedUrls"] ?? root["presignedUrls"]) as JsonArray ?? [];
        var parts = urls.OfType<JsonObject>()
            .Select(p => (Number: (int)LibraryMapper.Int(p["partNumber"]), Url: LibraryMapper.Str(p["url"])))
            .Where(p => p.Number > 0 && IsHttpsUrl(p.Url))
            .Select(p => new PresignedPartResponseModel { PartNumber = p.Number, Url = p.Url })
            .ToList();

        if (!UploadValidation.IsUploadId(uploadId) || partSize <= 0)
        {
            return null;
        }

        return new InitUploadResponseModel
        {
            MediaclipId = clipId,
            UploadId = uploadId,
            Key = NullIfEmpty(FirstNonEmpty(s3?["key"], root["key"])),
            PartSize = partSize,
            PresignedUrls = parts,
        };
    }

    private static string FirstNonEmpty(params JsonNode?[] nodes) => nodes.Select(LibraryMapper.Str).FirstOrDefault(s => s.Length > 0) ?? "";

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    /// <summary>Presigned part URLs and the completed upload's location carry S3 signatures and go to the browser: https only.</summary>
    internal static bool IsHttpsUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;
}

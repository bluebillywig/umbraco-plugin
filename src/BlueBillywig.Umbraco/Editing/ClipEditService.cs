using System.Globalization;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Editing;

/// <summary>
/// What an edit call came to: a value, a failed SAPI call (a 502 for the caller), or a refusal with its own status
/// (400 for bad input, 404, 409, 422 with the names that were refused). Nothing here is ever a silent success.
/// </summary>
public sealed record EditOutcome<T>(T? Value, SapiResult? Failure, int? Status = null, string? Error = null, IReadOnlyList<string>? Dropped = null) where T : class
{
    public bool Ok => Failure is null && Error is null;

    public static EditOutcome<T> Of(T value) => new(value, null);

    public static EditOutcome<T> Fail(SapiResult failure) => new(null, failure);

    public static EditOutcome<T> Reject(int status, string error, IReadOnlyList<string>? dropped = null) => new(null, null, status, error, dropped);
}

public interface IClipEditService
{
    Task<EditOutcome<ClipEditResponseModel>> GetAsync(long id, CancellationToken ct);

    Task<EditOutcome<ClipEditResponseModel>> UpdateAsync(long id, ClipUpdateRequestModel request, CancellationToken ct);

    /// <summary>Status, folder or CPP on up to 50 clips, or delete. Each clip is checked and reported on its own.</summary>
    Task<EditOutcome<BulkResultResponseModel>> BulkAsync(string action, IReadOnlyList<long>? ids, string? value, CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> ListSubtitlesAsync(long id, CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> UploadSubtitleAsync(long id, SubtitleUploadRequestModel request, CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> DeleteSubtitleAsync(long id, long subtitleId, CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> SetDefaultSubtitleAsync(long id, long subtitleId, CancellationToken ct);

    Task<EditOutcome<ThumbnailsResponseModel>> UploadThumbnailAsync(long id, string? dataUri, CancellationToken ct);

    Task<EditOutcome<ThumbnailsResponseModel>> SetMainThumbnailAsync(long id, string? src, CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<string>>> SuggestTagsAsync(string? prefix, CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<CustomFieldGroupResponseModel>>> GetCustomFieldGroupsAsync(CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<LanguageResponseModel>>> GetLanguagesAsync(CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<PolicyResponseModel>>> GetPoliciesAsync(CancellationToken ct);

    Task<EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>>> GetPlayoutsAsync(CancellationToken ct);
}

/// <summary>
/// Ports the plugin's <c>update_clip_metadata</c>, <c>save_new_data_library_videos</c>, <c>update_clip_status</c>,
/// <c>bulk_action</c>, the subtitle and thumbnail handlers and the lookups behind the edit drawer. All SAPI access goes
/// through <see cref="IBlueBillywigClient"/>. Every write is validated before anything is sent, every write reads the clip
/// first (a PUT to a deleted id would make SAPI create an empty clip), and a 2xx answer that does not look like a saved
/// entity counts as a failure.
/// </summary>
internal sealed class ClipEditService : IClipEditService
{
    private static readonly SapiResult NotAccepted = SapiResult.Failure(502, "Blue Billywig did not accept the change");

    private readonly IBlueBillywigClient _client;
    private readonly IBlueBillywigSettingsStore _settings;
    private readonly IThumbnailState _thumbnails;
    private readonly IClipPlayoutStore _playouts;
    private readonly ICppLookup? _cpp;

    public ClipEditService(IBlueBillywigClient client, IBlueBillywigSettingsStore settings, IThumbnailState thumbnails, IClipPlayoutStore playouts, ICppLookup? cpp = null)
    {
        _cpp = cpp;
        _client = client;
        _settings = settings;
        _thumbnails = thumbnails;
        _playouts = playouts;
    }

    // ---- reading ---------------------------------------------------------------------------------------------

    public async Task<EditOutcome<ClipEditResponseModel>> GetAsync(long id, CancellationToken ct)
    {
        (JsonObject? clip, SapiResult? failure) = await ReadClipAsync(id, ct);
        if (clip is null)
        {
            return EditOutcome<ClipEditResponseModel>.Fail(failure!);
        }

        return EditOutcome<ClipEditResponseModel>.Of(await MapEditAsync(id, clip, ct));
    }

    private async Task<ClipEditResponseModel> MapEditAsync(long id, JsonObject clip, CancellationToken ct)
    {
        string assetBase = await ClipThumbnails.AssetBaseAsync(_client, ct);
        bool customFields = _settings.Get().EnableCustomFields;
        var values = new Dictionary<string, string>();
        if (customFields)
        {
            // A failed definition lookup leaves the section empty rather than failing the whole editor.
            SapiResult groups = await GetGroupsResultAsync(ct);
            if (groups.Ok)
            {
                foreach (string name in CustomFieldDefinitions.Allowlist(CustomFieldDefinitions.ParseGroups(groups.Json?["items"])).Values)
                {
                    values[name] = FieldText(clip[name]);
                }
            }
        }

        string iso = LibraryMapper.Str(clip["isocode"]);
        return new ClipEditResponseModel
        {
            Id = id,
            Title = LibraryMapper.Str(clip["title"]),
            Description = LibraryMapper.Str(clip["description"]),
            Tags = (clip["cat"] as JsonArray ?? []).Select(LibraryMapper.Str).Where(t => t.Length > 0).ToList(),
            Status = NullIfEmpty(LibraryMapper.Str(clip["status"])),
            Cpp = NullIfEmpty(LibraryMapper.Str(clip["cpp"])),
            FolderCode = NullIfEmpty(LibraryMapper.Str(clip["foldercode"])),
            Playout = _playouts.Get(id),
            Language = NullIfEmpty(iso),
            EmbargoFrom = ClipEditRules.EmbargoOrNull(LibraryMapper.Str(clip["embargoFrom"])),
            EmbargoTo = ClipEditRules.EmbargoOrNull(LibraryMapper.Str(clip["embargoTo"])),
            UpdatedDate = NullIfEmpty(LibraryMapper.Str(clip["updateddate"])),
            Width = (int)Math.Clamp(LibraryMapper.Int(clip["width"]), 0, int.MaxValue),
            Height = (int)Math.Clamp(LibraryMapper.Int(clip["height"]), 0, int.MaxValue),
            Thumbnails = MapThumbnails(_thumbnails.ResolveClip(id, clip), assetBase, ThumbnailLists.PrimaryCropset(clip)),
            Subtitles = MapSubtitles(clip["subtitles"]),
            CustomFields = values,
            CustomFieldsEnabled = customFields,
        };
    }

    // ---- update ----------------------------------------------------------------------------------------------

    public async Task<EditOutcome<ClipEditResponseModel>> UpdateAsync(long id, ClipUpdateRequestModel request, CancellationToken ct)
    {
        if (id is <= 0 or >= ClipEditRules.MaxId)
        {
            return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Clip ID is required");
        }

        // 1. Everything is validated before SAPI is asked anything.
        var body = new Dictionary<string, object?> { ["id"] = id.ToString(CultureInfo.InvariantCulture) };

        if (request.Title is not null)
        {
            string title = ClipEditRules.Title(request.Title);
            if (title.Length == 0)
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "A title is required");
            }

            body["title"] = title;
        }

        if (request.Description is not null)
        {
            body["description"] = ClipEditRules.Description(request.Description);
        }

        if (request.Tags is not null)
        {
            body["cat"] = ClipEditRules.Tags(request.Tags);
        }

        // Rejected, not dropped: SAPI answers 200 and ignores an unknown status or CPP, which would look like success.
        if (request.Status is not null)
        {
            string status = request.Status.Trim();
            if (!ClipEditRules.IsStatus(status))
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Invalid status value");
            }

            body["status"] = status;
        }

        if (request.Cpp is not null)
        {
            string cpp = request.Cpp.Trim();
            if (cpp.Length > 0 && !ClipEditRules.IsCppCode(cpp))
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Invalid CPP code");
            }

            body["cpp"] = cpp;
        }

        if (request.FolderCode is not null)
        {
            string folder = request.FolderCode.Trim();
            if (folder.Length > 0 && !ClipEditRules.IsFolderCode(folder))
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Invalid folder code");
            }

            body["foldercode"] = folder;
        }

        foreach ((string key, string? raw) in new[] { ("embargoFrom", request.EmbargoFrom), ("embargoTo", request.EmbargoTo) })
        {
            if (raw is null)
            {
                continue;
            }

            string value = raw.Trim();
            if (!ClipEditRules.IsEmbargo(value))
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Invalid embargo date");
            }

            body[key] = value;
        }

        string? playout = null;
        bool setPlayout = false;
        if (request.Playout is not null)
        {
            playout = request.Playout.Trim();
            if (playout.Length > 0 && !ClipEditRules.IsPlayout(playout))
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Invalid playout name");
            }

            setPlayout = true;
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            string iso = request.Language.Trim().ToLowerInvariant();
            if (!ClipEditRules.IsIsoCode(iso))
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Invalid language");
            }

            (long? languageId, SapiResult? languageFailure) = await ResolveLanguageIdAsync(iso, ct);
            if (languageFailure is not null)
            {
                return EditOutcome<ClipEditResponseModel>.Fail(languageFailure);
            }

            if (languageId is null)
            {
                return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Unknown language isocode: " + iso);
            }

            body["languageid"] = languageId.Value.ToString(CultureInfo.InvariantCulture);
        }

        // Custom fields: only names SAPI defines for clips. If SAPI cannot confirm the definitions the save fails with a
        // 502, so a value the editor typed is never dropped silently. A name that is not defined (a group deleted in
        // the OVP in the meantime) is a 422 naming it, and nothing at all is written.
        if (request.CustomFields is { Count: > 0 })
        {
            SapiResult groups = await GetGroupsResultAsync(ct);
            if (!groups.Ok)
            {
                return EditOutcome<ClipEditResponseModel>.Fail(groups);
            }

            Dictionary<string, string> allowed = CustomFieldDefinitions.Allowlist(CustomFieldDefinitions.ParseGroups(groups.Json?["items"]));
            var dropped = new List<string>();
            var fields = new Dictionary<string, object?>();
            foreach ((string name, string value) in request.CustomFields)
            {
                string key = ClipEditRules.SanitizeKey(name);
                if (!allowed.TryGetValue(key, out string? real))
                {
                    dropped.Add(key.Length > 0 ? key : "(invalid name)");
                    continue;
                }

                fields[real] = ClipEditRules.FieldValue(value);
            }

            if (dropped.Count > 0)
            {
                return EditOutcome<ClipEditResponseModel>.Reject(
                    StatusCodes.Status422UnprocessableEntity,
                    "These custom fields are no longer defined in Blue Billywig: " + string.Join(", ", dropped) + ". Reload the clip and try again.",
                    dropped);
            }

            foreach ((string name, object? value) in fields)
            {
                body[name] = value;
            }
        }

        if (body.Count == 1 && !setPlayout)
        {
            return EditOutcome<ClipEditResponseModel>.Reject(StatusCodes.Status400BadRequest, "Nothing to save");
        }

        // 2. The clip must exist: a PUT to a deleted id would create an empty clip.
        (JsonObject? existing, SapiResult? readFailure) = await ReadClipAsync(id, ct);
        if (existing is null)
        {
            return EditOutcome<ClipEditResponseModel>.Fail(readFailure!);
        }

        // 3. One PUT with every field. (The plugin sends custom fields in a second PUT.)
        SapiResult? put = null;
        if (body.Count > 1)
        {
            put = await _client.PutAsync($"/sapi/mediaclip/{id}", body, ct);

            // Dropped whether or not SAPI said yes: a write whose answer was lost may still have changed the policy, and a
            // newly protected clip must not play ungated for the lookup's five minutes. A dropped entry costs one SAPI read.
            if (body.ContainsKey("cpp"))
            {
                _cpp?.Invalidate(GateEntity.Clip(id));
            }

            SapiResult? failure = AsFailure(put);
            if (failure is not null)
            {
                return EditOutcome<ClipEditResponseModel>.Fail(failure);
            }
        }

        if (setPlayout)
        {
            _playouts.Set(id, playout);
        }

        // A new title must not be hidden by a browser's cached player data. (The gate's cached CPP decision is dropped above.)
        _thumbnails.Touch(id);
        _client.ClearCache();

        // SAPI's PUT answer can show the state before the write for some fields, so the result is read back.
        (JsonObject? fresh, SapiResult? freshFailure) = await ReadClipAsync(id, ct);
        if (fresh is null)
        {
            return EditOutcome<ClipEditResponseModel>.Fail(freshFailure!);
        }

        // Clearing an embargo: the reference plugin sends empty dates, and bb.dev answers 200 and keeps both dates (checked
        // 2026-11-05: "" and null change nothing; any other non-date makes both fields 2001-12-02T23:00:00Z, a past window).
        // So the empty value is tried first, as the reference does, and when the read-back still shows a date the clip is
        // written with the value SAPI does turn into "no embargo". If that does not clear it either, the save fails.
        (JsonObject? settled, SapiResult? clearFailure) = await ClearEmbargoAsync(id, body, fresh, ct);
        if (settled is null)
        {
            return EditOutcome<ClipEditResponseModel>.Fail(clearFailure!);
        }

        fresh = settled;

        // SAPI answers 200 and keeps the old status when it does not take the new one (checked on bb.dev: "archived" is
        // ignored for a clip, like "weird"). The status is the one field whose outcome the answers show, so it is checked:
        // a status that did not stick is a failure, and the rest of the save, which did go through, stays.
        if (body.TryGetValue("status", out object? wanted) && !StatusTook((string)wanted!, put, fresh))
        {
            return EditOutcome<ClipEditResponseModel>.Fail(NotAccepted);
        }

        return EditOutcome<ClipEditResponseModel>.Of(await MapEditAsync(id, fresh, ct));
    }

    /// <summary>
    /// The value that makes bb.dev drop an embargo date when the empty string is ignored. Not a date, so SAPI stores its own
    /// "past" marker, which <see cref="ClipEditRules.EmbargoOrNull"/> reads as no embargo.
    /// </summary>
    internal const string EmbargoClearFallback = "0";

    /// <summary>Makes sure an embargo field that was asked to be cleared reads back as cleared; one more write when it did not.</summary>
    private async Task<(JsonObject? Clip, SapiResult? Failure)> ClearEmbargoAsync(long id, Dictionary<string, object?> body, JsonObject fresh, CancellationToken ct)
    {
        List<string> stuck = new[] { "embargoFrom", "embargoTo" }
            .Where(k => body.TryGetValue(k, out object? v) && v is "" && ClipEditRules.EmbargoOrNull(LibraryMapper.Str(fresh[k])) is not null)
            .ToList();
        if (stuck.Count == 0)
        {
            return (fresh, null);
        }

        var retry = new Dictionary<string, object?> { ["id"] = id.ToString(CultureInfo.InvariantCulture) };
        foreach (string key in stuck)
        {
            retry[key] = EmbargoClearFallback;
        }

        SapiResult? failure = AsFailure(await _client.PutAsync($"/sapi/mediaclip/{id}", retry, ct));
        if (failure is not null)
        {
            return (null, failure);
        }

        _client.ClearCache();
        (JsonObject? again, SapiResult? readFailure) = await ReadClipAsync(id, ct);
        if (again is null)
        {
            return (null, readFailure);
        }

        // Shown as saved only when the read-back shows the embargo gone.
        return stuck.Any(k => ClipEditRules.EmbargoOrNull(LibraryMapper.Str(again[k])) is not null) ? (null, NotAccepted) : (again, null);
    }

    /// <summary>Whether SAPI really holds the status that was written: the PUT answer or a fresh read shows it.</summary>
    private static bool StatusTook(string wanted, SapiResult? put, JsonNode? fresh) =>
        LibraryMapper.Str(put?.Json?["status"]) == wanted || LibraryMapper.Str(fresh?["status"]) == wanted;

    // ---- bulk ------------------------------------------------------------------------------------------------

    public async Task<EditOutcome<BulkResultResponseModel>> BulkAsync(string action, IReadOnlyList<long>? ids, string? value, CancellationToken ct)
    {
        if (!ClipEditRules.TryParseBulkIds(ids, out List<long> clipIds, out string idError))
        {
            return EditOutcome<BulkResultResponseModel>.Reject(StatusCodes.Status400BadRequest, idError);
        }

        string parsed = "";
        if (action == "delete")
        {
            // Delete has no value. The destructive policy is enforced on its own endpoint.
        }
        else if (!ClipEditRules.TryParseBulkValue(action, value, out parsed, out string valueError))
        {
            return EditOutcome<BulkResultResponseModel>.Reject(StatusCodes.Status400BadRequest, valueError);
        }

        if (!_client.IsConnected)
        {
            return EditOutcome<BulkResultResponseModel>.Fail(SapiResult.Failure(0, "Blue Billywig API not configured"));
        }

        var succeeded = new List<long>();
        var errors = new List<BulkErrorResponseModel>();
        foreach (long id in clipIds)
        {
            string? error = await BulkOneAsync(action, id, parsed, ct);
            if (error is null)
            {
                succeeded.Add(id);
            }
            else
            {
                errors.Add(new BulkErrorResponseModel { Id = id, Error = error });
            }
        }

        if (succeeded.Count > 0)
        {
            _client.ClearCache();
        }

        return EditOutcome<BulkResultResponseModel>.Of(new BulkResultResponseModel { Success = succeeded.Count, Failed = errors.Count, Succeeded = succeeded, Errors = errors });
    }

    /// <summary>One clip of a bulk action. Returns null on success, otherwise a message that is safe to show (never an upstream body).</summary>
    private async Task<string?> BulkOneAsync(string action, long id, string value, CancellationToken ct)
    {
        (JsonObject? clip, SapiResult? readFailure) = await ReadClipAsync(id, ct);
        if (clip is null)
        {
            return readFailure!.StatusCode == StatusCodes.Status404NotFound ? "Clip not found" : "Blue Billywig could not be reached for this clip";
        }

        SapiResult result;
        if (action == "delete")
        {
            result = await _client.DeleteAsync($"/sapi/mediaclip/{id}", ct);
            _cpp?.Invalidate(GateEntity.Clip(id));
            if (result.Ok)
            {
                _playouts.Set(id, null);
            }
        }
        else
        {
            string field = action switch { "status" => "status", "folder" => "foldercode", _ => "cpp" };
            result = await _client.PutAsync($"/sapi/mediaclip/{id}", new Dictionary<string, object?> { ["id"] = id.ToString(CultureInfo.InvariantCulture), [field] = value }, ct);
            if (field == "cpp")
            {
                _cpp?.Invalidate(GateEntity.Clip(id));
            }

            if (result.Ok)
            {
                _thumbnails.Touch(id);
            }
        }

        SapiResult? failure = action == "delete" ? (result.Ok ? null : result) : AsFailure(result);

        // A status SAPI ignores is a 200 with the old status in the answer. Counted as done it would be a silent no-op.
        if (failure is null && action == "status" && LibraryMapper.Str(result.Json?["status"]) != value)
        {
            (JsonObject? after, _) = await ReadClipAsync(id, ct);
            if (LibraryMapper.Str(after?["status"]) != value)
            {
                failure = NotAccepted;
            }
        }

        return failure is null ? null : "Blue Billywig did not accept the change";
    }

    // ---- subtitles -------------------------------------------------------------------------------------------

    public async Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> ListSubtitlesAsync(long id, CancellationToken ct)
    {
        (JsonObject? clip, SapiResult? failure) = await ReadClipAsync(id, ct);
        return clip is null ? EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(failure!) : EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Of(MapSubtitles(clip["subtitles"]));
    }

    public async Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> UploadSubtitleAsync(long id, SubtitleUploadRequestModel request, CancellationToken ct)
    {
        const int Bad = StatusCodes.Status400BadRequest;
        string iso = (request.IsoCode ?? "").Trim().ToLowerInvariant();
        if (id is <= 0 or >= ClipEditRules.MaxId || iso.Length == 0)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(Bad, "clip_id and isocode are required");
        }

        if (string.IsNullOrEmpty(request.Content))
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(Bad, "No subtitle file uploaded");
        }

        // The size is measured on the contents, not on what the browser claims.
        if (SubtitleRules.ByteCount(request.Content) > SubtitleRules.MaxBytes)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(Bad, "Subtitle file too large (max 1 MB)");
        }

        string? ext = SubtitleRules.Extension(request.FileName);
        if (ext is null)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(Bad, "Only .vtt and .srt files are supported");
        }

        if (!SubtitleRules.LooksLikeSubtitle(request.Content, ext))
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(Bad, "File content does not look like a valid " + ext.ToUpperInvariant() + " subtitle");
        }

        (long? languageId, SapiResult? languageFailure) = await ResolveLanguageIdAsync(iso, ct);
        if (languageFailure is not null)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(languageFailure);
        }

        if (languageId is null)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(Bad, "Unknown language isocode: " + iso);
        }

        (JsonObject? clip, SapiResult? readFailure) = await ReadClipAsync(id, ct);
        if (clip is null)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(readFailure!);
        }

        // One POST: SAPI's /sapi/subtitle takes the srt text in the body and stores it, as the plugin does.
        SapiResult? failure = AsFailure(await _client.PostAsync("/sapi/subtitle", new Dictionary<string, object?>
        {
            ["mediaclipid"] = id.ToString(CultureInfo.InvariantCulture),
            ["languageid"] = languageId.Value,
            ["name"] = UploadValidationText(request.Name, 100),
            ["default"] = request.Default,
            ["status"] = request.Publish ? "published" : "draft",
            ["subtype"] = "subtitle",
            ["srt"] = request.Content,
        }, ct));
        if (failure is not null)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(failure);
        }

        _client.ClearCache();
        return await ListSubtitlesAsync(id, ct);
    }

    public async Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> DeleteSubtitleAsync(long id, long subtitleId, CancellationToken ct)
    {
        if (id is <= 0 or >= ClipEditRules.MaxId || subtitleId is <= 0 or >= ClipEditRules.MaxId)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(StatusCodes.Status400BadRequest, "clip_id and subtitle_id required");
        }

        // The plugin deletes whatever subtitle id it is sent. Here the subtitle must belong to the clip named in the call.
        (JsonObject? clip, SapiResult? failure) = await ReadClipAsync(id, ct);
        if (clip is null)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(failure!);
        }

        if (!MapSubtitles(clip["subtitles"]).Any(s => s.Id == subtitleId))
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(StatusCodes.Status404NotFound, "Subtitle does not belong to this clip");
        }

        SapiResult result = await _client.DeleteAsync($"/sapi/subtitle/{subtitleId}", ct);
        if (!result.Ok)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(result);
        }

        _client.ClearCache();
        return await ListSubtitlesAsync(id, ct);
    }

    public async Task<EditOutcome<IReadOnlyList<SubtitleResponseModel>>> SetDefaultSubtitleAsync(long id, long subtitleId, CancellationToken ct)
    {
        if (id is <= 0 or >= ClipEditRules.MaxId || subtitleId is <= 0 or >= ClipEditRules.MaxId)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(StatusCodes.Status400BadRequest, "clip_id and subtitle_id required");
        }

        (JsonObject? clip, SapiResult? failure) = await ReadClipAsync(id, ct);
        if (clip is null)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(failure!);
        }

        IReadOnlyList<SubtitleResponseModel> siblings = MapSubtitles(clip["subtitles"]);
        if (!siblings.Any(s => s.Id == subtitleId))
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Reject(StatusCodes.Status404NotFound, "Subtitle does not belong to this clip");
        }

        // The new default is set first and the previous one cleared after it, as the plugin does, so a failure in between
        // leaves two defaults and not none. Either failure makes the whole call a 502.
        bool failed = AsFailure(await _client.PutAsync($"/sapi/subtitle/{subtitleId}", new Dictionary<string, object?> { ["id"] = subtitleId, ["default"] = "true" }, ct)) is not null;
        foreach (SubtitleResponseModel previous in siblings.Where(s => s.Default && s.Id != subtitleId))
        {
            failed |= AsFailure(await _client.PutAsync($"/sapi/subtitle/{previous.Id}", new Dictionary<string, object?> { ["id"] = previous.Id, ["default"] = "false" }, ct)) is not null;
        }

        _client.ClearCache();
        if (failed)
        {
            return EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(SapiResult.Failure(502, "Default subtitle partially updated"));
        }

        // SAPI answers 200 "metadata has been updated" even when the flag did not change. On bb.dev the default subtitle is the one
        // in the publication's default language and a PUT of the flag changes nothing, so the answer is checked against a fresh read.
        EditOutcome<IReadOnlyList<SubtitleResponseModel>> listed = await ListSubtitlesAsync(id, ct);
        return listed.Ok && !listed.Value!.Any(s => s.Id == subtitleId && s.Default)
            ? EditOutcome<IReadOnlyList<SubtitleResponseModel>>.Fail(NotAccepted)
            : listed;
    }

    // ---- thumbnails ------------------------------------------------------------------------------------------

    public async Task<EditOutcome<ThumbnailsResponseModel>> UploadThumbnailAsync(long id, string? dataUri, CancellationToken ct)
    {
        const int Bad = StatusCodes.Status400BadRequest;
        if (id is <= 0 or >= ClipEditRules.MaxId || string.IsNullOrEmpty(dataUri))
        {
            return EditOutcome<ThumbnailsResponseModel>.Reject(Bad, "clip_id and thumbnail_data required");
        }

        if (dataUri.Length > ThumbnailValidator.MaxDataLength)
        {
            return EditOutcome<ThumbnailsResponseModel>.Reject(Bad, "Image too large (max 10 MB)");
        }

        (JsonObject? clip, SapiResult? failure) = await ReadClipAsync(id, ct);
        if (clip is null)
        {
            return EditOutcome<ThumbnailsResponseModel>.Fail(failure!);
        }

        string? invalid = ThumbnailValidator.Validate(dataUri, (int)LibraryMapper.Int(clip["width"]), (int)LibraryMapper.Int(clip["height"]));
        if (invalid is not null)
        {
            return EditOutcome<ThumbnailsResponseModel>.Reject(Bad, invalid);
        }

        // SAPI's read lags behind a recent write: building on it would drop a thumbnail uploaded a moment ago.
        JsonArray thumbs = _thumbnails.ResolveClip(id, clip).Thumbnails;

        // The new image becomes the main thumbnail of the video's own crop set; other crop sets keep their main.
        string cropset = ThumbnailLists.UploadCropset(thumbs, ThumbnailLists.PrimaryCropset(clip));
        thumbs = ThumbnailLists.WithUpload(thumbs, dataUri, cropset);
        return await WriteThumbnailsAsync(id, clip, thumbs, ct);
    }

    public async Task<EditOutcome<ThumbnailsResponseModel>> SetMainThumbnailAsync(long id, string? src, CancellationToken ct)
    {
        if (id is <= 0 or >= ClipEditRules.MaxId || string.IsNullOrWhiteSpace(src))
        {
            return EditOutcome<ThumbnailsResponseModel>.Reject(StatusCodes.Status400BadRequest, "clip_id and thumbnail src required");
        }

        (JsonObject? clip, SapiResult? failure) = await ReadClipAsync(id, ct);
        if (clip is null)
        {
            return EditOutcome<ThumbnailsResponseModel>.Fail(failure!);
        }

        JsonArray thumbs = _thumbnails.ResolveClip(id, clip).Thumbnails;

        // The src, not an index: the order can change once SAPI's read catches up, and a src that is gone means the editor
        // shows an outdated list, where an index would point at a different image.
        int index = ThumbnailLists.IndexOf(thumbs, src.Trim());
        if (index < 0)
        {
            return EditOutcome<ThumbnailsResponseModel>.Reject(StatusCodes.Status409Conflict, "The thumbnails changed in the meantime. The list has been reloaded; pick the thumbnail again.");
        }

        string primary = ThumbnailLists.PrimaryCropset(clip);
        string cropset = ThumbnailLists.UploadCropset(thumbs, primary);
        thumbs = ThumbnailLists.WithMain(thumbs, index, cropset, primary);
        return await WriteThumbnailsAsync(id, clip, thumbs, ct);
    }

    /// <summary>The PUT of a thumbnail change, and what it makes of SAPI's answer (the plugin's <c>confirmed_thumbnails</c>).</summary>
    private async Task<EditOutcome<ThumbnailsResponseModel>> WriteThumbnailsAsync(long id, JsonObject clip, JsonArray sent, CancellationToken ct)
    {
        SapiResult result = await _client.PutAsync($"/sapi/mediaclip/{id}", new JsonObject { ["id"] = id.ToString(CultureInfo.InvariantCulture), ["thumbnails"] = sent.DeepClone() }, ct);
        SapiResult? failure = AsFailure(result);
        if (failure is not null)
        {
            return EditOutcome<ThumbnailsResponseModel>.Fail(failure);
        }

        _client.ClearCache();
        string? notice = null;
        if (result.Json?["thumbnails"] is JsonArray { Count: > 0 } confirmed)
        {
            // The reply's updateddate is not passed on: bb.dev answers a PUT with the entity's date from before the write, which
            // equals the date of the read this edit started from, so the date comparison would call that stale read "as new
            // as the write" and show the old list straight after saving. Without a date the list is held for HOLD instead.
            _thumbnails.Remember(id, confirmed);
        }
        else if (sent.OfType<JsonObject>().Any(t => ThumbnailLists.Src(t).Length == 0))
        {
            // An upload has no src for its new image yet, so there is nothing to remember. The write still counts as done
            // (SAPI may have stored the image, and a retry could store a second copy), the player URLs are versioned, and
            // the reply carries a notice instead of an error.
            _thumbnails.Touch(id);
            notice = "Blue Billywig did not confirm the new thumbnail yet. Reopen the clip in a few minutes to check it before you change thumbnails.";
        }
        else
        {
            _thumbnails.Remember(id, sent);
        }

        string assetBase = await ClipThumbnails.AssetBaseAsync(_client, ct);
        ResolvedThumbnails resolved = _thumbnails.ResolveClip(id, clip);
        return EditOutcome<ThumbnailsResponseModel>.Of(new ThumbnailsResponseModel { Thumbnails = MapThumbnails(resolved, assetBase, ThumbnailLists.PrimaryCropset(clip)), Notice = notice });
    }

    // ---- lookups ---------------------------------------------------------------------------------------------

    public async Task<EditOutcome<IReadOnlyList<string>>> SuggestTagsAsync(string? prefix, CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync(
            "/sapi/mediaclip",
            new Dictionary<string, string> { ["limit"] = "500", ["sort"] = "createddate desc", ["fields"] = "cat" },
            TimeSpan.FromMinutes(5),
            ct);
        if (!result.Ok)
        {
            return EditOutcome<IReadOnlyList<string>>.Fail(result);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<string>();
        foreach (JsonObject item in (result.Json?["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (string tag in (item["cat"] as JsonArray ?? []).Select(LibraryMapper.Str))
            {
                if (tag.Length > 0 && seen.Add(tag))
                {
                    all.Add(tag);
                }
            }
        }

        all.Sort(StringComparer.OrdinalIgnoreCase);
        string needle = (prefix ?? "").Trim();
        IEnumerable<string> matches = needle.Length == 0 ? all : all.Where(t => t.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        return EditOutcome<IReadOnlyList<string>>.Of(matches.Take(20).ToList());
    }

    public async Task<EditOutcome<IReadOnlyList<CustomFieldGroupResponseModel>>> GetCustomFieldGroupsAsync(CancellationToken ct)
    {
        SapiResult result = await GetGroupsResultAsync(ct);
        return result.Ok
            ? EditOutcome<IReadOnlyList<CustomFieldGroupResponseModel>>.Of(CustomFieldDefinitions.ParseGroups(result.Json?["items"]))
            : EditOutcome<IReadOnlyList<CustomFieldGroupResponseModel>>.Fail(result);
    }

    public async Task<EditOutcome<IReadOnlyList<LanguageResponseModel>>> GetLanguagesAsync(CancellationToken ct)
    {
        SapiResult result = await GetLanguagesResultAsync(ct);
        if (!result.Ok)
        {
            return EditOutcome<IReadOnlyList<LanguageResponseModel>>.Fail(result);
        }

        return EditOutcome<IReadOnlyList<LanguageResponseModel>>.Of(Languages(result.Json));
    }

    public async Task<EditOutcome<IReadOnlyList<PolicyResponseModel>>> GetPoliciesAsync(CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync("/sapi/contentprotectionpolicy", new Dictionary<string, string> { ["q"] = "*" }, TimeSpan.FromHours(1), ct);
        if (!result.Ok)
        {
            return EditOutcome<IReadOnlyList<PolicyResponseModel>>.Fail(result);
        }

        // Only the code and name reach the dropdown. A policy without a valid uuid code could not be written anyway.
        List<PolicyResponseModel> policies = (result.Json?["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => new { Code = LibraryMapper.Str(p["code"]), Name = LibraryMapper.Str(p["name"]), Status = LibraryMapper.Str(p["status"]) })
            .Where(p => ClipEditRules.IsCppCode(p.Code))
            .Select(p => new PolicyResponseModel { Code = p.Code, Name = p.Name.Length > 0 ? p.Name : p.Code, Status = p.Status })
            .ToList();
        return EditOutcome<IReadOnlyList<PolicyResponseModel>>.Of(policies);
    }

    public async Task<EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>>> GetPlayoutsAsync(CancellationToken ct)
    {
        SapiResult result = await _client.GetAsync("/sapi/playout/", cacheFor: TimeSpan.FromHours(1), ct: ct);
        if (!result.Ok)
        {
            return EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>>.Fail(result);
        }

        List<PlayoutOptionResponseModel> playouts = (result.Json?["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => new PlayoutOptionResponseModel { Label = LibraryMapper.Str(p["name"]), Value = LibraryMapper.Str(p["label"]) })
            .Where(p => ClipEditRules.IsPlayout(p.Value))
            .Select(p => p.Label.Length == 0 ? new PlayoutOptionResponseModel { Label = p.Value, Value = p.Value } : p)
            .ToList();
        if (playouts.Count == 0)
        {
            playouts.Add(new PlayoutOptionResponseModel { Label = "Default", Value = "default" });
        }

        return EditOutcome<IReadOnlyList<PlayoutOptionResponseModel>>.Of(playouts);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>The clip, read fresh (never from the cache) before a write or for the editor. A clip that is not an entity is a 502.</summary>
    private async Task<(JsonObject? Clip, SapiResult? Failure)> ReadClipAsync(long id, CancellationToken ct)
    {
        if (id is <= 0 or >= ClipEditRules.MaxId)
        {
            return (null, SapiResult.Failure(StatusCodes.Status404NotFound, "Invalid id"));
        }

        SapiResult result = await _client.GetAsync($"/sapi/mediaclip/{id}", ct: ct);
        if (!result.Ok)
        {
            return (null, result);
        }

        return result.Json is JsonObject clip && LibraryMapper.Int(clip["id"]) > 0
            ? (clip, null)
            : (null, SapiResult.Failure(502, "Unexpected response from Blue Billywig"));
    }

    /// <summary>
    /// Whether a write that SAPI answered really was accepted. The client already turns non-2xx into a failure; this also
    /// refuses a 2xx whose body is not an entity or carries an error (SAPI can answer 200 with <c>{"Error": ...}</c> or
    /// <c>{"error": true, "body": ...}</c>, and the plugin's <c>put_failure</c> treats those as failures). The body itself is not passed on.
    /// </summary>
    internal static SapiResult? AsFailure(SapiResult result)
    {
        if (!result.Ok)
        {
            return result;
        }

        if (result.Json is not JsonObject obj)
        {
            return NotAccepted;
        }

        if (obj["Error"] is not null && LibraryMapper.Str(obj["Error"]).Length > 0)
        {
            return NotAccepted;
        }

        return obj["error"] is JsonValue v && ((v.TryGetValue(out bool b) && b) || (v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s) && s is not "false" and not "0"))
            ? NotAccepted
            : null;
    }

    private Task<SapiResult> GetGroupsResultAsync(CancellationToken ct) =>
        _client.GetAsync("/sapi/customfieldgroup", new Dictionary<string, string> { ["limit"] = "100" }, TimeSpan.FromMinutes(10), ct);

    private Task<SapiResult> GetLanguagesResultAsync(CancellationToken ct) =>
        _client.GetAsync("/sapi/language", new Dictionary<string, string> { ["limit"] = "500" }, TimeSpan.FromDays(1), ct);

    private static IReadOnlyList<LanguageResponseModel> Languages(JsonNode? json) =>
        (json?["items"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(l => new { Id = LibraryMapper.Int(l["id"]), Iso = LibraryMapper.Str(l["isocode"]), Name = LibraryMapper.Str(l["languagename"]) is { Length: > 0 } n ? n : LibraryMapper.Str(l["name"]) })
            .Where(l => l.Id > 0 && l.Iso.Length > 0)
            .Select(l => new LanguageResponseModel { Id = l.Id, IsoCode = l.Iso, Name = l.Name.Length > 0 ? l.Name : l.Iso })
            .ToList();

    private async Task<(long? Id, SapiResult? Failure)> ResolveLanguageIdAsync(string iso, CancellationToken ct)
    {
        SapiResult result = await GetLanguagesResultAsync(ct);
        if (!result.Ok)
        {
            return (null, result);
        }

        LanguageResponseModel? match = Languages(result.Json).FirstOrDefault(l => l.IsoCode.Equals(iso, StringComparison.OrdinalIgnoreCase));
        return (match?.Id, null);
    }

    private static IReadOnlyList<SubtitleResponseModel> MapSubtitles(JsonNode? node) =>
        (node as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(s => new SubtitleResponseModel
            {
                Id = LibraryMapper.Int(s["id"]),
                Name = LibraryMapper.Str(s["name"]),
                IsoCode = NullIfEmpty(LibraryMapper.Str(s["isocode"])),
                LanguageName = NullIfEmpty(LibraryMapper.Str(s["languagename"])),
                Default = ThumbnailLists.IsTrue(s["default"]),
                Status = NullIfEmpty(LibraryMapper.Str(s["status"])),
            })
            .Where(s => s.Id > 0)
            .ToList();

    private static IReadOnlyList<ThumbnailResponseModel> MapThumbnails(ResolvedThumbnails resolved, string assetBase, string primary)
    {
        string main = ThumbnailLists.MainSrc(resolved.Thumbnails, primary);
        return resolved.Thumbnails
            .OfType<JsonObject>()
            .Select(t =>
            {
                string src = ThumbnailLists.Src(t);
                return new ThumbnailResponseModel
                {
                    Src = src.Length > 0 ? src : null,
                    Url = src.Length > 0 ? ClipThumbnails.Absolute(assetBase, src) : null,
                    Main = src.Length > 0 && src == main,
                    Unconfirmed = src.Length == 0 || resolved.Unconfirmed.Contains(src),
                };
            })
            .ToList();
    }

    private static string FieldText(JsonNode? node) => node switch
    {
        // SAPI returns an explicit null for an unset typed field. String(null) must not become the text "null" and be saved.
        null => "",
        JsonArray array => string.Join(", ", array.Select(LibraryMapper.Str)),
        JsonValue => LibraryMapper.Str(node),
        _ => "",
    };

    private static string UploadValidationText(string? text, int max) => Upload.UploadValidation.SanitizeText(text, max, multiline: false);

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;
}

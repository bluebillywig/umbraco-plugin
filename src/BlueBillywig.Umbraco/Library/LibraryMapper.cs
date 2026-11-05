using System.Globalization;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.ViewModels;

namespace BlueBillywig.Umbraco.Library;

/// <summary>Maps SAPI JSON to the response models. Nothing from SAPI is passed through untouched.</summary>
public static class LibraryMapper
{
    /// <param name="thumbnails">When given, the main thumbnail comes from the list a recent write confirmed (the plugin shows the main thumbnail the same way everywhere).</param>
    public static LibraryClipResponseModel? ToClip(JsonNode? node, string assetBase, IThumbnailState? thumbnails = null)
    {
        if (node is not JsonObject clip)
        {
            return null;
        }

        long id = Int(clip["id"]);
        if (id <= 0)
        {
            return null;
        }

        string title = Str(clip["title"]);
        if (title.Length == 0)
        {
            title = Str(clip["originalfilename"]);
        }

        int length = (int)Math.Clamp(Int(clip["length"]), 0, int.MaxValue);
        return new LibraryClipResponseModel
        {
            Id = id,
            Title = title.Length > 0 ? title : $"Clip {id}",
            Description = Str(clip["description"]),
            Tags = (clip["cat"] as JsonArray ?? []).Select(Str).Where(t => t.Length > 0).ToList(),
            Status = NullIfEmpty(Str(clip["status"])),
            MediaType = NullIfEmpty(Str(clip["mediatype"])),
            SourceType = NullIfEmpty(Str(clip["sourcetype"])),
            Length = length > 0 ? length : null,
            CreatedDate = NullIfEmpty(Str(clip["createddate"])),
            UpdatedDate = NullIfEmpty(Str(clip["updateddate"])),
            ThumbnailUrl = ClipThumbnails.Absolute(assetBase, thumbnails is null ? ClipThumbnails.MainSrc(clip) : thumbnails.ClipMainSrc(id, clip)),
            PreviewUrl = ClipThumbnails.Absolute(assetBase, Str((clip["movingThumbnails"] as JsonArray)?.FirstOrDefault()?["src"])),
        };
    }

    public static LibraryPlaylistResponseModel? ToPlaylist(JsonNode? node, string embedCode)
    {
        if (node is not JsonObject list)
        {
            return null;
        }

        long id = Int(list["id"]);
        if (id <= 0)
        {
            return null;
        }

        string title = Str(list["title"]);
        return new LibraryPlaylistResponseModel
        {
            Id = id,
            Title = title.Length > 0 ? title : $"Playlist {id}",
            Status = NullIfEmpty(Str(list["status"])),
            CreatedDate = NullIfEmpty(Str(list["createddate"])),
            UpdatedDate = NullIfEmpty(Str(list["updateddate"])),
            EmbedCode = embedCode,
        };
    }

    /// <summary>The embed code for a playlist. SAPI's embedcode endpoint only knows clips, so it is built from the player URL patterns.</summary>
    public static string PlaylistEmbedCode(string baseUrl, string playout, long id, string embedType)
    {
        string js = BlueBillywigUrls.PlaylistScript(baseUrl, playout, id);
        string html = BlueBillywigUrls.PlaylistPage(baseUrl, playout, id);
        return embedType switch
        {
            "iframe" => $"<iframe title=\"Video player\" src=\"{EmbedHtml.Encoder.Encode(html)}\" width=\"640\" height=\"360\" frameborder=\"0\" allowfullscreen></iframe>",
            "url" => js,
            _ => $"<script type=\"text/javascript\" src=\"{EmbedHtml.Encoder.Encode(js)}\"></script>",
        };
    }

    /// <summary>Normalises <c>/sapi/folder/tree</c> items. Folders without a valid code are dropped: they could not be filtered on anyway.</summary>
    public static List<FolderResponseModel> ToFolders(JsonNode? items, int depth = 0)
    {
        var result = new List<FolderResponseModel>();
        if (items is not JsonArray array || depth > 12)
        {
            return result;
        }

        foreach (JsonObject folder in array.OfType<JsonObject>())
        {
            string code = Str(folder["code"]);
            if (!LibraryQuery.IsFolderCode(code))
            {
                continue;
            }

            string name = Str(folder["name"]);
            result.Add(new FolderResponseModel
            {
                Code = code,
                Name = name.Length > 0 ? name : code,
                Children = ToFolders(folder["children"], depth + 1),
            });
        }

        return result;
    }

    public static string Str(JsonNode? node) => node is JsonValue v ? (v.TryGetValue(out string? s) ? s?.Trim() ?? "" : v.ToString()) : "";

    public static long Int(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue(out long l) || (v.TryGetValue(out string? s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))) ? l : 0;

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;
}

namespace BlueBillywig.Umbraco.ViewModels;

public sealed class ThumbnailResponseModel
{
    /// <summary>The src SAPI knows the image by. Null for an upload SAPI has not confirmed yet.</summary>
    public required string? Src { get; init; }

    /// <summary>The absolute image URL, or null without a src.</summary>
    public required string? Url { get; init; }

    public required bool Main { get; init; }

    /// <summary>True for an image SAPI's own read does not contain yet (a recent upload or main change).</summary>
    public required bool Unconfirmed { get; init; }
}

public sealed class ThumbnailsResponseModel
{
    public required IReadOnlyList<ThumbnailResponseModel> Thumbnails { get; init; }

    /// <summary>Set when SAPI accepted the upload but did not confirm the new image yet.</summary>
    public required string? Notice { get; init; }
}

public sealed class SubtitleResponseModel
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string? IsoCode { get; init; }
    public required string? LanguageName { get; init; }
    public required bool Default { get; init; }
    public required string? Status { get; init; }
}

public sealed class ClipEditResponseModel
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string? Status { get; init; }

    /// <summary>The uuid of the content protection policy, or null.</summary>
    public required string? Cpp { get; init; }

    public required string? FolderCode { get; init; }

    /// <summary>The playout chosen for this clip in the library. Kept in Umbraco (SAPI has no such field), like the plugin's per-clip playout option.</summary>
    public required string? Playout { get; init; }

    /// <summary>The ISO code of the clip's language, or null.</summary>
    public required string? Language { get; init; }

    public required string? EmbargoFrom { get; init; }
    public required string? EmbargoTo { get; init; }
    public required string? UpdatedDate { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required IReadOnlyList<ThumbnailResponseModel> Thumbnails { get; init; }
    public required IReadOnlyList<SubtitleResponseModel> Subtitles { get; init; }

    /// <summary>Values of the custom fields SAPI defines for clips, by field name. Empty unless custom fields are enabled in the settings.</summary>
    public required IReadOnlyDictionary<string, string> CustomFields { get; init; }

    public required bool CustomFieldsEnabled { get; init; }
}

/// <summary>
/// Only the fields that are sent are written (the plugin's rule: a missing field must not blank stored data).
/// An empty string clears <c>cpp</c>, <c>folderCode</c>, <c>embargoFrom</c> and <c>embargoTo</c>.
/// </summary>
public sealed class ClipUpdateRequestModel
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Status { get; init; }
    public string? Cpp { get; init; }
    public string? FolderCode { get; init; }
    public string? Playout { get; init; }

    /// <summary>The ISO code of a language from <c>library/languages</c>.</summary>
    public string? Language { get; init; }

    public string? EmbargoFrom { get; init; }
    public string? EmbargoTo { get; init; }
    public IReadOnlyDictionary<string, string>? CustomFields { get; init; }
}

public sealed class BulkRequestModel
{
    /// <summary><c>status</c>, <c>folder</c> or <c>cpp</c>. Delete has its own endpoint, behind the stricter policy.</summary>
    public string? Action { get; init; }

    public IReadOnlyList<long>? Ids { get; init; }

    /// <summary>The new status, folder code or CPP code. Empty means "move to root" or "clear CPP".</summary>
    public string? Value { get; init; }
}

public sealed class BulkDeleteRequestModel
{
    public IReadOnlyList<long>? Ids { get; init; }
}

public sealed class BulkErrorResponseModel
{
    public required long Id { get; init; }
    public required string Error { get; init; }
}

/// <summary>Per clip results of a bulk action. A partial failure is a 200 with <c>failed</c> above zero.</summary>
public sealed class BulkResultResponseModel
{
    public required int Success { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<long> Succeeded { get; init; }
    public required IReadOnlyList<BulkErrorResponseModel> Errors { get; init; }
}

public sealed class SubtitleUploadRequestModel
{
    public string? IsoCode { get; init; }
    public string? Name { get; init; }
    public bool Default { get; init; }
    public bool Publish { get; init; }

    /// <summary>The file name, only to check the extension (.vtt or .srt).</summary>
    public string? FileName { get; init; }

    /// <summary>The text of the file, read in the browser.</summary>
    public string? Content { get; init; }
}

public sealed class ThumbnailUploadRequestModel
{
    /// <summary>The image as a <c>data:image/jpeg|png;base64,...</c> URI, up to 10 MB.</summary>
    public string? Data { get; init; }
}

public sealed class ThumbnailMainRequestModel
{
    public string? Src { get; init; }
}

public sealed class CustomFieldResponseModel
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required string Type { get; init; }
    public required string IType { get; init; }
}

public sealed class CustomFieldGroupResponseModel
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<CustomFieldResponseModel> Fields { get; init; }
}

public sealed class LanguageResponseModel
{
    public required long Id { get; init; }
    public required string IsoCode { get; init; }
    public required string Name { get; init; }
}

public sealed class PolicyResponseModel
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
}

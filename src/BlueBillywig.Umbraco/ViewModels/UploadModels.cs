using System.Text.Json.Serialization;

namespace BlueBillywig.Umbraco.ViewModels;

public sealed class InitUploadRequestModel
{
    public string? Filename { get; init; }
    public long Filesize { get; init; }
    public string? ContentType { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }

    /// <summary>Comma separated, like the plugin's hidden tags field.</summary>
    public string? Tags { get; init; }

    public string? Status { get; init; }
}

public sealed class PresignedPartResponseModel
{
    public required int PartNumber { get; init; }
    public required string Url { get; init; }
}

public sealed class InitUploadResponseModel
{
    public required long MediaclipId { get; init; }
    public required string UploadId { get; init; }
    public required string? Key { get; init; }
    public required long PartSize { get; init; }
    public required IReadOnlyList<PresignedPartResponseModel> PresignedUrls { get; init; }
}

public sealed class SignPartRequestModel
{
    public string? UploadId { get; init; }
    public int PartNumber { get; init; }
}

public sealed class SignPartResponseModel
{
    public required string Url { get; init; }
}

public sealed class UploadPartModel
{
    [JsonPropertyName("PartNumber")]
    public int PartNumber { get; init; }

    [JsonPropertyName("ETag")]
    public string? ETag { get; init; }
}

public sealed class CompleteUploadRequestModel
{
    public string? UploadId { get; init; }
    public IReadOnlyList<UploadPartModel>? Parts { get; init; }
}

public sealed class CompleteUploadResponseModel
{
    public required string? Location { get; init; }
}

public sealed class AbortUploadRequestModel
{
    public long MediaclipId { get; init; }
}

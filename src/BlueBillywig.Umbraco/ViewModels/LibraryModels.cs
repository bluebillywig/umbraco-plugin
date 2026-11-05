namespace BlueBillywig.Umbraco.ViewModels;

public sealed class LibraryClipResponseModel
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string? Status { get; init; }
    public required string? MediaType { get; init; }
    public required string? SourceType { get; init; }
    public required int? Length { get; init; }
    public required string? CreatedDate { get; init; }
    public required string? UpdatedDate { get; init; }
    public required string? ThumbnailUrl { get; init; }

    /// <summary>The moving thumbnail (a short looping clip) shown on hover. Null when the clip has none.</summary>
    public required string? PreviewUrl { get; init; }
}

public sealed class LibraryClipListResponseModel
{
    public required IReadOnlyList<LibraryClipResponseModel> Items { get; init; }

    /// <summary>The publication's total for this query, from SAPI's <c>numfound</c> (never the page's own count).</summary>
    public required int Total { get; init; }

    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

public sealed class LibraryPlaylistResponseModel
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string? Status { get; init; }
    public required string? CreatedDate { get; init; }
    public required string? UpdatedDate { get; init; }
    public required string EmbedCode { get; init; }
}

public sealed class LibraryPlaylistListResponseModel
{
    public required IReadOnlyList<LibraryPlaylistResponseModel> Items { get; init; }
    public required int Total { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

public sealed class FolderResponseModel
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<FolderResponseModel> Children { get; init; }
}

public sealed class EmbedCodeResponseModel
{
    public required string Code { get; init; }
}

public sealed class ClipPreviewResponseModel
{
    /// <summary>The player page to show in a sandboxed frame.</summary>
    public required string Url { get; init; }

    /// <summary>True when the URL carries a short-lived read-only token, so drafts can play.</summary>
    public required bool Authenticated { get; init; }
}

public sealed class LibraryCapabilitiesResponseModel
{
    public required bool CanDelete { get; init; }
}

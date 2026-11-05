namespace BlueBillywig.Umbraco.ViewModels;

public sealed class ClipSummaryResponseModel
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string? ThumbnailUrl { get; init; }
    public required int? Length { get; init; }
    public required string? Status { get; init; }

    /// <summary>Start of the embargo window as SAPI holds it; null when there is none (the dev OVP's "cleared" value counts as none).</summary>
    public string? EmbargoFrom { get; init; }

    public string? EmbargoTo { get; init; }
}

/// <summary>Whether the publication has any channels or shorts, so the picker offers only the types that can be chosen.</summary>
public sealed class EmbedFeaturesResponseModel
{
    public required bool HasChannels { get; init; }

    public required bool HasShorts { get; init; }
}

public sealed class EntityOptionResponseModel
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string? Status { get; init; }
}

public sealed class PlayoutOptionResponseModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

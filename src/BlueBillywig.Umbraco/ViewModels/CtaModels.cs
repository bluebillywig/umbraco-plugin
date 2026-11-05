using BlueBillywig.Umbraco.Cta;

namespace BlueBillywig.Umbraco.ViewModels;

/// <summary>
/// What the editor needs to show a clip's call to action: the reference plugin's <c>get_cta</c> reply. A timeline made
/// by another tool is a normal answer here, not an error: <see cref="ForeignTimeline"/> is set, <see cref="Cta"/> is
/// null, and the editor locks itself so nothing can be saved over it.
/// </summary>
public sealed class CtaStateResponseModel
{
    /// <summary>The stored call to action as the flat editor config, or null when the clip has none (or only a foreign timeline).</summary>
    public required CtaConfig? Cta { get; init; }

    /// <summary>The SAPI id of our timeline, or "".</summary>
    public required string TimelineId { get; init; }

    /// <summary>The clip's length in seconds, so "until the end" can be saved without a player. 0 when SAPI does not know it.</summary>
    public required double ClipLength { get; init; }

    public required bool ForeignTimeline { get; init; }

    public required string ForeignTimelineId { get; init; }
}

public sealed class CtaDeleteResponseModel
{
    /// <summary>False when the clip had no call to action to delete.</summary>
    public required bool Deleted { get; init; }
}

public sealed class CtaValidateResponseModel
{
    /// <summary>The messages of the server's save rules; empty when the config would be accepted.</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

public sealed class CtaImageUploadRequestModel
{
    /// <summary>A <c>data:image/(jpeg|png|gif|webp);base64,...</c> URI, at most 10 MB.</summary>
    public string? Data { get; init; }
}

public sealed class CtaImageUploadResponseModel
{
    /// <summary>The id of the new creative, to put in the call to action's background image.</summary>
    public required string ImageId { get; init; }
}

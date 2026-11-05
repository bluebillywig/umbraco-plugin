namespace BlueBillywig.Umbraco.ViewModels;

/// <summary>The content protection settings as saved. The shared secret is write-only: only whether one is set is reported.</summary>
public sealed class GateSettingsResponseModel
{
    public required bool Enabled { get; init; }

    /// <summary>Token lifetime in seconds, already within 300 to 7200.</summary>
    public required int ExpirySeconds { get; init; }

    /// <summary>Names of the member groups that may watch protected content. Empty allows nobody.</summary>
    public required IReadOnlyList<string> MemberGroups { get; init; }

    public required string? Message { get; init; }

    public required string? LoginUrl { get; init; }

    public required bool HasSharedSecret { get; init; }

    /// <summary>True when the secret is set in configuration and so cannot be changed here.</summary>
    public required bool SharedSecretPinned { get; init; }
}

public sealed class GateSettingsRequestModel
{
    public bool Enabled { get; init; }
    public long? ExpirySeconds { get; init; }
    public IReadOnlyList<string>? MemberGroups { get; init; }
    public string? Message { get; init; }
    public string? LoginUrl { get; init; }

    /// <summary>Null or empty keeps the stored secret. To remove it, send <c>ClearSharedSecret</c>.</summary>
    public string? SharedSecret { get; init; }

    public bool ClearSharedSecret { get; init; }
}

/// <summary>What the admin banner shows (the plugin's gate error notice): errors by reason from the last hour, and the pepper failure.</summary>
public sealed class GateHealthResponseModel
{
    public required bool Healthy { get; init; }

    public required IReadOnlyList<GateErrorCountResponseModel> Errors { get; init; }

    public required int ErrorTotal { get; init; }

    public required string? LastErrorAt { get; init; }

    public required bool PepperFailed { get; init; }
}

public sealed class GateErrorCountResponseModel
{
    /// <summary>A key from <c>GateReasons</c>, such as <c>sapi_error</c>.</summary>
    public required string Reason { get; init; }

    public required int Count { get; init; }
}

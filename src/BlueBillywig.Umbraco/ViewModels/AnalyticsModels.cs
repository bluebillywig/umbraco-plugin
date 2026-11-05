namespace BlueBillywig.Umbraco.ViewModels;

/// <summary>One row of a SAPI analytics facet: a value (a title, a device type, a percentage) and how many times it was seen.</summary>
public sealed class AnalyticsFacetResponseModel
{
    public required string Value { get; init; }
    public required long Count { get; init; }
}

public sealed class DeviceShareResponseModel
{
    public required string Name { get; init; }
    public required long Count { get; init; }

    /// <summary>Share of all device rows, in percent, to one decimal (the plugin's rounding).</summary>
    public required double Percent { get; init; }
}

/// <summary>The dashboard widget: the last seven days across the publication. Port of <c>render_dashboard_widget</c>.</summary>
public sealed class DashboardAnalyticsResponseModel
{
    public required long TotalViews { get; init; }
    public required long TotalInits { get; init; }

    /// <summary>Views as a percentage of player loads, one decimal; 0 when there were no player loads.</summary>
    public required double PlayRate { get; init; }

    /// <summary>At most five.</summary>
    public required IReadOnlyList<AnalyticsFacetResponseModel> TopVideos { get; init; }

    public required IReadOnlyList<DeviceShareResponseModel> Devices { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
}

/// <summary>Engagement for one clip. Port of <c>SapiClient::getClipAnalytics</c> and the drawer's retention curve.</summary>
public sealed class ClipAnalyticsResponseModel
{
    public required long Views { get; init; }
    public required long Inits { get; init; }

    /// <summary>Views divided by player loads (a fraction), or null without player loads.</summary>
    public required double? PlayRate { get; init; }

    /// <summary>Seconds, or null when SAPI sent none.</summary>
    public required double? AvgViewTime { get; init; }

    /// <summary>
    /// How many viewers watched at least 0%, 1%, ... 100% of the clip: 101 numbers, the first being the most. Built from
    /// SAPI's <c>exactPercentageViewed</c> distribution by summing from the right, as the plugin's script does.
    /// </summary>
    public required IReadOnlyList<long> Retention { get; init; }

    public required IReadOnlyList<AnalyticsFacetResponseModel> Countries { get; init; }
    public required IReadOnlyList<AnalyticsFacetResponseModel> Devices { get; init; }

    /// <summary>True when the player-load call failed: the play rate is then unknown, not zero.</summary>
    public required bool InitsFailed { get; init; }

    public required string From { get; init; }
    public required string To { get; init; }
    public required int Days { get; init; }
}

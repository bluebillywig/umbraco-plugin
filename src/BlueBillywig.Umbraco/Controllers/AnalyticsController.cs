using BlueBillywig.Umbraco.Analytics;
using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Web.Common.Authorization;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// Analytics for the Content dashboard and the clip editor. A failed SAPI call is a 502 that names the sections that
/// failed and nothing of what SAPI said, so it can never be read as "no views".
/// </summary>
public sealed class AnalyticsController : BlueBillywigControllerBase
{
    private readonly IAnalyticsService _analytics;

    public AnalyticsController(IAnalyticsService analytics) => _analytics = analytics;

    /// <summary>
    /// The widget on the Content dashboard. Like the plugin's dashboard widget it is for everyone who sees the dashboard,
    /// so it sits under the Content section policy, not the Blue Billywig section's.
    /// </summary>
    [HttpGet("analytics/dashboard")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
    [ProducesResponseType<DashboardAnalyticsResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Dashboard(CancellationToken ct) => Respond(await _analytics.GetDashboardAsync(ct));

    /// <summary>Engagement for one clip, for the clip editor (the plugin's <c>get_clip_analytics</c>, Editor level there).</summary>
    [HttpGet("analytics/clips/{id:long}")]
    [Authorize(Policy = BlueBillywigPolicies.Library)]
    [ProducesResponseType<ClipAnalyticsResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Clip(long id, int? days, CancellationToken ct)
    {
        if (id is <= 0 or >= 1_000_000_000_000)
        {
            return BadRequest(new { error = "Invalid id" });
        }

        return Respond(await _analytics.GetClipAsync(id, days ?? 30, ct));
    }

    private IActionResult Respond<T>(AnalyticsOutcome<T> outcome) where T : class =>
        outcome.Ok
            ? Ok(outcome.Value)
            : StatusCode(StatusCodes.Status502BadGateway, Unavailable(outcome.FailedSections));

    /// <summary>
    /// Problem details, so the backoffice client keeps the body. <c>sections</c> names what failed
    /// (<c>views</c>, <c>inits</c>, <c>top_videos</c>, <c>devices</c>); nothing SAPI said is in it.
    /// </summary>
    internal static ProblemDetails Unavailable(IReadOnlyList<string> sections)
    {
        var problem = new ProblemDetails { Type = "BlueBillywig.AnalyticsUnavailable", Status = StatusCodes.Status502BadGateway, Title = "Unable to load analytics" };
        problem.Extensions["sections"] = sections;
        return problem;
    }
}

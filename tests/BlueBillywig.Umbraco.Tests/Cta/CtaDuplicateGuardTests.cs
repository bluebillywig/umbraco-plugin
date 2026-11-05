using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Cta;
using BlueBillywig.Umbraco.Sapi;

namespace BlueBillywig.Umbraco.Tests.Cta;

/// <summary>Port of the plugin's TimelineDuplicateGuardTest (fable-audit #2, ADO 19578) and more of getTimelineForClip. C7.</summary>
public class CtaDuplicateGuardTests
{
    private static CtaTimelineLocator Locator(FakeClient client) => new(client);

    private static void Clip(FakeClient client, string timelinesJson) => client.Json("/sapi/mediaclip/42", $$"""{"id":42,"timelines":{{timelinesJson}}}""");

    [Fact]
    public async Task All_attempted_fetches_failing_returns_a_failure()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":1},{"id":2}]""");
        // /sapi/timeline/1 and /2 unseeded: FakeClient answers 404 for both.
        TimelineLookup result = await Locator(client).FindAsync(42);
        Assert.Equal(TimelineLookupKind.Failed, result.Kind);
        Assert.Null(result.Timeline);
    }

    [Fact]
    public async Task An_empty_id_entry_does_not_defeat_the_guard()
    {
        // The #19578 case: the empty-id entry is skipped without a fetch. The real fetch fails, so the guard must still fire.
        var client = new FakeClient();
        Clip(client, """[{"id":""},{"id":7}]""");
        Assert.Equal(TimelineLookupKind.Failed, (await Locator(client).FindAsync(42)).Kind);
    }

    [Fact]
    public async Task Only_empty_id_entries_means_no_timeline_not_an_error()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":""}]""");
        Assert.Equal(TimelineLookupKind.None, (await Locator(client).FindAsync(42)).Kind);
        Assert.DoesNotContain(client.Requests, r => r.StartsWith("/sapi/timeline/"));
    }

    [Fact]
    public async Task A_successful_fetch_is_returned()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":3}]""");
        client.Json("/sapi/timeline/3", """{"id":3,"widgets":[{"label":"bb-cta:Promo"}]}""");
        TimelineLookup result = await Locator(client).FindAsync(42);
        Assert.Equal(TimelineLookupKind.Found, result.Kind);
        Assert.Equal(3, (int)result.Timeline!["id"]!);
    }

    [Fact]
    public async Task The_clip_is_read_with_expand_timelines()
    {
        var client = new FakeClient();
        Clip(client, "[]");
        await Locator(client).FindAsync(42);
        var call = Assert.Single(client.Calls);
        Assert.Equal("/sapi/mediaclip/42", call.Path);
        Assert.Equal("timelines", call.Request!.Query!["expand"]);
    }

    [Fact]
    public async Task A_failed_clip_read_is_a_failure_and_not_none()
    {
        var client = new FakeClient(); // 404 for the clip
        Assert.Equal(TimelineLookupKind.Failed, (await Locator(client).FindAsync(42)).Kind);
        client.Responses["/sapi/mediaclip/42"] = SapiResult.Failure(502, "bad gateway");
        Assert.Equal(TimelineLookupKind.Failed, (await Locator(client).FindAsync(42)).Kind);
        client.Responses["/sapi/mediaclip/42"] = SapiResult.Success(200, null, "");
        Assert.Equal(TimelineLookupKind.Failed, (await Locator(client).FindAsync(42)).Kind);
    }

    [Fact]
    public async Task A_missing_clip_id_is_a_failure_without_a_request()
    {
        var client = new FakeClient();
        Assert.Equal(TimelineLookupKind.Failed, (await Locator(client).FindAsync(0)).Kind);
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("""{"id":42}""")]
    [InlineData("""{"id":42,"timelines":[]}""")]
    [InlineData("""{"id":42,"timelines":null}""")]
    [InlineData("""{"id":42,"timelines":"x"}""")]
    public async Task A_clip_without_timelines_has_none(string clipJson)
    {
        var client = new FakeClient();
        client.Json("/sapi/mediaclip/42", clipJson);
        Assert.Equal(TimelineLookupKind.None, (await Locator(client).FindAsync(42)).Kind);
    }

    [Fact]
    public async Task Soft_deleted_timelines_are_skipped()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":1}]""");
        client.Json("/sapi/timeline/1", """{"id":1,"status":"deleted","widgets":[{"label":"bb-cta:Old"}]}""");
        Assert.Equal(TimelineLookupKind.None, (await Locator(client).FindAsync(42)).Kind);
    }

    [Fact]
    public async Task A_timeline_we_authored_wins_over_a_later_foreign_one()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":1},{"id":2}]""");
        client.Json("/sapi/timeline/1", """{"id":1,"widgets":[{"label":"bb-cta:Promo"}]}""");
        client.Json("/sapi/timeline/2", """{"id":2,"widgets":[{"label":"Quiz"}]}""");
        Assert.Equal(1, (int)(await Locator(client).FindAsync(42)).Timeline!["id"]!);
    }

    [Fact]
    public async Task Without_one_of_ours_the_last_live_timeline_is_returned_so_the_editor_can_refuse_it()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":1},{"id":2}]""");
        client.Json("/sapi/timeline/1", """{"id":1,"widgets":[{"label":"A"}]}""");
        client.Json("/sapi/timeline/2", """{"id":2,"widgets":[{"label":"B"}]}""");
        Assert.Equal(2, (int)(await Locator(client).FindAsync(42)).Timeline!["id"]!);
    }

    [Fact]
    public async Task One_failed_fetch_with_nothing_of_ours_found_is_a_failure()
    {
        // Stricter than the plugin (which compared errors with attempts): the unreadable one could be our CTA.
        var client = new FakeClient();
        Clip(client, """[{"id":1},{"id":2}]""");
        client.Json("/sapi/timeline/1", """{"id":1,"status":"deleted","widgets":[]}""");
        // 2 is unseeded.
        Assert.Equal(TimelineLookupKind.Failed, (await Locator(client).FindAsync(42)).Kind);
    }

    [Fact]
    public async Task One_failed_fetch_is_harmless_once_one_of_ours_is_found()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":1},{"id":2}]""");
        client.Json("/sapi/timeline/1", """{"id":1,"widgets":[{"label":"bb-cta:Promo"}]}""");
        Assert.Equal(TimelineLookupKind.Found, (await Locator(client).FindAsync(42)).Kind);
    }

    [Fact]
    public async Task An_unusable_timeline_id_counts_as_unverifiable()
    {
        var client = new FakeClient();
        Clip(client, """[{"id":"abc"}]""");
        Assert.Equal(TimelineLookupKind.Failed, (await Locator(client).FindAsync(42)).Kind);
    }

    [Fact]
    public async Task At_most_the_first_25_timelines_are_fetched_and_a_truncated_search_without_a_match_fails()
    {
        var client = new FakeClient();
        Clip(client, "[" + string.Join(",", Enumerable.Range(1, 30).Select(i => $"{{\"id\":{i}}}")) + "]");
        for (int i = 1; i <= 30; i++)
        {
            client.Json($"/sapi/timeline/{i}", $$"""{"id":{{i}},"widgets":[{"label":"other"}]}""");
        }

        TimelineLookup result = await Locator(client).FindAsync(42);
        Assert.Equal(25, client.Requests.Count(r => r.StartsWith("/sapi/timeline/")));
        Assert.Equal(TimelineLookupKind.Failed, result.Kind);

        // A match inside the first 25 is still returned.
        client.Json("/sapi/timeline/10", """{"id":10,"widgets":[{"label":"bb-cta:Promo"}]}""");
        Assert.Equal(10, (int)(await Locator(client).FindAsync(42)).Timeline!["id"]!);
    }
}

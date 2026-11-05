using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Gate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace BlueBillywig.Umbraco.Tests.Gate;

internal sealed class FakeMemberSource : IGateMemberSource
{
    public GateMember? Member { get; set; }

    public Exception? Failure { get; set; }

    public int Lookups { get; private set; }

    public Task<GateMember?> GetCurrentAsync(HttpContext http, CancellationToken ct)
    {
        Lookups++;
        return Failure is null ? Task.FromResult(Member) : Task.FromException<GateMember?>(Failure);
    }
}

/// <summary>G2: the visitor mapping. Anonymous, member in an allowed group, member in another group, backoffice user, failures.</summary>
public class MemberViewerProviderTests
{
    private static (MemberGateViewerProvider Provider, FakeMemberSource Source, DefaultHttpContext Http, GateSettingsStore Config) Create(params string[] allowedGroups)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/members/film";
        http.Request.QueryString = new QueryString("?a=1");
        var config = new GateSettingsStore(new LockedKeyValues(), new EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions()), new ProcessWriteLock());
        config.Save(new GateConfig(true, 3600, allowedGroups, null, "/login?returnUrl={returnUrl}"), null);
        var source = new FakeMemberSource();
        return (new MemberGateViewerProvider(new HttpContextAccessor { HttpContext = http }, config, source), source, http, config);
    }

    private static GateMember Member(string key = "0f8fad5bd9cb469fa16570867728950e", bool approved = true, bool locked = false, params string[] groups) =>
        new(key, groups, approved, locked);

    [Fact]
    public async Task An_anonymous_visitor_is_anonymous_with_a_login_link_back_to_the_page()
    {
        var (provider, _, _, _) = Create("Premium");
        await provider.PrimeAsync(default);

        GateViewer viewer = provider.GetViewer(GateEntity.Clip(1));

        Assert.False(viewer.IsAuthenticated);
        Assert.False(viewer.IsAllowed);
        Assert.Null(viewer.UserId);
        Assert.Equal("/login?returnUrl=%2Fmembers%2Ffilm%3Fa%3D1", viewer.LoginUrl);
    }

    [Theory]
    [InlineData("https://sso.example/login?r={returnUrl}", "https://sso.example/login?r=%2Fmembers%2Ffilm")]
    [InlineData("http://sso.example/login?r={returnUrl}", "http://sso.example/login?r=%2Fmembers%2Ffilm")]
    [InlineData("/login?returnUrl={returnUrl}", "/login?returnUrl=%2Fmembers%2Ffilm%3Fa%3D1")]
    [InlineData("/login/{returnUrl}", "/login/%2Fmembers%2Ffilm%3Fa%3D1")]
    public async Task The_return_url_carries_the_query_string_only_to_a_login_page_on_this_site(string template, string expected)
    {
        var (provider, _, _, config) = Create("Premium");
        config.Save(new GateConfig(true, 3600, ["Premium"], null, template), null);
        await provider.PrimeAsync(default);

        Assert.Equal(expected, provider.GetViewer(GateEntity.Clip(1)).LoginUrl);
    }

    [Fact]
    public async Task The_path_base_is_part_of_the_return_path_for_both()
    {
        var (provider, _, http, config) = Create("Premium");
        http.Request.PathBase = "/site";
        config.Save(new GateConfig(true, 3600, ["Premium"], null, "https://sso.example/login?r={returnUrl}"), null);
        await provider.PrimeAsync(default);

        Assert.Equal("https://sso.example/login?r=%2Fsite%2Fmembers%2Ffilm", provider.GetViewer(GateEntity.Clip(1)).LoginUrl);
    }

    [Fact]
    public async Task A_backoffice_user_is_an_anonymous_visitor_because_the_member_scheme_does_not_know_them()
    {
        // The source reports "no member" for anyone whose principal is not a member: a backoffice user's cookie is another scheme.
        var (provider, source, _, _) = Create("Premium");
        source.Member = null;
        await provider.PrimeAsync(default);

        Assert.False(provider.GetViewer(GateEntity.Clip(1)).IsAuthenticated);
    }

    [Fact]
    public async Task A_member_in_an_allowed_group_is_allowed_and_has_a_stable_id_that_is_not_the_login()
    {
        var (provider, source, _, _) = Create("Premium", "Staff");
        source.Member = Member(groups: ["premium"]);
        await provider.PrimeAsync(default);

        GateViewer viewer = provider.GetViewer(GateEntity.Clip(1));

        Assert.True(viewer.IsAuthenticated);
        Assert.True(viewer.IsAllowed);
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", viewer.UserId);
    }

    [Fact]
    public async Task A_member_in_another_group_is_authenticated_but_not_allowed()
    {
        var (provider, source, _, _) = Create("Premium");
        source.Member = Member(groups: ["Newsletter"]);
        await provider.PrimeAsync(default);

        GateViewer viewer = provider.GetViewer(GateEntity.Clip(1));

        Assert.True(viewer.IsAuthenticated);
        Assert.False(viewer.IsAllowed);
    }

    [Fact]
    public async Task An_empty_group_list_allows_nobody_even_a_member_of_every_group()
    {
        var (provider, source, _, _) = Create();
        source.Member = Member(groups: ["Premium", "Staff", "Newsletter"]);
        await provider.PrimeAsync(default);

        Assert.False(provider.GetViewer(GateEntity.Clip(1)).IsAllowed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_member_that_is_not_approved_or_is_locked_out_is_anonymous(bool approved, bool locked)
    {
        var (provider, source, _, _) = Create("Premium");
        source.Member = Member(approved: approved, locked: locked, groups: ["Premium"]);
        await provider.PrimeAsync(default);

        GateViewer viewer = provider.GetViewer(GateEntity.Clip(1));

        Assert.False(viewer.IsAuthenticated);
        Assert.False(viewer.IsAllowed);
        Assert.Null(viewer.UserId);
    }

    [Fact]
    public async Task The_member_is_looked_up_once_per_request_whatever_the_number_of_embeds()
    {
        var (provider, source, _, _) = Create("Premium");
        source.Member = Member(groups: ["Premium"]);

        for (int i = 0; i < 5; i++)
        {
            await provider.PrimeAsync(default);
            provider.GetViewer(GateEntity.Clip(i + 1));
        }

        Assert.Equal(1, source.Lookups);
    }

    [Fact]
    public async Task A_failed_lookup_is_an_error_not_anonymous_and_not_allowed()
    {
        var (provider, source, _, _) = Create("Premium");
        source.Failure = new InvalidOperationException("db down");
        await provider.PrimeAsync(default);

        Assert.Throws<InvalidOperationException>(() => provider.GetViewer(GateEntity.Clip(1)));
    }

    [Fact]
    public void A_viewer_that_was_never_resolved_is_an_error_so_the_gate_closes()
    {
        var (provider, _, _, _) = Create("Premium");

        Assert.Throws<InvalidOperationException>(() => provider.GetViewer(GateEntity.Clip(1)));
    }

    [Fact]
    public void Without_a_request_the_viewer_is_anonymous()
    {
        var config = new GateSettingsStore(new LockedKeyValues(), new EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions()), new ProcessWriteLock());
        var provider = new MemberGateViewerProvider(new HttpContextAccessor(), config, new FakeMemberSource { Member = Member(groups: ["Premium"]) });

        Assert.False(provider.GetViewer(GateEntity.Clip(1)).IsAuthenticated);
    }

    [Fact]
    public async Task A_provider_failure_closes_the_gate_with_the_internal_error_tally()
    {
        var (provider, source, _, _) = Create("Premium");
        source.Failure = new InvalidOperationException("db down");
        await provider.PrimeAsync(default);
        var health = new RecordingHealth();
        var gate = new ContentGate(new FakeGateSettings(), new FakeGateSecrets(), provider, new FakeCpp(), health, GateInjector.IsBbvmsHost, new FakeTime());

        GateResult result = await gate.ApplyAsync(GateEntity.Clip(42), Gates.ClipEmbed);

        Assert.Equal(GateOutcome.ClosedOnError, result.Outcome);
        Assert.Equal(["internal_error"], health.Errors);
        Assert.DoesNotContain("bbvms", result.Html);
    }
}

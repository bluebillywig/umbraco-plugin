using System.Security.Claims;
using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Composing;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Embedding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>
/// The visible marker for a CSP nonce that could not be applied (the reference plugin's CSP failure marker). Policy under test:
/// only with a nonce in play; shown to a signed-in backoffice user (and then kept out of shared caches) or to everybody when
/// ShowEmbedProblems is on; everyone else keeps the silent fallback and the log warning.
/// </summary>
public class EmbedProblemMarkerTests
{
    private const string Unpreparable = """{"body":"<object data=\"https://demo.bbvms.com/x\"></object>"}""";

    private sealed class Audience(EmbedProblemAudience value) : IBlueBillywigEmbedDiagnostics
    {
        public ValueTask<EmbedProblemAudience> GetAudienceAsync(HttpContext? httpContext) => ValueTask.FromResult(value);
    }

    private sealed class CapturedLog : ILogger<BlueBillywigEmbedRenderer>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private static async Task<(string Html, DefaultHttpContext Http, CapturedLog Log)> RenderAsync(string? nonce, EmbedProblemAudience? audience, string embedBody = Unpreparable, bool started = false)
    {
        var client = new FakeClient();
        client.Json("/sapi/embedcode/9/default/javascript", embedBody);
        client.Json("/sapi/mediaclip/9", """{"id":9,"title":"T"}""");
        var http = new DefaultHttpContext();
        if (started)
        {
            http.Features.Set<IHttpResponseFeature>(new StartedResponse());
        }

        var log = new CapturedLog();
        var renderer = new BlueBillywigEmbedRenderer(new FakeSettings(), client, new FixedConsent(true), new FixedNonce(nonce), new HttpContextAccessor { HttpContext = http }, log, diagnostics: audience is null ? null : new Audience(audience.Value));
        string html = (await renderer.RenderAsync(new BlueBillywigVideo { Id = 9, LazyLoad = false })).ToHtmlString();
        return (html, http, log);
    }

    [Fact]
    public async Task A_signed_in_user_sees_the_marker_next_to_the_working_fallback_and_the_response_turns_private()
    {
        (string html, DefaultHttpContext http, _) = await RenderAsync("n0nce", EmbedProblemAudience.SignedInUser);

        Assert.Contains("<div class=\"bb-embed-problem\" role=\"alert\" data-bb-reason=\"embed-code-unprepared\">", html);
        Assert.Contains("Video problem: the player could not be prepared for this site&#x27;s content security policy. See the log.</div>", html);
        // The fallback script embed is still there, with the nonce: the marker is added, nothing is taken away.
        Assert.Contains("c/9.js", html);
        Assert.Contains("nonce=\"n0nce\"", html);
        Assert.Equal("private, no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Everybody_sees_it_when_the_site_switched_it_on_and_nothing_is_marked_private()
    {
        (string html, DefaultHttpContext http, _) = await RenderAsync("n0nce", EmbedProblemAudience.Everyone);

        Assert.Contains("class=\"bb-embed-problem\"", html);
        Assert.False(http.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Everybody_else_keeps_the_silent_fallback_and_the_log_gets_a_warning(bool diagnosticsRegistered)
    {
        (string html, DefaultHttpContext http, CapturedLog log) = await RenderAsync("n0nce", diagnosticsRegistered ? EmbedProblemAudience.Nobody : null);

        Assert.DoesNotContain("bb-embed-problem", html);
        Assert.Contains("c/9.js", html);
        Assert.False(http.Response.Headers.ContainsKey("Cache-Control"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not in the expected shape"));
    }

    [Fact]
    public async Task Without_a_nonce_the_fallback_is_not_a_csp_matter_and_no_marker_shows()
    {
        (string html, _, _) = await RenderAsync(null, EmbedProblemAudience.Everyone);

        Assert.DoesNotContain("bb-embed-problem", html);
    }

    [Fact]
    public async Task A_good_embed_code_shows_no_marker_to_anybody()
    {
        (string html, _, _) = await RenderAsync("n0nce", EmbedProblemAudience.Everyone, """{"body":"<script src=\"https://demo.bbvms.com/p/default/c/9.js\"></script>"}""");

        Assert.DoesNotContain("bb-embed-problem", html);
        Assert.Contains("nonce=\"n0nce\"", html);
    }

    [Fact]
    public async Task A_nonce_the_renderer_refuses_is_a_problem_too()
    {
        (string html, DefaultHttpContext http, CapturedLog log) = await RenderAsync("bad nonce\"><script>", EmbedProblemAudience.SignedInUser, """{"body":"<script src=\"https://demo.bbvms.com/p/default/c/9.js\"></script>"}""");

        Assert.Contains("data-bb-reason=\"nonce-rejected\"", html);
        Assert.DoesNotContain("bad nonce", html);
        Assert.DoesNotContain("nonce=", html);
        Assert.Equal("private, no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not a valid nonce"));

        (string quiet, _, _) = await RenderAsync("bad nonce\"><script>", EmbedProblemAudience.Nobody);
        Assert.DoesNotContain("bb-embed-problem", quiet);
    }

    [Fact]
    public async Task The_user_marker_is_dropped_when_the_response_can_no_longer_be_made_private()
    {
        // Otherwise the cache could keep a page that shows an alert to everyone who asks for the same URL.
        (string html, _, _) = await RenderAsync("n0nce", EmbedProblemAudience.SignedInUser, started: true);

        Assert.DoesNotContain("bb-embed-problem", html);
    }

    // The default audience -------------------------------------------------------------------------------

    private sealed class Users(Guid? key) : IBackOfficeUserInfo
    {
        public IReadOnlyCollection<string> GetGroupAliases(ClaimsPrincipal principal) => [];

        public IReadOnlyCollection<string> GetAllowedSections(ClaimsPrincipal principal) => [];

        public Guid? GetUserKey(ClaimsPrincipal principal) => key;
    }

    private static BackOfficeEmbedDiagnostics Default(Guid? user, bool show = false) => new(new ServiceCollection().AddSingleton<IBackOfficeUserInfo>(new Users(user)).BuildServiceProvider(), new FixedOptions(new BlueBillywigOptions { ShowEmbedProblems = show }));

    [Fact]
    public async Task The_switch_shows_the_marker_to_everybody_even_without_a_request()
    {
        Assert.Equal(EmbedProblemAudience.Everyone, await Default(null, show: true).GetAudienceAsync(null));
    }

    [Fact]
    public async Task A_backoffice_user_on_the_request_is_the_signed_in_audience()
    {
        Assert.Equal(EmbedProblemAudience.SignedInUser, await Default(Guid.NewGuid()).GetAudienceAsync(new DefaultHttpContext()));
    }

    [Fact]
    public async Task A_visitor_or_a_host_without_the_backoffice_scheme_is_nobody()
    {
        // No IAuthenticationService on a bare context: asking the backoffice scheme throws, which counts as nobody.
        Assert.Equal(EmbedProblemAudience.Nobody, await Default(null).GetAudienceAsync(new DefaultHttpContext()));
        Assert.Equal(EmbedProblemAudience.Nobody, await Default(null).GetAudienceAsync(null));
    }

    [Fact]
    public void The_switch_is_off_by_default_and_a_sites_own_audience_wins()
    {
        Assert.False(new BlueBillywigOptions().ShowEmbedProblems);

        var services = new ServiceCollection();
        var custom = new Audience(EmbedProblemAudience.Nobody);
        services.AddSingleton<IBlueBillywigEmbedDiagnostics>(custom);
        BlueBillywigEmbedComposer.AddServices(services);

        Assert.Single(services, d => d.ServiceType == typeof(IBlueBillywigEmbedDiagnostics));
        Assert.Same(custom, services.Single(d => d.ServiceType == typeof(IBlueBillywigEmbedDiagnostics)).ImplementationInstance);

        var defaults = new ServiceCollection();
        BlueBillywigEmbedComposer.AddServices(defaults);
        Assert.Equal(typeof(BackOfficeEmbedDiagnostics), defaults.Single(d => d.ServiceType == typeof(IBlueBillywigEmbedDiagnostics)).ImplementationType);
    }
}

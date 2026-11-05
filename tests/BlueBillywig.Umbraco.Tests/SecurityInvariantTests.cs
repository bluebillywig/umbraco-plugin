using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BlueBillywig.Sapi.Authentication;
using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Web.Common.Authorization;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// I3: every endpoint the package adds requires backoffice authorization. The sweep reflects over the whole assembly, so a
/// new controller, or a new action on an old one, that is anonymous fails here until someone puts it on
/// <see cref="DeliberatelyPublic"/> with a reason. The only entry today is the video sitemap, with its reason.
/// <para>
/// What the sweep checks (see <see cref="Sweep.Problems"/>, which is itself tested against deliberately bad fixtures):
/// every controller by MVC's own naming rule (not only <c>ControllerBase</c> subclasses), every public action including the
/// ones inherited from a base class, an Http attribute on each, a known policy on each, a policy that may write on each
/// mutating verb (not Content or Overview), the Delete policy on each HTTP DELETE, a review entry for each destructive action
/// that is not a DELETE, and opt-outs from authorization matched by verb and full route.
/// </para>
/// </summary>
public class EndpointAuthorizationTests
{
    /// <summary>"VERB /full/route" entries that may be reached without signing in, each with the reason.</summary>
    private static readonly Dictionary<string, string> DeliberatelyPublic = new()
    {
        // The video sitemap is read by search engine crawlers, which cannot sign in, and it is the same kind of file as the
        // site's own sitemap. It lists only what a public page already shows: pages behind Public Access and clips with a
        // content protection policy are left out (see VideoSitemapService and its tests), and it answers only for hosts the
        // site has configured (see ConfiguredSitemapHosts).
        ["GET /video-sitemap.xml"] = "Public by nature: crawlers read it anonymously; it lists public pages and public clips only, for configured hosts only.",
    };

    /// <summary>
    /// Destructive actions that are not an HTTP DELETE, each with the policies that may guard it and the reviewed reason. A
    /// non-GET action whose name or route says remove, delete, abort, cleanup, disconnect or clear must be listed (the sweep
    /// fails otherwise, so the question "does this need the Delete policy" is asked for every new one). An HTTP DELETE needs
    /// the Delete policy and no entry.
    /// </summary>
    private static readonly Dictionary<string, (string[] Policies, string Reason)> Destructive = new()
    {
        ["POST /umbraco/bluebillywig/api/v1/library/clips/bulk-delete"] = ([BlueBillywigPolicies.Delete], "Deletes clips: the Delete policy, like the single DELETE."),

        // Reviewed exception, on purpose: the reference plugin lets an Author (the section level here) remove a call to
        // action, and what it removes is the timeline this package authored, never a clip. Do not move it to Delete without
        // deciding to drop that capability parity; see the comment on CtaController.RemoveCta.
        ["POST /umbraco/bluebillywig/api/v1/library/clips/{id:long}/cta/remove"] = ([BlueBillywigPolicies.Library], "Library level to match the reference plugin's Author capability; removes only the call to action timeline this package authored."),

        // Removes the placeholder clip of the caller's own, uncompleted upload (ownership is checked in UploadService and tested).
        ["POST /umbraco/bluebillywig/api/v1/upload/abort"] = ([BlueBillywigPolicies.Library], "Library level: it only removes the caller's own placeholder clip, which is what the uploader created a moment ago."),

        ["POST /umbraco/bluebillywig/api/v1/settings/cleanup"] = ([AuthorizationPolicies.SectionAccessSettings], "Settings section: the uninstall action of the settings dashboard; it empties this package's own settings only."),
        ["POST /umbraco/bluebillywig/api/v1/settings/disconnect"] = ([AuthorizationPolicies.SectionAccessSettings], "Settings section: forgets the stored credentials, a settings change."),
        ["POST /umbraco/bluebillywig/api/v1/settings/clear-cache"] = ([AuthorizationPolicies.SectionAccessSettings], "Settings section: drops cached SAPI answers, nothing is lost."),
    };

    private static readonly Assembly Package = typeof(BlueBillywigControllerBase).Assembly;

    [Fact]
    public void The_sweep_finds_the_packages_controllers()
    {
        // A guard against the sweep passing because it found nothing.
        string[] names = Sweep.ControllerTypes(Package).Select(c => c.Name).ToArray();
        foreach (string expected in new[] { "SettingsController", "StatusController", "AnalyticsController", "LibraryController", "ClipEditController", "UploadController", "VideoController", "VideoSitemapController", "GateController", "CtaController" })
        {
            Assert.Contains(expected, names);
        }

        Assert.True(Sweep.Endpoints(Sweep.ControllerTypes(Package)).Count() >= 40, "expected the full set of endpoints to be found");
    }

    [Fact]
    public void The_package_passes_every_rule_of_the_sweep()
    {
        IReadOnlyList<string> problems = Sweep.Problems(Sweep.ControllerTypes(Package), DeliberatelyPublic, Destructive);

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void Every_entry_of_the_two_review_lists_names_an_endpoint_that_exists()
    {
        string[] keys = Sweep.Endpoints(Sweep.ControllerTypes(Package)).Select(e => e.Key).ToArray();

        Assert.All(DeliberatelyPublic.Keys, k => Assert.Contains(k, keys));
        Assert.All(Destructive.Keys, k => Assert.Contains(k, keys));
    }

    [Fact]
    public void Nothing_in_the_package_opts_out_of_authorization_except_the_listed_endpoints()
    {
        string[] optedOut = Sweep.Endpoints(Sweep.ControllerTypes(Package))
            .Where(e => e.AllowsAnonymous)
            .Select(e => e.Key)
            .Where(k => !DeliberatelyPublic.ContainsKey(k))
            .ToArray();
        // Also a type or a method that is not an endpoint but carries the attribute, in any class of the package.
        string[] stray = Package.GetTypes()
            .Where(t => t.GetCustomAttribute<AllowAnonymousAttribute>(false) is not null && !Sweep.IsController(t))
            .Select(t => t.Name)
            .ToArray();

        Assert.Empty(optedOut);
        Assert.Empty(stray);
    }

    [Fact]
    public void Destructive_endpoints_carry_the_stricter_policy_on_the_action()
    {
        foreach (var endpoint in Sweep.Endpoints(Sweep.ControllerTypes(Package)).Where(e => e.Verb == "DELETE"))
        {
            Assert.Contains(endpoint.Action.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == BlueBillywigPolicies.Delete);
        }

        MethodInfo bulkDelete = typeof(ClipEditController).GetMethod(nameof(ClipEditController.BulkDelete))!;
        Assert.Contains(bulkDelete.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == BlueBillywigPolicies.Delete);
    }

    [Theory]
    [InlineData(typeof(SettingsController))]
    [InlineData(typeof(GateController))]
    public void Settings_endpoints_need_the_settings_section(Type controller)
    {
        // The gate's shared secret and group list are settings too: GateController sits behind the same Settings policy.
        MethodInfo[] actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttributes<HttpMethodAttribute>(true).Any()).ToArray();
        Assert.NotEmpty(actions);
        foreach (MethodInfo action in actions)
        {
            Assert.Contains(Sweep.Authorize(controller, action), a => a.Policy == AuthorizationPolicies.SectionAccessSettings);
        }
    }

    // The sweep tested against bad examples: each fixture breaks one rule, and the sweep must say so. -------------

    private static IReadOnlyList<string> Check(params Type[] controllers) => Sweep.Problems(controllers, DeliberatelyPublic, Destructive);

    [Fact]
    public void A_controller_that_is_not_a_controller_base_is_found_and_checked()
    {
        Assert.True(Sweep.IsController(typeof(SweepFixtures.PocoNoAuthController)));
        Assert.False(typeof(ControllerBase).IsAssignableFrom(typeof(SweepFixtures.PocoNoAuthController)));

        Assert.Contains(Check(typeof(SweepFixtures.PocoNoAuthController)), p => p.Contains("PocoNoAuthController.Get") && p.Contains("no [Authorize]"));
    }

    [Fact]
    public void A_public_action_inherited_from_a_base_class_is_checked()
    {
        Assert.Contains(Check(typeof(SweepFixtures.InheritingController)), p => p.Contains("InheritingController.Inherited"));
    }

    [Fact]
    public void A_mutating_verb_needs_a_policy_that_may_write_not_content_or_overview()
    {
        IReadOnlyList<string> problems = Check(typeof(SweepFixtures.ContentWritesController));

        Assert.Contains(problems, p => p.Contains("ContentWritesController.Put") && p.Contains("write policy"));
        Assert.Contains(problems, p => p.Contains("ContentWritesController.Post") && p.Contains("write policy"));
        Assert.DoesNotContain(problems, p => p.Contains("ContentWritesController.Get"));
    }

    [Fact]
    public void A_delete_verb_without_the_delete_policy_is_a_problem()
    {
        Assert.Contains(Check(typeof(SweepFixtures.LibraryDeleteController)), p => p.Contains("LibraryDeleteController.Remove") && p.Contains("Delete policy"));
    }

    [Fact]
    public void A_destructive_post_that_nobody_reviewed_is_a_problem_and_a_listed_one_must_keep_its_policy()
    {
        IReadOnlyList<string> problems = Check(typeof(SweepFixtures.UnreviewedDestructiveController));

        Assert.Contains(problems, p => p.Contains("UnreviewedDestructiveController.Wipe") && p.Contains("destructive"));
        Assert.Contains(problems, p => p.Contains("UnreviewedDestructiveController.Clear") && p.Contains("destructive"));
    }

    [Fact]
    public void An_opt_out_is_matched_by_verb_and_full_route_not_by_the_method_name()
    {
        // Same method name as the allowed sitemap action, other route and verb: the old name-suffix match let this through.
        IReadOnlyList<string> problems = Check(typeof(SweepFixtures.LookAlikeAnonymousController));

        Assert.Contains(problems, p => p.Contains("GET /look-alike") && p.Contains("AllowAnonymous"));
        Assert.Contains(problems, p => p.Contains("POST /video-sitemap.xml") && p.Contains("AllowAnonymous"));
    }

    [Fact]
    public void The_listed_opt_out_itself_passes()
    {
        Assert.Empty(Check(typeof(BlueBillywig.Umbraco.Seo.VideoSitemapController)));
    }
}

/// <summary>Controllers that each break one rule of the sweep. They live in the test assembly and are never routed.</summary>
internal static class SweepFixtures
{
    public sealed class PocoNoAuthController
    {
        [HttpGet("poco")]
        public IActionResult Get() => new OkResult();
    }

    public abstract class BaseWithAction
    {
        [HttpGet("inherited")]
        public IActionResult Inherited() => new OkResult();
    }

    public sealed class InheritingController : BaseWithAction;

    [Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
    public sealed class ContentWritesController : ControllerBase
    {
        [HttpGet("c")]
        public IActionResult Get() => new OkResult();

        [HttpPut("c")]
        public IActionResult Put() => new OkResult();

        [HttpPost("c")]
        public IActionResult Post() => new OkResult();
    }

    [Authorize(Policy = BlueBillywigPolicies.Library)]
    public sealed class LibraryDeleteController : ControllerBase
    {
        [HttpDelete("things/{id}")]
        public IActionResult Remove(long id) => new OkResult();
    }

    [Authorize(Policy = BlueBillywigPolicies.Library)]
    public sealed class UnreviewedDestructiveController : ControllerBase
    {
        [HttpPost("things/wipe")]
        public IActionResult Wipe() => new OkResult();

        [HttpPost("things/clear")]
        public IActionResult Clear() => new OkResult();
    }

    public sealed class LookAlikeAnonymousController : ControllerBase
    {
        [HttpGet("/look-alike")]
        [AllowAnonymous]
        public IActionResult ServeSitemap() => new OkResult();

        [HttpPost("/video-sitemap.xml")]
        [AllowAnonymous]
        public IActionResult Other() => new OkResult();
    }
}

/// <summary>The sweep itself: finds controllers and endpoints the way MVC does and applies the rules.</summary>
internal static class Sweep
{
    public sealed record EndpointInfo(Type Controller, MethodInfo Action, string Verb, string Route)
    {
        public string Name => $"{Controller.Name}.{Action.Name}";

        public string Key => $"{Verb} {Route}";

        public bool AllowsAnonymous =>
            Action.GetCustomAttribute<AllowAnonymousAttribute>(true) is not null || Controller.GetCustomAttribute<AllowAnonymousAttribute>(true) is not null;
    }

    /// <summary>Policies that may guard a mutating verb. Content and Overview are for reading.</summary>
    private static readonly string[] WritePolicies =
    [
        BlueBillywigPolicies.Library,
        BlueBillywigPolicies.Delete,
        AuthorizationPolicies.SectionAccessSettings,
    ];

    private static readonly string[] KnownPolicies = [.. WritePolicies, BlueBillywigPolicies.Overview, AuthorizationPolicies.SectionAccessContent];

    private static readonly System.Text.RegularExpressions.Regex DestructiveWords =
        new("remove|delete|abort|clean ?up|disconnect|clear|purge|wipe|reset", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>MVC's rule: a public, concrete, non-generic class whose name ends in "Controller" (or that has [Controller]).</summary>
    public static bool IsController(Type t) =>
        t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && (t.IsPublic || t.IsNestedPublic)
        && t.GetCustomAttribute<NonControllerAttribute>(true) is null
        && (t.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) || t.GetCustomAttribute<ControllerAttribute>(true) is not null);

    /// <summary>Every controller MVC would route, plus anything that derives from <see cref="ControllerBase"/> whatever it is called.</summary>
    public static IEnumerable<Type> ControllerTypes(Assembly assembly) =>
        assembly.GetTypes().Where(t => IsController(t) || (!t.IsAbstract && (t.IsPublic || t.IsNestedPublic) && typeof(ControllerBase).IsAssignableFrom(t)));

    /// <summary>The public actions of a controller, inherited ones included, minus the framework's own members.</summary>
    public static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType is { } d && d != typeof(object) && d.Assembly == controller.Assembly)
            .Where(m => m.GetCustomAttribute<NonActionAttribute>(true) is null && !m.IsSpecialName);

    public static IReadOnlyList<AuthorizeAttribute> Authorize(Type controller, MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>(true).Concat(controller.GetCustomAttributes<AuthorizeAttribute>(true)).ToList();

    public static IEnumerable<EndpointInfo> Endpoints(IEnumerable<Type> controllers)
    {
        foreach (Type controller in controllers)
        {
            foreach (MethodInfo action in Actions(controller))
            {
                foreach (HttpMethodAttribute http in action.GetCustomAttributes<HttpMethodAttribute>(true))
                {
                    foreach (string verb in http.HttpMethods)
                    {
                        yield return new EndpointInfo(controller, action, verb.ToUpperInvariant(), FullRoute(controller, http.Template));
                    }
                }
            }
        }
    }

    /// <summary>The route as a client sees it: the class-level route and the action's template, or the action's alone when it starts with "/".</summary>
    private static string FullRoute(Type controller, string? template)
    {
        template ??= "";
        if (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal))
        {
            return "/" + template.TrimStart('~', '/');
        }

        string prefix = controller.GetCustomAttributes(true).OfType<IRouteTemplateProvider>().Select(r => r.Template).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
        string combined = string.Join('/', new[] { prefix, template }.Where(x => x.Length > 0));

        // Placeholders in the attribute: the backoffice path segment and the API version. Name the route as it is served.
        return "/" + combined.Replace("[umbracoBackOffice]", "umbraco").Replace("v{version:apiVersion}", "v1");
    }

    public static IReadOnlyList<string> Problems(
        IEnumerable<Type> controllers,
        IReadOnlyDictionary<string, string> deliberatelyPublic,
        IReadOnlyDictionary<string, (string[] Policies, string Reason)> destructive)
    {
        var problems = new List<string>();
        Type[] list = controllers.ToArray();

        foreach (Type controller in list)
        {
            foreach (MethodInfo action in Actions(controller).Where(a => !a.GetCustomAttributes<HttpMethodAttribute>(true).Any()))
            {
                problems.Add($"{controller.Name}.{action.Name}: public action without an Http attribute");
            }
        }

        foreach (EndpointInfo e in Endpoints(list))
        {
            if (e.AllowsAnonymous)
            {
                if (!deliberatelyPublic.ContainsKey(e.Key))
                {
                    problems.Add($"{e.Name} ({e.Key}): [AllowAnonymous] and not on the DeliberatelyPublic list");
                }

                continue;
            }

            IReadOnlyList<AuthorizeAttribute> authorize = Authorize(e.Controller, e.Action);
            if (authorize.Count == 0)
            {
                problems.Add($"{e.Name} ({e.Key}): no [Authorize]");
                continue;
            }

            string[] policies = authorize.Select(a => a.Policy).Where(p => p is not null).Select(p => p!).ToArray();
            if (!policies.Any(KnownPolicies.Contains))
            {
                problems.Add($"{e.Name} ({e.Key}): no known policy ({string.Join(",", authorize.Select(a => a.Policy ?? "(none)"))})");
                continue;
            }

            bool mutating = e.Verb is "POST" or "PUT" or "PATCH" or "DELETE";
            if (mutating && !policies.Any(WritePolicies.Contains))
            {
                problems.Add($"{e.Name} ({e.Key}): a mutating verb needs a write policy (Library, Delete or Settings), not {string.Join(",", policies)}");
            }

            if (e.Verb == "DELETE" && !e.Action.GetCustomAttributes<AuthorizeAttribute>(true).Any(a => a.Policy == BlueBillywigPolicies.Delete))
            {
                problems.Add($"{e.Name} ({e.Key}): an HTTP DELETE needs the Delete policy on the action");
            }

            if (mutating && e.Verb != "DELETE" && DestructiveWords.IsMatch(e.Action.Name + " " + e.Route))
            {
                if (!destructive.TryGetValue(e.Key, out var reviewed))
                {
                    problems.Add($"{e.Name} ({e.Key}): looks destructive and is not on the reviewed Destructive list (give it the Delete policy, or review it and list it with the reason)");
                }
                else if (!policies.Any(reviewed.Policies.Contains))
                {
                    problems.Add($"{e.Name} ({e.Key}): destructive, reviewed as {string.Join("/", reviewed.Policies)}, but guarded by {string.Join(",", policies)}");
                }
            }
        }

        return problems;
    }
}

/// <summary>
/// I1: the read-write secret and the token made from it never reach a browser, in any response or rendered HTML. The
/// sentinels below are fed in as the read-write secret; every string the package can produce for a browser is searched
/// for them and for the real HOTP tokens they would produce.
/// </summary>
public class ReadWriteSecretTests
{
    private const string Secret = "RW-SECRET-SENTINEL-93f2a";
    private const string RoSecret = "RO-SECRET-SENTINEL-61bc7";

    private static BlueBillywigSettings Settings() => new()
    {
        Publication = "demo", ApiId = "4242", ApiSecret = Secret, ReadOnlyApiId = "4343", ReadOnlyApiSecret = RoSecret,
        Playout = "default", Embed = "javascript", EnableCustomFields = true,
    };

    /// <summary>The tokens the read-write key would send to SAPI in the current and neighbouring two-minute windows.</summary>
    private static string[] ReadWriteTokens()
    {
        var auth = new RpcTokenAuthenticator(4242, Secret);
        return [Secret, auth.Authenticate()["rpctoken"], "4242-"];
    }

    private static void AssertClean(string name, string text)
    {
        foreach (string needle in ReadWriteTokens())
        {
            Assert.False(text.Contains(needle, StringComparison.Ordinal), $"{name} contains a read-write credential ({(needle.Length > 8 ? needle[..4] + "..." : needle)})");
        }
    }

    [Fact]
    public async Task Rendered_html_never_holds_the_read_write_secret_or_token()
    {
        foreach (bool consent in new[] { true, false })
        {
            var client = new FakeClient { ReadOnlyToken = "4343-ro-token" };
            client.Json("/sapi/mediaclip/9", """{"id":9,"title":"T","transcript":"words","chapters":[{"time":3,"title":"C"}]}""");
            client.Json("/sapi/embedcode/9/default/javascript", """{"body":"<script src=\"https://demo.bbvms.com/p/default/c/9.js\"></script>"}""");
            var settings = new FakeSettings { Value = Settings() };
            var renderer = new BlueBillywigEmbedRenderer(settings, client, new FixedConsent(consent), new FixedNonce("n0nce"), new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<BlueBillywigEmbedRenderer>.Instance);

            foreach (BlueBillywigVideo video in new[]
            {
                new BlueBillywigVideo { Id = 9, ShowChapters = true, LazyLoad = true },
                new BlueBillywigVideo { Id = 9, ShowChapters = true, LazyLoad = false },
                new BlueBillywigVideo { Type = BlueBillywigVideoType.Channel, Id = 9 },
                new BlueBillywigVideo { Type = BlueBillywigVideoType.Shorts, Id = 9 },
            })
            {
                string html = (await renderer.RenderAsync(video)).ToHtmlString();
                AssertClean($"render {video.Type} consent={consent}", html);
                AssertClean("base64 payload", DecodeLazyPayloads(html));
            }
        }
    }

    private static string DecodeLazyPayloads(string html) =>
        string.Concat(System.Text.RegularExpressions.Regex.Matches(html, "data-bb-embed=\"([^\"]+)\"").Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value))));

    [Fact]
    public async Task Backoffice_responses_never_hold_the_read_write_secret_or_token()
    {
        var settings = new FakeSettings { Value = Settings() };
        var client = new FakeClient { ReadOnlyToken = "4343-ro-token" };
        client.Json("/sapi/publication", """{"name":"Demo"}""");
        var library = new LibraryService(client, settings);

        string[] responses =
        [
            Json(new SettingsController(settings, client, new BlueBillywigCleanup(new FakeKeyValues()), new FakeTester(), NullLogger<SettingsController>.Instance).GetSettings()),
            Json(await new StatusController(settings, client).Get(default)),
            Json((await library.GetPreviewAsync(9, default)).Value),
        ];

        Assert.All(responses.Select((r, i) => (r, i)), t => AssertClean("response " + t.i, t.r));
        Assert.Contains("rpctoken=4343-ro-token", responses[2]);
    }

    private static string Json(object? result) => JsonSerializer.Serialize(result is ObjectResult o ? o.Value : result);

    [Fact]
    public void The_only_token_the_client_hands_out_is_made_from_the_read_only_pair()
    {
        var client = new BlueBillywigClient(new FakeSettings { Value = Settings() }, new MemoryCache(new MemoryCacheOptions()), NullLogger<BlueBillywigClient>.Instance, new HttpClient(new FailingHandler()));

        string token = client.GetReadOnlyToken()!;

        Assert.StartsWith("4343-", token);
        Assert.Equal(new RpcTokenAuthenticator(4343, RoSecret).Authenticate()["rpctoken"], token);
        AssertClean("read-only token", token);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }
}

/// <summary>
/// I2: what SAPI says in an error body is for the log. It never reaches a result, a response or a page. The real client is
/// used, with a handler that answers every request with a failure whose body is full of recognisable internals.
/// </summary>
public class ErrorBodyTests
{
    private const string Internal = "INTERNAL-TRACE /var/www/secret/path.php:42 <script>alert(1)</script> SECRET-TOKEN-xyz";

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(Internal, Encoding.UTF8, "text/plain") });
    }

    private static (BlueBillywigClient Client, FakeSettings Settings) Failing(HttpStatusCode status = HttpStatusCode.InternalServerError)
    {
        var settings = new FakeSettings();
        return (new BlueBillywigClient(settings, new MemoryCache(new MemoryCacheOptions()), NullLogger<BlueBillywigClient>.Instance, new HttpClient(new Handler(status))), settings);
    }

    private static void AssertNoInternals(string where, string? text)
    {
        Assert.False(text is not null && (text.Contains("INTERNAL-TRACE", StringComparison.Ordinal) || text.Contains("SECRET-TOKEN", StringComparison.Ordinal) || text.Contains("/var/www", StringComparison.Ordinal)), $"{where} carries an upstream error body");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task The_client_result_carries_a_generic_message_for_every_verb(HttpStatusCode status)
    {
        (BlueBillywigClient client, _) = Failing(status);

        SapiResult[] results =
        [
            await client.GetAsync("/sapi/x"), await client.GetAsync("/sapi/x", cacheFor: TimeSpan.FromMinutes(1)),
            await client.PostAsync("/sapi/x", new { a = 1 }), await client.PutAsync("/sapi/x", new { a = 1 }), await client.DeleteAsync("/sapi/x"),
            await client.TestConnectionAsync(),
        ];

        foreach (SapiResult r in results)
        {
            Assert.False(r.Ok);
            Assert.Equal($"SAPI request failed ({(int)status})", r.Error);
            Assert.Null(r.Json);
            Assert.Null(r.RawBody);
            AssertNoInternals("result", JsonSerializer.Serialize(r));
        }
    }

    [Fact]
    public async Task The_rendered_page_shows_the_fallback_embed_and_no_upstream_text()
    {
        (BlueBillywigClient client, FakeSettings settings) = Failing();
        var renderer = new BlueBillywigEmbedRenderer(settings, client, new FixedConsent(true), new FixedNonce(null), new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<BlueBillywigEmbedRenderer>.Instance);

        foreach (BlueBillywigVideo video in new[]
        {
            new BlueBillywigVideo { Id = 9, ShowChapters = true, LazyLoad = false },
            new BlueBillywigVideo { Id = 9, LazyLoad = true },
        })
        {
            string html = (await renderer.RenderAsync(video)).ToHtmlString();
            Assert.Contains("c/9.js", html);
            AssertNoInternals("html", html);
            AssertNoInternals("lazy payload", string.Concat(System.Text.RegularExpressions.Regex.Matches(html, "data-bb-embed=\"([^\"]+)\"").Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value)))));
        }
    }

    [Fact]
    public async Task Backoffice_responses_for_a_failed_call_are_a_502_with_a_generic_message()
    {
        (BlueBillywigClient client, FakeSettings settings) = Failing();
        var library = new LibraryService(client, settings);
        var libraryController = new LibraryController(library, client);
        var settingsController = new SettingsController(settings, client, new BlueBillywigCleanup(new FakeKeyValues()), new FakeTester { Answer = CredentialCheck.Failed(500) }, NullLogger<SettingsController>.Instance);
        var analytics = new AnalyticsController(new Analytics.AnalyticsService(client, TimeProvider.System));

        IActionResult[] results =
        [
            await libraryController.Clips(null, null, null, null, null, 0, default),
            await libraryController.Livestreams(null, 0, default),
            await settingsController.Playouts(default),
            await analytics.Dashboard(default),
            await analytics.Clip(5, 30, default),
        ];

        foreach (IActionResult result in results)
        {
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(502, obj.StatusCode);
            AssertNoInternals("response", JsonSerializer.Serialize(obj.Value));
        }

        var test = (SettingsController.TestConnectionResponse)((OkObjectResult)await settingsController.TestConnection(new SettingsController.TestConnectionRequest(null, null, null))).Value!;
        Assert.False(test.Ok);
        AssertNoInternals("test connection", JsonSerializer.Serialize(test));
    }
}

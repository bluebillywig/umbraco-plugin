using System.Net;
using System.Text;
using System.Text.Json;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// S12 to S15 and E19: the settings save checks the credentials and refuses a bad set, the connection test takes the typed
/// values, disconnecting is explicit, and the publication has the reference plugin's shape.
/// </summary>
public class SettingsCredentialTests
{
    private static (SettingsController Controller, FakeSettings Settings, FakeTester Tester, FakeClient Client, FakeKeyValues Kv) Rig()
    {
        var settings = new FakeSettings();
        var client = new FakeClient();
        var kv = new FakeKeyValues();
        var tester = new FakeTester();
        return (new SettingsController(settings, client, new BlueBillywigCleanup(kv, client), tester, NullLogger<SettingsController>.Instance), settings, tester, client, kv);
    }

    private static (SettingsController Controller, FakeTester Tester) RigPinned()
    {
        var tester = new FakeTester();
        var client = new FakeClient();
        return (new SettingsController(new PinnedSettings(), client, new BlueBillywigCleanup(new FakeKeyValues(), client), tester, NullLogger<SettingsController>.Instance), tester);
    }

    private static SettingsController.SettingsRequest Request(
        string? publication = "demo", string? apiId = "1", string? apiSecret = "typed-secret", bool clearSecret = false, string? consent = null) =>
        new(publication, apiId, apiSecret, null, null, "default", "javascript", 15, true, true, consent, clearSecret);

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    // S12 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_credential_check_refuses_the_save_and_stores_and_clears_nothing()
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, FakeClient client, _) = Rig();
        tester.Answer = CredentialCheck.Failed(401);
        BlueBillywigSettings before = settings.Value;

        IActionResult result = await controller.SaveSettings(Request(apiSecret: "wrong"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        string body = Json(bad.Value);
        Assert.Contains("API connection failed", body);
        Assert.Contains("401", body);
        Assert.DoesNotContain("wrong", body);
        Assert.Same(before, settings.Value);
        Assert.Equal(0, client.Cleared);
    }

    [Fact]
    public async Task The_check_runs_with_the_typed_values_and_the_stored_secret_when_none_is_typed()
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, _, _) = Rig();
        settings.Value = settings.Value with { ApiSecret = "stored-secret" };

        await controller.SaveSettings(Request(publication: "other-pub", apiId: "9", apiSecret: null));
        await controller.SaveSettings(Request(publication: "other-pub", apiId: "9", apiSecret: "typed-secret"));

        Assert.Equal(["stored-secret", "typed-secret"], tester.Asked.Select(a => a.ApiSecret));
        Assert.All(tester.Asked, a => { Assert.Equal("other-pub", a.Publication); Assert.Equal("9", a.ApiId); });
    }

    [Fact]
    public async Task Blank_id_and_secret_mean_not_connected_and_save_without_a_check()
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, _, _) = Rig();
        settings.Value = settings.Value with { ApiSecret = "stored-secret" };

        var ok = Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(publication: "demo", apiId: "", apiSecret: null, clearSecret: true)));

        Assert.Empty(tester.Asked);
        var saved = ((SettingsController.SaveSettingsResponse)ok.Value!).Settings;
        Assert.False(saved.HasApiSecret);
        Assert.Equal("", saved.ApiId);
    }

    [Fact]
    public async Task An_incomplete_set_saves_untested_like_the_reference_plugin()
    {
        (SettingsController controller, _, FakeTester tester, _, _) = Rig();

        Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(publication: "", apiId: "5", apiSecret: "s")));

        Assert.Empty(tester.Asked);
    }

    [Fact]
    public async Task An_empty_secret_in_a_normal_save_keeps_the_stored_one()
    {
        (SettingsController controller, FakeSettings settings, _, _, _) = Rig();
        settings.Value = settings.Value with { ApiSecret = "stored-secret" };

        await controller.SaveSettings(Request(apiSecret: ""));

        Assert.Equal("stored-secret", settings.Value.ApiSecret);
    }

    [Fact]
    public async Task Config_pinned_values_are_used_as_they_are_whatever_the_form_sends()
    {
        (SettingsController controller, FakeTester tester) = RigPinned();

        // The pinned publication has two characters, which the slug rule would refuse if it were typed.
        Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(publication: "BAD PUB", apiId: "999", apiSecret: "typed")));

        BlueBillywigSettings asked = Assert.Single(tester.Asked);
        Assert.Equal("bb", asked.Publication);
        Assert.Equal("7", asked.ApiId);
        Assert.Equal("pinned-secret", asked.ApiSecret);
    }

    [Fact]
    public async Task A_given_read_only_key_is_checked_too_and_a_bad_one_refuses_the_save()
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, FakeClient client, _) = Rig();
        tester.Decide = c => c.ApiId == "5" ? CredentialCheck.Failed(401) : new CredentialCheck(true, "Demo publication", null);
        BlueBillywigSettings before = settings.Value;

        IActionResult result = await controller.SaveSettings(Request() with { ReadOnlyApiId = "5", ReadOnlyApiSecret = "ro-wrong" });

        string body = Json(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains("Read-only API connection failed", body);
        Assert.Contains("401", body);
        Assert.DoesNotContain("ro-wrong", body);
        Assert.Equal(["1", "5"], tester.Asked.Select(a => a.ApiId));
        Assert.Same(before, settings.Value);
        Assert.Equal(0, client.Cleared);
    }

    [Fact]
    public async Task The_read_only_check_uses_the_stored_read_only_secret_when_none_is_typed_and_skips_an_incomplete_pair()
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, _, _) = Rig();
        settings.Value = settings.Value with { ReadOnlyApiSecret = "stored-ro" };

        await controller.SaveSettings(Request() with { ReadOnlyApiId = "5", ReadOnlyApiSecret = "" });
        Assert.Equal("stored-ro", tester.Asked[^1].ApiSecret);
        Assert.Equal("5", tester.Asked[^1].ApiId);

        tester.Asked.Clear();
        settings.Value = settings.Value with { ReadOnlyApiSecret = "" };
        await controller.SaveSettings(Request() with { ReadOnlyApiId = "5", ReadOnlyApiSecret = "" });
        Assert.Equal(["1"], tester.Asked.Select(a => a.ApiId));
    }

    [Fact]
    public async Task A_non_2xx_answer_from_sapi_counts_as_a_failed_check_not_a_success()
    {
        var tester = new SapiCredentialTester(null, new HttpClient(new Handler(HttpStatusCode.BadRequest, """{"name":"Looks fine"}""")));

        CredentialCheck check = await tester.TestAsync(new BlueBillywigSettings { Publication = "bb", ApiId = "7", ApiSecret = "s" });

        Assert.False(check.Ok);
    }

    private sealed class PinnedSettings : IBlueBillywigSettingsStore
    {
        private BlueBillywigSettings _value = new() { Publication = "bb", ApiId = "7", ApiSecret = "pinned-secret", Playout = "default", Embed = "javascript" };

        public event Action? Changed { add { } remove { } }

        public BlueBillywigSettings Get() => _value;

        public IReadOnlyCollection<string> ConfigOverrides() => ["publication", "apiId", "apiSecret"];

        public void Save(BlueBillywigSettings settings, string? apiSecret, string? readOnlyApiSecret) => _value = settings;

        public void Disconnect() { }
    }

    // S13 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_connection_uses_the_typed_values_and_stores_nothing()
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, FakeClient client, FakeKeyValues kv) = Rig();
        BlueBillywigSettings before = settings.Value;

        var ok = Assert.IsType<OkObjectResult>(await controller.TestConnection(new SettingsController.TestConnectionRequest("typed-pub", "42", "typed-secret")));

        var result = (SettingsController.TestConnectionResponse)ok.Value!;
        Assert.True(result.Ok);
        Assert.Equal("Demo publication", result.Publication);
        BlueBillywigSettings asked = Assert.Single(tester.Asked);
        // The base URL is the configured one (here none), never a typed one.
        Assert.Equal(("typed-pub", null, "42", "typed-secret"), (asked.Publication, asked.BaseUrl, asked.ApiId, asked.ApiSecret));
        Assert.Same(before, settings.Value);
        Assert.Empty(kv.Rows);
        Assert.Equal(0, client.Cleared);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Test_connection_with_a_blank_secret_uses_the_stored_one_and_a_blank_body_tests_what_is_stored()
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, _, _) = Rig();
        settings.Value = settings.Value with { ApiSecret = "stored-secret" };

        await controller.TestConnection(new SettingsController.TestConnectionRequest("", "", ""));
        await controller.TestConnection(new SettingsController.TestConnectionRequest(null, null, null));

        Assert.All(tester.Asked, a => { Assert.Equal("stored-secret", a.ApiSecret); Assert.Equal("demo", a.Publication); Assert.Equal("1", a.ApiId); });
        Assert.Equal(2, tester.Asked.Count);
    }

    [Fact]
    public async Task A_failed_connection_test_keeps_the_result_shape_and_returns_only_the_fixed_message()
    {
        (SettingsController controller, _, FakeTester tester, _, _) = Rig();
        tester.Answer = CredentialCheck.Failed(404);

        var result = (SettingsController.TestConnectionResponse)((OkObjectResult)await controller.TestConnection(new SettingsController.TestConnectionRequest(null, null, null))).Value!;

        Assert.False(result.Ok);
        Assert.Null(result.Publication);
        Assert.Contains("Publication not found", result.Error);
    }

    // The real tester, over a fake HTTP handler -------------------------------------------------------------

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task The_real_check_turns_a_bad_answer_into_a_fixed_message_without_the_response_body(HttpStatusCode status, string code)
    {
        var tester = new SapiCredentialTester(null, new HttpClient(new Handler(status, "internal path /var/www/secret-stack-trace")));

        CredentialCheck check = await tester.TestAsync(new BlueBillywigSettings { Publication = "bb", BaseUrl = "https://bb.dev.example", ApiId = "7", ApiSecret = "s" });

        Assert.False(check.Ok);
        Assert.Contains(code, check.Error);
        Assert.DoesNotContain("stack", check.Error);
        Assert.DoesNotContain("/var/www", check.Error);
    }

    [Fact]
    public async Task The_real_check_succeeds_only_with_a_publication_name_and_uses_the_effective_base_url()
    {
        var handler = new Handler(HttpStatusCode.OK, """{"name":"Dev publication"}""");
        var tester = new SapiCredentialTester(null, new HttpClient(handler));

        CredentialCheck ok = await tester.TestAsync(new BlueBillywigSettings { Publication = "bb", BaseUrl = "https://bb.dev.example", ApiId = "7", ApiSecret = "s" });
        CredentialCheck noName = await new SapiCredentialTester(null, new HttpClient(new Handler(HttpStatusCode.OK, """{"other":1}"""))).TestAsync(new BlueBillywigSettings { Publication = "bb", ApiId = "7", ApiSecret = "s" });

        Assert.True(ok.Ok);
        Assert.Equal("Dev publication", ok.Publication);
        Assert.Equal("bb.dev.example", Assert.Single(handler.Seen).Host);
        Assert.False(noName.Ok);
    }

    // S14 -------------------------------------------------------------------------------------------------

    [Fact]
    public void Disconnect_removes_the_credentials_clears_the_caches_and_says_it_is_disconnected()
    {
        (SettingsController controller, FakeSettings settings, _, FakeClient client, _) = Rig();
        settings.Value = settings.Value with { ReadOnlyApiId = "5", ReadOnlyApiSecret = "ro-secret" };

        var ok = Assert.IsType<OkObjectResult>(controller.Disconnect(new SettingsController.ConfirmRequest(true)));

        var response = (SettingsController.DisconnectResponse)ok.Value!;
        Assert.Equal(1, settings.Disconnects);
        Assert.Equal(1, client.Cleared);
        Assert.True(response.CacheCleared);
        Assert.False(response.Settings.HasApiSecret);
        Assert.False(response.Settings.HasReadOnlyApiSecret);
        Assert.Equal("", response.Settings.ApiId);
        Assert.Empty(response.StillConfigured);
        Assert.DoesNotContain("secret", Json(response).Replace("HasApiSecret", "").Replace("HasReadOnlyApiSecret", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task S9_the_status_the_header_notice_reads_flips_to_unconfigured_after_a_disconnect_and_back_after_a_save()
    {
        (SettingsController controller, FakeSettings settings, _, _, _) = Rig();
        var status = new StatusController(settings, new FakeClient());
        async Task<bool> Configured() => ((StatusController.StatusResponse)((OkObjectResult)await status.Get(default)).Value!).Configured;

        Assert.True(await Configured());
        controller.Disconnect(new SettingsController.ConfirmRequest(true));
        Assert.False(await Configured());
        await controller.SaveSettings(Request());
        Assert.True(await Configured());
    }

    [Fact]
    public void Disconnect_does_not_claim_to_remove_values_that_configuration_pins()
    {
        (SettingsController controller, _) = RigPinned();

        var response = (SettingsController.DisconnectResponse)((OkObjectResult)controller.Disconnect(new SettingsController.ConfirmRequest(true))).Value!;

        Assert.Equal(["publication", "apiId", "apiSecret"], response.StillConfigured);
        Assert.True(response.Settings.HasApiSecret);
    }

    [Fact]
    public async Task The_real_store_empties_the_stored_credentials_on_disconnect_and_leaves_other_settings_and_other_keys()
    {
        var kv = new FakeKeyValues();
        var store = new BlueBillywigSettingsStore(kv, new EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions()));
        store.Save(new BlueBillywigSettings { Publication = "demo", ApiId = "7", ReadOnlyApiId = "8", Playout = "wide", Embed = "iframe" }, "secret", "ro-secret");
        kv.Rows["BlueBillywig.license"] = "another package";
        int changed = 0;
        store.Changed += () => changed++;

        store.Disconnect();

        BlueBillywigSettings after = store.Get();
        Assert.False(after.HasCredentials);
        Assert.Equal("", after.ReadOnlyApiSecret);
        Assert.Equal("wide", after.Playout);
        Assert.Equal("another package", kv.Rows["BlueBillywig.license"]);
        Assert.Equal(1, changed);
        await Task.CompletedTask;
    }

    // S15 -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("abc", true)]
    [InlineData("bb.dev", true)]
    [InlineData("my-pub.01", true)]
    [InlineData("a1b", true)]
    [InlineData("bb", false)]            // 2 characters: the plugin's pattern needs at least 3
    [InlineData("b", false)]
    [InlineData("BB.dev", false)]        // not lowercased: refused
    [InlineData("my_pub", false)]
    [InlineData("bb..dev", false)]
    [InlineData(".bb.dev", false)]
    [InlineData("bb.dev.", false)]
    [InlineData("-bb", false)]
    [InlineData("bb-", false)]
    [InlineData("bb dev", false)]
    [InlineData("bbé", false)]
    [InlineData("a/b", false)]
    public void The_publication_has_the_shape_the_reference_plugin_accepts(string publication, bool valid) =>
        Assert.Equal(valid, SettingsController.IsValidPublication(publication));

    [Fact]
    public void A_publication_is_at_most_63_characters()
    {
        Assert.True(SettingsController.IsValidPublication(new string('a', 63)));
        Assert.False(SettingsController.IsValidPublication(new string('a', 64)));
    }

    [Theory]
    [InlineData("bb..dev")]
    [InlineData("My_Pub")]
    [InlineData("ab")]
    public async Task A_typed_publication_with_the_wrong_shape_is_refused_before_the_check_and_nothing_is_stored(string publication)
    {
        (SettingsController controller, FakeSettings settings, FakeTester tester, _, _) = Rig();
        BlueBillywigSettings before = settings.Value;

        var bad = Assert.IsType<BadRequestObjectResult>(await controller.SaveSettings(Request(publication: publication)));

        Assert.Contains("lowercase ASCII slug", Json(bad.Value));
        Assert.Empty(tester.Asked);
        Assert.Same(before, settings.Value);
    }

    [Fact]
    public async Task The_publication_is_trimmed_not_lowercased()
    {
        (SettingsController controller, FakeSettings settings, _, _, _) = Rig();

        Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(publication: "  demo.pub  ")));
        Assert.Equal("demo.pub", settings.Value.Publication);
        Assert.IsType<BadRequestObjectResult>(await controller.SaveSettings(Request(publication: "Demo.pub")));
    }

    // E19 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_consent_message_is_saved_cleaned_and_capped()
    {
        (SettingsController controller, FakeSettings settings, _, _, _) = Rig();

        await controller.SaveSettings(Request(consent: "  Our video partner will get your IP.\u0007  " + new string('x', 600)));

        string saved = settings.Value.ConsentMessage;
        Assert.StartsWith("Our video partner will get your IP.", saved);
        Assert.Equal(ConsentMessage.MaxLength, saved.Length);
        Assert.DoesNotContain('\u0007', saved);
    }

    [Fact]
    public async Task The_consent_message_is_stored_read_back_and_removable_with_the_real_store()
    {
        var kv = new FakeKeyValues();
        var store = new BlueBillywigSettingsStore(kv, new EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions()));
        store.Save(new BlueBillywigSettings { ConsentMessage = "Hello <b>there</b>" }, null, null);
        Assert.Equal("Hello <b>there</b>", store.Get().ConsentMessage);

        var pinned = new BlueBillywigSettingsStore(kv, new EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions { ConsentPlaceholderMessage = "From appsettings" }));
        Assert.Equal("From appsettings", pinned.Get().ConsentMessage);
        Assert.Contains("consentMessage", pinned.ConfigOverrides());

        Assert.Contains("BlueBillywig.consentMessage", BlueBillywigCleanup.Keys);
        new BlueBillywigCleanup(kv).Run();
        Assert.Equal("", store.Get().ConsentMessage);
        await Task.CompletedTask;
    }

    private sealed class SiteConsent(string? message) : IBlueBillywigConsentProvider
    {
        public bool IsGranted(HttpContext? httpContext) => false;

        public string? GetPlaceholderMessage(HttpContext? httpContext) => message;
    }

    private static async Task<string> RenderDenied(string? providerMessage, string? settingMessage)
    {
        var client = new FakeClient();
        var settings = new FakeSettings();
        settings.Value = settings.Value with { ConsentMessage = settingMessage ?? "" };
        var renderer = new BlueBillywigEmbedRenderer(settings, client, providerMessage is null ? new FixedConsent(false) : new SiteConsent(providerMessage), new FixedNonce(null), new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<BlueBillywigEmbedRenderer>.Instance);
        return (await renderer.RenderAsync(new BlueBillywigVideo { Id = 5 })).ToHtmlString();
    }

    [Fact]
    public async Task The_default_consent_text_stays_when_nothing_is_set()
    {
        string html = await RenderDenied(null, null);

        Assert.Contains(">" + ConsentMessage.Default + "</p>", html);
        Assert.Contains("bb-consent-placeholder__msg", html);
    }

    [Fact]
    public async Task A_sites_consent_message_replaces_the_default_and_is_html_encoded()
    {
        string html = await RenderDenied(null, "We share <script>alert(1)</script> with \"Blue Billywig\".");

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain(ConsentMessage.Default, html);
    }

    [Fact]
    public async Task The_consent_provider_can_word_the_notice_and_wins_over_the_setting()
    {
        string html = await RenderDenied("Per-visitor text <i>x</i>", "Setting text");

        Assert.Contains("Per-visitor text &lt;i&gt;x&lt;/i&gt;", html);
        Assert.DoesNotContain("Setting text", html);
    }

    [Fact]
    public async Task A_provider_that_has_no_text_falls_back_to_the_setting_then_the_default()
    {
        Assert.Contains("Setting text", await RenderDenied("", "Setting text"));
        Assert.Contains(ConsentMessage.Default, await RenderDenied(" ", null));
    }
}

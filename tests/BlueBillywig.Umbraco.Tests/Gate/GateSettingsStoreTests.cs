using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace BlueBillywig.Umbraco.Tests.Gate;

/// <summary>An in-memory table with the compare-and-set Umbraco offers and a write lock that really excludes other threads.</summary>
internal sealed class LockedKeyValues : IKeyValueService
{
    private readonly object _sync = new();
    public Dictionary<string, string> Rows { get; } = new(StringComparer.Ordinal);

    /// <summary>Makes every write fail, like a read-only database.</summary>
    public bool WritesFail { get; set; }

    public int Writes { get; private set; }

    public string? GetValue(string key)
    {
        lock (_sync) { return Rows.GetValueOrDefault(key); }
    }

    public IReadOnlyDictionary<string, string?>? FindByKeyPrefix(string keyPrefix)
    {
        lock (_sync) { return Rows.Where(r => r.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToDictionary(r => r.Key, r => (string?)r.Value); }
    }

    public void SetValue(string key, string value)
    {
        if (WritesFail) throw new InvalidOperationException("read-only");
        lock (_sync) { Rows[key] = value; Writes++; }
    }

    public void SetValue(string key, string originValue, string newValue) => throw new NotSupportedException();

    public bool TrySetValue(string key, string originValue, string newValue) => throw new NotSupportedException();
}

internal sealed class ProcessWriteLock : IGateWriteLock
{
    private readonly object _sync = new();

    public T Run<T>(Func<T> action)
    {
        lock (_sync) { return action(); }
    }
}

/// <summary>G3 and the settings half of G1/G2: stored secrets, pepper insert-if-absent, validation, and that a secret is write-only.</summary>
public class GateSettingsStoreTests
{
    private const string Secret = "cpp-shared-secret-0123456789abcdef";

    private static (GateSettingsStore Store, LockedKeyValues Kv, IDataProtectionProvider Dp) Create(BlueBillywigOptions? options = null, IDataProtectionProvider? dp = null, LockedKeyValues? kv = null)
    {
        kv ??= new LockedKeyValues();
        dp ??= new EphemeralDataProtectionProvider();
        return (new GateSettingsStore(kv, dp, new FixedOptions(options ?? new BlueBillywigOptions()), new ProcessWriteLock()), kv, dp);
    }

    [Fact]
    public void Nothing_is_enabled_and_nobody_is_allowed_by_default()
    {
        var (store, _, _) = Create();

        GateConfig config = store.Get();

        Assert.False(config.Enabled);
        Assert.False(store.Enabled);
        Assert.Empty(config.MemberGroups);
        Assert.Equal(GateJwt.DefaultExpirySeconds, config.ExpirySeconds);
        Assert.False(store.HasSharedSecret);
        Assert.Null(store.GetSharedSecret());
    }

    [Fact]
    public void A_saved_config_round_trips_and_the_expiry_is_clamped()
    {
        var (store, _, _) = Create();

        store.Save(new GateConfig(true, 99_999_999, ["Premium", "Staff"], "Members only", "/login?returnUrl={returnUrl}"), null);

        GateConfig config = store.Get();
        Assert.True(config.Enabled);
        Assert.Equal(GateJwt.MaxExpirySeconds, config.ExpirySeconds);
        Assert.Equal(["Premium", "Staff"], config.MemberGroups);
        Assert.Equal("Members only", store.CustomMessage);
        Assert.Equal("/login?returnUrl={returnUrl}", config.LoginUrl);
        Assert.Equal(GateJwt.MaxExpirySeconds, store.ExpirySeconds);
    }

    [Fact]
    public void A_stored_expiry_outside_the_range_is_clamped_on_read_too()
    {
        var (store, kv, _) = Create();
        kv.Rows[GateSettingsStore.ExpiryKey] = "5";

        Assert.Equal(GateJwt.MinExpirySeconds, store.ExpirySeconds);
    }

    [Fact]
    public void The_shared_secret_is_encrypted_at_rest_and_null_keeps_it()
    {
        var (store, kv, _) = Create();
        store.Save(new GateConfig(true, 3600, [], null, null), Secret);

        Assert.DoesNotContain(Secret, kv.Rows.Values.Concat(kv.Rows.Keys));
        Assert.NotEqual("", kv.Rows[GateSettingsStore.SecretKey]);
        Assert.Equal(Secret, store.GetSharedSecret());
        Assert.True(store.HasSharedSecret);

        store.Save(new GateConfig(false, 3600, [], null, null), null);
        Assert.Equal(Secret, store.GetSharedSecret());

        store.Save(new GateConfig(false, 3600, [], null, null), "");
        Assert.Null(store.GetSharedSecret());
        Assert.False(store.HasSharedSecret);
    }

    [Fact]
    public void A_secret_in_configuration_wins_and_is_reported_as_pinned()
    {
        var (store, _, _) = Create(new BlueBillywigOptions { GateSharedSecret = "from-the-environment-0123456789ab" });
        store.Save(new GateConfig(true, 3600, [], null, null), Secret);

        Assert.Equal("from-the-environment-0123456789ab", store.GetSharedSecret());
        Assert.True(store.SharedSecretPinned);
    }

    [Theory]
    [InlineData("too-short-pinned-secret")] // 23 characters
    [InlineData("0123456789abcdef0123456789abcde")] // 31
    [InlineData("0123456789abcdef 0123456789abcdef")] // whitespace
    public void A_pinned_secret_that_fails_the_rules_is_treated_as_no_secret_so_the_gate_closes(string pinned)
    {
        var (store, _, _) = Create(new BlueBillywigOptions { GateSharedSecret = pinned });
        store.Save(new GateConfig(true, 3600, [], null, null), Secret);

        // A pinned value wins over a stored one; when it is not acceptable the answer is "no secret", not the stored one.
        Assert.Null(store.GetSharedSecret());
        Assert.False(store.HasSharedSecret);
        Assert.True(store.SharedSecretPinned);
    }

    [Fact]
    public void A_pinned_secret_of_32_characters_is_used()
    {
        var (store, _, _) = Create(new BlueBillywigOptions { GateSharedSecret = "0123456789abcdef0123456789abcdef" });

        Assert.Equal("0123456789abcdef0123456789abcdef", store.GetSharedSecret());
    }

    [Fact]
    public void A_secret_stored_when_16_characters_were_enough_keeps_working()
    {
        var (store, _, _) = Create();
        store.Save(new GateConfig(true, 3600, [], null, null), "0123456789abcdef");

        Assert.Equal("0123456789abcdef", store.GetSharedSecret());
    }

    [Fact]
    public void A_secret_that_cannot_be_decrypted_reads_as_unset_so_the_gate_closes()
    {
        var (store, kv, _) = Create();
        store.Save(new GateConfig(true, 3600, [], null, null), Secret);

        // The Data Protection keys changed (a new key ring).
        var (afterRotation, _, _) = Create(dp: new EphemeralDataProtectionProvider(), kv: kv);

        Assert.Null(afterRotation.GetSharedSecret());
        Assert.False(afterRotation.HasSharedSecret);
    }

    // The salt ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_salt_is_made_once_stored_encrypted_and_stable()
    {
        var (store, kv, _) = Create();

        string? first = store.GetSalt();
        string? second = store.GetSalt();

        Assert.NotNull(first);
        Assert.True(first!.Length >= 32);
        Assert.Equal(first, second);
        Assert.DoesNotContain(first, kv.Rows.Values);
    }

    [Fact]
    public void An_unreadable_salt_is_replaced_instead_of_closing_the_gate_for_good()
    {
        var (store, kv, _) = Create();
        string? old = store.GetSalt();

        var (rotated, _, _) = Create(dp: new EphemeralDataProtectionProvider(), kv: kv);
        string? replaced = rotated.GetSalt();

        Assert.NotNull(replaced);
        Assert.NotEqual(old, replaced);
        Assert.Equal(replaced, rotated.GetSalt());
    }

    [Fact]
    public void A_salt_that_cannot_be_stored_is_null_so_the_gate_fails_closed()
    {
        var (store, kv, _) = Create();
        kv.WritesFail = true;

        Assert.Null(store.GetSalt());
    }

    // The pepper (G3) --------------------------------------------------------------------------------------

    [Fact]
    public void The_pepper_is_inserted_only_if_absent()
    {
        var (store, kv, _) = Create();

        Assert.True(store.TryAddPepper("first-" + new string('a', 40)));
        Assert.False(store.TryAddPepper("second-" + new string('b', 40)));

        Assert.Equal("first-" + new string('a', 40), store.GetPepper());
        Assert.Equal("first-" + new string('a', 40), kv.Rows[GateSettingsStore.PepperKey]);
    }

    [Fact]
    public void An_overwrite_replaces_the_pepper()
    {
        var (store, _, _) = Create();
        store.TryAddPepper("first-" + new string('a', 40));

        Assert.True(store.TrySetPepper("second-" + new string('b', 40)));

        Assert.Equal("second-" + new string('b', 40), store.GetPepper());
    }

    [Fact]
    public void A_pepper_write_that_fails_is_reported_not_thrown()
    {
        var (store, kv, _) = Create();
        kv.WritesFail = true;

        Assert.False(store.TryAddPepper(new string('a', 64)));
        Assert.False(store.TrySetPepper(new string('a', 64)));
        Assert.Null(store.GetPepper());
    }

    [Fact]
    public async Task Concurrent_requests_end_up_with_one_pepper_and_one_sub_per_member()
    {
        var (store, _, _) = Create();
        var health = new RecordingHealth();
        var pepper = new GatePepper(store, health);

        // Many requests at once on a fresh install, each through the real GatePepper against the real store.
        PepperResult[] results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(pepper.GetOrCreate)));

        Assert.Single(results.Select(r => r.Pepper).Distinct());
        Assert.All(results, r => Assert.True(r.Persisted));
        Assert.Equal(results[0].Pepper, store.GetPepper());
        Assert.Equal(0, health.PepperFailures);

        string salt = store.GetSalt()!;
        Assert.Single(results.Select(r => GateSubject.For("member-1", salt, r.Pepper)).Distinct());
    }

    [Fact]
    public async Task Concurrent_salt_creation_gives_every_caller_the_same_salt()
    {
        var (store, _, _) = Create();

        string?[] salts = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(store.GetSalt)));

        Assert.Single(salts.Distinct());
        Assert.NotNull(salts[0]);
    }

    [Fact]
    public void A_pepper_that_cannot_be_stored_degrades_with_the_banner_flag_instead_of_closing_the_gate()
    {
        var (store, kv, _) = Create();
        kv.WritesFail = true;
        var health = new RecordingHealth();

        PepperResult result = new GatePepper(store, health).GetOrCreate();

        Assert.False(result.Persisted);
        Assert.True(result.Pepper.Length >= GatePepper.MinLength);
        Assert.Equal(1, health.PepperFailures);
    }

    [Fact]
    public void The_uninstall_list_names_every_key_the_gate_store_writes()
    {
        var (store, kv, _) = Create();
        store.Save(new GateConfig(true, 600, ["a"], "m", "/login"), Secret);
        store.GetSalt();
        store.TryAddPepper(new string('p', 64));

        Assert.Equal(kv.Rows.Keys.OrderBy(k => k), GateSettingsStore.Keys.OrderBy(k => k));
        Assert.All(GateSettingsStore.Keys, k => Assert.Contains(k, BlueBillywigCleanup.Keys));
    }

    // Validation -------------------------------------------------------------------------------------------

    [Fact]
    public void Validation_cleans_groups_and_clamps_the_expiry()
    {
        (GateConfig? config, string? error) = GateConfigRules.Validate(true, 10, [" Premium ", "premium", "", "Staff"], "  hello  ", " /login ");

        Assert.Null(error);
        Assert.Equal(["Premium", "Staff"], config!.MemberGroups);
        Assert.Equal(GateJwt.MinExpirySeconds, config.ExpirySeconds);
        Assert.Equal("hello", config.Message);
        Assert.Equal("/login", config.LoginUrl);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/login")]
    [InlineData("data:text/html,x")]
    [InlineData("/login with space")]
    public void Validation_refuses_an_unsafe_login_url(string url)
    {
        (GateConfig? config, string? error) = GateConfigRules.Validate(true, 3600, [], null, url);

        Assert.Null(config);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validation_accepts_the_return_url_placeholder_and_absolute_urls()
    {
        Assert.NotNull(GateConfigRules.Validate(true, 3600, [], null, "/login?returnUrl={returnUrl}").Config);
        Assert.NotNull(GateConfigRules.Validate(true, 3600, [], null, "https://sso.example/login?r={returnUrl}").Config);
    }

    [Fact]
    public void Validation_refuses_a_group_name_with_control_characters_and_an_overlong_message()
    {
        Assert.Null(GateConfigRules.Validate(true, 3600, ["bad\nname"], null, null).Config);
        Assert.Null(GateConfigRules.Validate(true, 3600, [], new string('x', GateConfigRules.MaxMessage + 1), null).Config);
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("has a space in the middle 1234", false)]
    [InlineData("0123456789abcdef", false)] // 16 characters were enough before; new values need 32
    [InlineData("0123456789abcdef0123456789abcde", false)] // 31
    [InlineData("0123456789abcdef0123456789abcdef", true)] // 32
    public void Validation_checks_the_secret(string secret, bool ok)
    {
        Assert.Equal(ok, GateConfigRules.ValidateSecret(secret) is null);
    }

    [Fact]
    public void A_secret_error_never_echoes_the_secret()
    {
        string? error = GateConfigRules.ValidateSecret("a b");

        Assert.DoesNotContain("a b", error);
    }
}

/// <summary>The endpoints behind the settings dashboard: the secret is write-only (G1, I1), the policy list maps failures (G8), the tally feeds the banner (G7).</summary>
public class GateControllerTests
{
    private const string Secret = "cpp-shared-secret-0123456789abcdef";

    private sealed class Groups : IGateMemberGroupSource
    {
        public Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["Premium", "Staff"]);
    }

    private static (GateController Controller, GateSettingsStore Store, GateHealthTally Health, EditRig Rig) Create(BlueBillywigOptions? options = null)
    {
        var kv = new LockedKeyValues();
        var store = new GateSettingsStore(kv, new EphemeralDataProtectionProvider(), new FixedOptions(options ?? new BlueBillywigOptions()), new ProcessWriteLock());
        var health = new GateHealthTally();
        var rig = new EditRig();
        return (new GateController(store, health, rig.Service, rig.Client, new Groups()), store, health, rig);
    }

    private static T Value<T>(IActionResult result) => Assert.IsAssignableFrom<T>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public void A_saved_secret_is_never_in_any_response()
    {
        var (controller, store, _, _) = Create();

        GateSettingsResponseModel saved = Value<GateSettingsResponseModel>(controller.SaveGate(new GateSettingsRequestModel { Enabled = true, ExpirySeconds = 3600, MemberGroups = ["Premium"], SharedSecret = Secret }));
        GateSettingsResponseModel read = Value<GateSettingsResponseModel>(controller.GetGate());

        Assert.True(saved.HasSharedSecret);
        Assert.True(read.HasSharedSecret);
        Assert.Equal(Secret, store.GetSharedSecret());
        string json = System.Text.Json.JsonSerializer.Serialize(read) + System.Text.Json.JsonSerializer.Serialize(saved);
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain("ecret\":\"", json.Replace("HasSharedSecret", "").Replace("SharedSecretPinned", ""));
        Assert.Empty(typeof(GateSettingsResponseModel).GetProperties().Where(p => p.Name == "SharedSecret"));
    }

    [Fact]
    public void Saving_without_a_secret_keeps_the_stored_one_and_clearing_removes_it()
    {
        var (controller, store, _, _) = Create();
        controller.SaveGate(new GateSettingsRequestModel { Enabled = true, SharedSecret = Secret });

        controller.SaveGate(new GateSettingsRequestModel { Enabled = true, MemberGroups = ["Staff"] });
        Assert.Equal(Secret, store.GetSharedSecret());

        GateSettingsResponseModel cleared = Value<GateSettingsResponseModel>(controller.SaveGate(new GateSettingsRequestModel { Enabled = true, ClearSharedSecret = true }));
        Assert.False(cleared.HasSharedSecret);
        Assert.Null(store.GetSharedSecret());
    }

    [Fact]
    public void A_bad_request_is_a_400_and_writes_nothing()
    {
        var (controller, store, _, _) = Create();

        IActionResult badSecret = controller.SaveGate(new GateSettingsRequestModel { Enabled = true, SharedSecret = "short" });
        IActionResult badUrl = controller.SaveGate(new GateSettingsRequestModel { Enabled = true, LoginUrl = "javascript:alert(1)" });

        Assert.IsType<BadRequestObjectResult>(badSecret);
        Assert.IsType<BadRequestObjectResult>(badUrl);
        Assert.False(store.Enabled);
        Assert.False(store.HasSharedSecret);
        Assert.DoesNotContain("short", System.Text.Json.JsonSerializer.Serialize(((BadRequestObjectResult)badSecret).Value));
    }

    [Fact]
    public void The_dashboard_refuses_a_new_secret_under_32_characters()
    {
        var (controller, _, _, _) = Create();

        IActionResult result = controller.SaveGate(new GateSettingsRequestModel { Enabled = true, SharedSecret = "0123456789abcdef" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void A_secret_pinned_by_configuration_cannot_be_changed_here()
    {
        var (controller, store, _, _) = Create(new BlueBillywigOptions { GateSharedSecret = "from-the-environment-0123456789ab" });

        IActionResult result = controller.SaveGate(new GateSettingsRequestModel { Enabled = true, SharedSecret = Secret });

        Assert.IsType<BadRequestObjectResult>(result);
        GateSettingsResponseModel read = Value<GateSettingsResponseModel>(controller.GetGate());
        Assert.True(read.SharedSecretPinned);
        Assert.Equal("from-the-environment-0123456789ab", store.GetSharedSecret());
    }

    [Fact]
    public async Task The_policy_list_returns_code_name_and_status_only()
    {
        var (controller, _, _, rig) = Create();
        rig.Client.Json("/sapi/contentprotectionpolicy", """{"items":[{"id":"70","code":"545179f0-1503-46d6-9cc7-041fab63fc48","name":"Premium","status":"active","rulesets":[{"filters":[]}],"createdBy":"x@example.com"},{"id":"71","code":"not-a-uuid","name":"bad"}]}""");

        IReadOnlyList<PolicyResponseModel> policies = Value<IReadOnlyList<PolicyResponseModel>>(await controller.Policies(default));

        PolicyResponseModel only = Assert.Single(policies);
        Assert.Equal("545179f0-1503-46d6-9cc7-041fab63fc48", only.Code);
        Assert.Equal("Premium", only.Name);
        Assert.DoesNotContain("x@example.com", System.Text.Json.JsonSerializer.Serialize(policies));
    }

    [Fact]
    public async Task A_sapi_failure_is_a_502_without_the_upstream_body()
    {
        var (controller, _, _, rig) = Create();
        rig.Client.Responses["/sapi/contentprotectionpolicy"] = SapiResult.Failure(500, "SAPI request failed (500)");

        IActionResult result = await controller.Policies(default);

        var object502 = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, object502.StatusCode);
        Assert.DoesNotContain("<html", System.Text.Json.JsonSerializer.Serialize(object502.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_connection_is_a_503_like_the_plugin()
    {
        var (controller, _, _, rig) = Create();
        rig.Client.IsConnected = false;

        IActionResult result = await controller.Policies(default);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task The_member_groups_are_listed_for_the_picker()
    {
        var (controller, _, _, _) = Create();

        Assert.Equal(["Premium", "Staff"], Value<IReadOnlyList<string>>(await controller.MemberGroups(default)));
    }

    [Fact]
    public void The_health_endpoint_reports_the_tally_and_the_pepper_flag()
    {
        var (controller, _, health, _) = Create();
        Assert.True(Value<GateHealthResponseModel>(controller.Health()).Healthy);

        health.RecordGateError(GateReasons.SapiError);
        health.RecordGateError(GateReasons.SapiError);
        health.RecordGateError(GateReasons.SecretMissing);
        health.RecordPepperPersistFailure();
        GateHealthResponseModel report = Value<GateHealthResponseModel>(controller.Health());

        Assert.False(report.Healthy);
        Assert.Equal(3, report.ErrorTotal);
        Assert.Equal("sapi_error", report.Errors[0].Reason);
        Assert.Equal(2, report.Errors[0].Count);
        Assert.True(report.PepperFailed);
        Assert.NotNull(report.LastErrorAt);
    }

    [Fact]
    public void Every_gate_endpoint_needs_the_settings_section()
    {
        Assert.Contains(typeof(GateController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(), a => a.Policy == AuthorizationPolicies.SectionAccessSettings);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>An in-memory key-value table. Like Umbraco's it can set and read but never delete a key.</summary>
internal sealed class FakeKeyValues : IKeyValueService
{
    public Dictionary<string, string> Rows { get; } = new(StringComparer.Ordinal);

    public string? GetValue(string key) => Rows.GetValueOrDefault(key);

    public IReadOnlyDictionary<string, string?>? FindByKeyPrefix(string keyPrefix) =>
        Rows.Where(r => r.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToDictionary(r => r.Key, r => (string?)r.Value);

    public void SetValue(string key, string value) => Rows[key] = value;

    public void SetValue(string key, string originValue, string newValue) => throw new NotSupportedException();

    public bool TrySetValue(string key, string originValue, string newValue) => throw new NotSupportedException();
}

/// <summary>
/// S11, the uninstall equivalent. Ports the plugin's <c>UninstallAllowlistTest</c>: every secret-bearing key is wiped, a
/// look-alike key of another package survives, and nothing is cleared by a wildcard.
/// </summary>
public class CleanupTests
{
    private static (BlueBillywigCleanup Cleanup, FakeKeyValues Kv, FakeClient Client) Rig()
    {
        var kv = new FakeKeyValues();
        var client = new FakeClient();
        return (new BlueBillywigCleanup(kv, client), kv, client);
    }

    /// <summary>The keys the settings store really writes, found by saving every field through the real store.</summary>
    private static HashSet<string> KeysWrittenBySaving()
    {
        var kv = new FakeKeyValues();
        var store = new BlueBillywigSettingsStore(kv, new EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions()));
        store.Save(
            new BlueBillywigSettings { Publication = "p", BaseUrl = "https://x.example", ApiId = "1", ReadOnlyApiId = "2", Playout = "a", Embed = "iframe", PageCount = 9, EnableCustomFields = true, EnableCta = false },
            "api-secret",
            "ro-secret");
        return kv.Rows.Keys.ToHashSet();
    }

    [Fact]
    public void The_allowlist_names_every_key_the_settings_store_writes()
    {
        HashSet<string> written = KeysWrittenBySaving();

        // The settings store's keys plus the gate store's, which have their own save (and a salt and pepper that a save never writes).
        Assert.Equal(written.Concat(BlueBillywig.Umbraco.Gate.GateSettingsStore.Keys).OrderBy(k => k), BlueBillywigCleanup.Keys.OrderBy(k => k));
        Assert.All(BlueBillywigCleanup.Keys, k => Assert.StartsWith("BlueBillywig.", k));
    }

    [Fact]
    public void Every_secret_bearing_key_is_wiped_and_reads_as_unset_afterwards()
    {
        (BlueBillywigCleanup cleanup, FakeKeyValues kv, FakeClient client) = Rig();
        foreach (string key in BlueBillywigCleanup.Keys) kv.Rows[key] = "sensitive-value-" + key;

        IReadOnlyList<string> cleared = cleanup.Run();

        Assert.Equal(BlueBillywigCleanup.Keys.Length, cleared.Count);
        Assert.All(BlueBillywigCleanup.Keys, k => Assert.Equal("", kv.Rows[k]));
        Assert.Equal(1, client.Cleared);
        // Read back through the store: nothing is left, so the package is unconfigured.
        var store = new BlueBillywigSettingsStore(kv, new EphemeralDataProtectionProvider(), new FixedOptions(new BlueBillywigOptions()));
        Assert.False(store.Get().HasCredentials);
        Assert.Equal("", store.Get().ApiSecret);
        Assert.Equal("", store.Get().ReadOnlyApiSecret);
    }

    [Fact]
    public void Entries_of_other_packages_that_share_the_prefix_survive()
    {
        (BlueBillywigCleanup cleanup, FakeKeyValues kv, _) = Rig();
        string[] sentinels =
        [
            "BlueBillywig.license", "BlueBillywig.license-expires", "BlueBillywig.freemius-account",
            "BlueBillywig.ClipPlayoutX", "BlueBillywig.ClipPlayout.abc", "BlueBillywig.ClipPlayout.12abc", "BlueBillywig.ClipPlayout.", "BlueBillywig.ClipPlayout.1.2",
            "BlueBillywig.apiSecretBackup", "BlueBillywig", "Umbraco.CoreUpgrade", "bluebillywig.apiId",
        ];
        foreach (string key in sentinels) kv.Rows[key] = "keep-" + key;
        kv.Rows["BlueBillywig.apiSecret"] = "secret";

        cleanup.Run();

        Assert.All(sentinels, k => Assert.Equal("keep-" + k, kv.Rows[k]));
        Assert.Equal("", kv.Rows["BlueBillywig.apiSecret"]);
    }

    [Fact]
    public void Per_clip_playouts_are_cleared_by_numeric_id_only()
    {
        (BlueBillywigCleanup cleanup, FakeKeyValues kv, _) = Rig();
        kv.Rows["BlueBillywig.ClipPlayout.11224"] = "wide";
        kv.Rows["BlueBillywig.ClipPlayout.7"] = "narrow";
        kv.Rows["BlueBillywig.ClipPlayout.not-a-number"] = "keep";

        IReadOnlyList<string> cleared = cleanup.Run();

        Assert.Equal(["BlueBillywig.ClipPlayout.11224", "BlueBillywig.ClipPlayout.7"], cleared.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("", kv.Rows["BlueBillywig.ClipPlayout.11224"]);
        Assert.Equal("keep", kv.Rows["BlueBillywig.ClipPlayout.not-a-number"]);
    }

    [Fact]
    public void Running_twice_clears_nothing_the_second_time_and_reports_names_not_values()
    {
        (BlueBillywigCleanup cleanup, FakeKeyValues kv, _) = Rig();
        kv.Rows["BlueBillywig.apiSecret"] = "TOP-SECRET-VALUE";

        IReadOnlyList<string> first = cleanup.Run();
        IReadOnlyList<string> second = cleanup.Run();

        Assert.Equal(["BlueBillywig.apiSecret"], first);
        Assert.Empty(second);
        Assert.DoesNotContain("TOP-SECRET-VALUE", string.Join(",", first));
    }

    [Theory]
    [InlineData("BlueBillywig.apiId", true)]
    [InlineData("BlueBillywig.ClipPlayout.1", true)]
    [InlineData("BlueBillywig.ClipPlayout.999999999999", true)]
    [InlineData("BlueBillywig.ClipPlayout.9999999999999", false)]
    [InlineData("BlueBillywig.*", false)]
    [InlineData("BlueBillywig.", false)]
    [InlineData("BlueBillywig.ClipPlayout.1; DROP", false)]
    public void Only_listed_keys_and_numeric_clip_playouts_are_ours(string key, bool ours) =>
        Assert.Equal(ours, BlueBillywigCleanup.IsOwnKey(key));
}

public class SettingsControllerTests
{
    private sealed class ThrowingClient : FakeClientBase
    {
        public override void ClearCache() => throw new InvalidOperationException("nope");
    }

    // FakeClient is sealed in spirit (its members are not virtual), so the throwing variant wraps the interface members it needs.
    private abstract class FakeClientBase : IBlueBillywigClient
    {
        public bool IsConnected => true;
        public string Publication => "demo";
        public string BaseUrl => "https://demo.bbvms.com";
        public Task<SapiResult> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, TimeSpan? cacheFor = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SapiResult> PostAsync(string path, object? json = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SapiResult> PutAsync(string path, object? json = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SapiResult> DeleteAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SapiResult> SendAsync(string method, string path, SapiRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SapiResult> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct = default) => throw new NotSupportedException();
        public string? GetReadOnlyToken() => null;
        public Task<SapiResult> TestConnectionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public abstract void ClearCache();
    }

    private static (SettingsController Controller, FakeSettings Settings, FakeClient Client, FakeKeyValues Kv) Rig(FakeTester? tester = null)
    {
        var settings = new FakeSettings();
        var client = new FakeClient();
        var kv = new FakeKeyValues();
        return (new SettingsController(settings, client, new BlueBillywigCleanup(kv, client), tester ?? new FakeTester(), NullLogger<SettingsController>.Instance), settings, client, kv);
    }

    private static SettingsController.SettingsRequest Request(string? playout = "default", string? apiId = "1", string? roId = null, string? embed = "javascript", int? pageCount = 15) =>
        new("demo", apiId, null, roId, null, playout, embed, pageCount, true, true);

    [Theory]
    [InlineData("../../sapi/admintoken")]
    [InlineData("foo/bar")]
    [InlineData("..")]
    [InlineData("my playout")]
    [InlineData("a\"b")]
    public async Task A_default_playout_that_is_not_a_slug_is_refused_before_anything_is_written(string playout)
    {
        (SettingsController controller, FakeSettings settings, FakeClient client, _) = Rig();
        BlueBillywigSettings before = settings.Value;

        IActionResult result = await controller.SaveSettings(Request(playout: playout));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Same(before, settings.Value);
        Assert.Equal(0, client.Cleared);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("my_playout-2")]
    [InlineData("Vertical-9x16")]
    public async Task Slug_playouts_are_accepted_and_empty_means_default(string playout)
    {
        (SettingsController controller, FakeSettings settings, _, _) = Rig();

        Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(playout: playout)));
        Assert.Equal(playout, settings.Value.Playout);

        await controller.SaveSettings(Request(playout: "  "));
        Assert.Equal("default", settings.Value.Playout);
    }

    [Fact]
    public async Task The_read_only_key_must_not_be_the_read_write_key()
    {
        (SettingsController controller, FakeSettings settings, _, _) = Rig();
        BlueBillywigSettings before = settings.Value;

        Assert.IsType<BadRequestObjectResult>(await controller.SaveSettings(Request(apiId: "7", roId: "7")));
        Assert.Same(before, settings.Value);
        Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(apiId: "7", roId: "8")));
    }

    [Theory]
    [InlineData("flash")]
    [InlineData("")]
    public async Task The_embed_type_is_one_of_the_plugins_five(string embed) =>
        Assert.IsType<BadRequestObjectResult>(await Rig().Controller.SaveSettings(Request(embed: embed)));

    [Theory]
    [InlineData("javascript")]
    [InlineData("iframe")]
    [InlineData("url")]
    [InlineData("amp")]
    [InlineData("oembed")]
    public async Task All_five_embed_types_save(string embed)
    {
        (SettingsController controller, FakeSettings settings, _, _) = Rig();

        await controller.SaveSettings(Request(embed: embed));

        Assert.Equal(embed, settings.Value.Embed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Videos_per_page_is_bounded(int pageCount) =>
        Assert.IsType<BadRequestObjectResult>(await Rig().Controller.SaveSettings(Request(pageCount: pageCount)));

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public async Task Videos_per_page_accepts_both_ends_of_the_range(int pageCount)
    {
        (SettingsController controller, FakeSettings settings, _, _) = Rig();

        Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(pageCount: pageCount)));

        Assert.Equal(pageCount, settings.Value.PageCount);
    }

    [Fact]
    public async Task A_save_clears_the_cache_and_says_so()
    {
        (SettingsController controller, _, FakeClient client, _) = Rig();

        var ok = Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request()));

        var saved = Assert.IsType<SettingsController.SaveSettingsResponse>(ok.Value);
        Assert.True(saved.CacheCleared);
        Assert.Equal(1, client.Cleared);
        Assert.True(saved.Settings.EnableCustomFields);
        Assert.Equal("default", saved.Settings.Playout);
    }

    [Fact]
    public async Task A_cache_clear_that_fails_is_reported_not_hidden_and_the_settings_are_still_saved()
    {
        var settings = new FakeSettings();
        var controller = new SettingsController(settings, new ThrowingClient(), new BlueBillywigCleanup(new FakeKeyValues()), new FakeTester(), NullLogger<SettingsController>.Instance);

        var ok = Assert.IsType<OkObjectResult>(await controller.SaveSettings(Request(playout: "wide")));

        Assert.False(Assert.IsType<SettingsController.SaveSettingsResponse>(ok.Value).CacheCleared);
        Assert.Equal("wide", settings.Value.Playout);
        Assert.DoesNotContain("\"ok\":true", JsonSerializer.Serialize(((OkObjectResult)controller.ClearCache(new SettingsController.ConfirmRequest(true))).Value));
    }

    [Fact]
    public void The_response_says_whether_secrets_are_set_and_never_returns_them()
    {
        (SettingsController controller, FakeSettings settings, _, _) = Rig();
        settings.Value = settings.Value with { ReadOnlyApiId = "5", ReadOnlyApiSecret = "RO-SECRET-VALUE", ApiSecret = "RW-SECRET-VALUE" };

        string json = JsonSerializer.Serialize(((OkObjectResult)controller.GetSettings()).Value);

        Assert.DoesNotContain("RO-SECRET-VALUE", json);
        Assert.DoesNotContain("RW-SECRET-VALUE", json);
        Assert.Contains("\"HasReadOnlyApiSecret\":true", json);
        Assert.Contains("\"HasApiSecret\":true", json);
    }

    [Fact]
    public async Task The_playout_dropdown_lists_the_publications_playouts_and_a_failure_is_a_502_without_upstream_text()
    {
        (SettingsController controller, _, FakeClient client, _) = Rig();
        client.Json("/sapi/playout/", """{"items":[{"name":"Default playout","label":"default"},{"name":"","label":"wide"},{"name":"Evil","label":"../x"}]}""");

        var ok = Assert.IsType<OkObjectResult>(await controller.Playouts(default));

        Assert.Equal(["default", "wide"], ((IReadOnlyList<PlayoutOptionResponseModel>)ok.Value!).Select(p => p.Value));
        Assert.Equal("wide", ((IReadOnlyList<PlayoutOptionResponseModel>)ok.Value!)[1].Label);

        client.Responses["/sapi/playout/"] = SapiResult.Failure(500, "SAPI request failed (500)");
        var failed = Assert.IsType<ObjectResult>(await controller.Playouts(default));
        Assert.Equal(502, failed.StatusCode);
        Assert.DoesNotContain("trace", JsonSerializer.Serialize(failed.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_cleanup_endpoint_runs_the_allowlisted_cleanup()
    {
        (SettingsController controller, _, FakeClient client, FakeKeyValues kv) = Rig();
        kv.Rows["BlueBillywig.apiSecret"] = "x";
        kv.Rows["BlueBillywig.license"] = "paid";

        var ok = Assert.IsType<OkObjectResult>(controller.Cleanup(new SettingsController.ConfirmRequest(true)));

        Assert.Equal(["BlueBillywig.apiSecret"], Assert.IsType<SettingsController.CleanupResponse>(ok.Value).Cleared);
        Assert.Equal("paid", kv.Rows["BlueBillywig.license"]);
        Assert.Equal(1, client.Cleared);
    }

    [Fact]
    public async Task The_status_endpoint_reports_configured_and_links_to_the_ovp_of_the_configured_base_url()
    {
        var settings = new FakeSettings { Value = new BlueBillywigSettings { Publication = "bb", BaseUrl = "https://bb.dev.example/", ApiId = "1", ApiSecret = "s", Playout = "wide" } };
        var client = new FakeClient();
        client.Json("/sapi/publication", """{"name":"Blue Billywig dev"}""");
        var controller = new StatusController(settings, client);

        var status = (StatusController.StatusResponse)((OkObjectResult)await controller.Get(default)).Value!;

        Assert.True(status.Configured);
        Assert.Equal("Blue Billywig dev", status.PublicationName);
        Assert.Equal("https://bb.dev.example/ovp/", status.OvpUrl);
        Assert.Equal("wide", status.DefaultPlayout);
    }

    [Fact]
    public async Task Without_credentials_the_status_says_unconfigured_and_asks_sapi_nothing()
    {
        var settings = new FakeSettings { Value = new BlueBillywigSettings { Publication = "bb", ApiId = "1" } };
        var client = new FakeClient();
        var controller = new StatusController(settings, client);

        var status = (StatusController.StatusResponse)((OkObjectResult)await controller.Get(default)).Value!;

        Assert.False(status.Configured);
        Assert.Null(status.PublicationName);
        Assert.Empty(client.Requests);
        Assert.Equal("https://bb.bbvms.com/ovp/", status.OvpUrl);
    }
}

/// <summary>S5 and S6: the settings take effect where they are used.</summary>
public class SettingsEffectTests
{
    [Fact]
    public async Task Videos_per_page_sets_the_library_page_size_and_the_offset()
    {
        var client = new FakeClient();
        var settings = new FakeSettings { Value = new BlueBillywigSettings { Publication = "demo", ApiId = "1", ApiSecret = "s", PageCount = 7 } };
        client.Json("/sapi/mediaclip", """{"numfound":30,"items":[]}""");
        client.Json("/sapi/folder", "{}");
        client.Json("/sapi/publication", """{"baseurl":"https://demo.bbvms.com"}""");
        var service = new LibraryService(client, settings);
        LibraryQuery.TryParse(null, null, null, null, null, null, 2, out LibraryFilter filter, out _);

        LibraryResult<LibraryClipListResponseModel> result = await service.ListClipsAsync(filter, LibraryQuery.MaxPageSize, default);

        IReadOnlyDictionary<string, string> query = client.Calls.First(c => c.Path == "/sapi/mediaclip").Request!.Query!;
        Assert.Equal("7", query["limit"]);
        Assert.Equal("14", query["offset"]);
        Assert.Equal(7, result.Value!.PageSize);
    }

    [Fact]
    public async Task The_preview_url_carries_only_the_read_only_token()
    {
        var client = new FakeClient { ReadOnlyToken = "55-readonlytoken" };
        var settings = new FakeSettings { Value = new BlueBillywigSettings { Publication = "demo", ApiId = "1", ApiSecret = "RW-SECRET-VALUE", ReadOnlyApiId = "55", ReadOnlyApiSecret = "ro" } };
        var service = new LibraryService(client, settings);

        string url = (await service.GetPreviewAsync(9, default)).Value!.Url;

        Assert.Contains("rpctoken=55-readonlytoken", url);
        Assert.DoesNotContain("RW-SECRET-VALUE", url);
        Assert.DoesNotContain("rpctoken=1-", url);
    }

    [Fact]
    public async Task Without_a_read_only_pair_the_preview_is_the_public_player_with_no_token()
    {
        var service = new LibraryService(new FakeClient { ReadOnlyToken = null }, new FakeSettings());

        LibraryResult<ClipPreviewResponseModel> result = await service.GetPreviewAsync(9, default);

        Assert.DoesNotContain("rpctoken", result.Value!.Url);
        Assert.False(result.Value.Authenticated);
    }
}

public class CustomFieldsToggleTests
{
    [Fact]
    public async Task With_custom_fields_off_the_editor_shows_none_and_never_asks_for_the_definitions()
    {
        var rig = new EditRig();
        rig.Settings.Value = rig.Settings.Value with { EnableCustomFields = false };
        rig.AddClip(7, """{"id":"7","title":"t","colour":"red","status":"published"}""");

        var result = await rig.Service.GetAsync(7, default);

        Assert.True(result.Ok);
        Assert.False(result.Value!.CustomFieldsEnabled);
        Assert.Empty(result.Value.CustomFields);
        Assert.DoesNotContain("/sapi/customfieldgroup", rig.Client.Requests);
    }
}

public class ReadOnlyTokenTests
{
    private static BlueBillywigClient Client(BlueBillywigSettings settings) =>
        new(new FakeSettings { Value = settings }, new MemoryCache(new MemoryCacheOptions()), NullLogger<BlueBillywigClient>.Instance, new HttpClient(new NoNetwork()));

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void A_distinct_read_only_pair_gives_a_token_with_its_own_id()
    {
        string? token = Client(new BlueBillywigSettings { Publication = "p", ApiId = "7", ApiSecret = "rw", ReadOnlyApiId = "8", ReadOnlyApiSecret = "ro" }).GetReadOnlyToken();

        Assert.NotNull(token);
        Assert.StartsWith("8-", token);
    }

    [Theory]
    [InlineData("7", "ro")] // same id
    [InlineData("8", "rw")] // same secret
    public void A_read_only_pair_that_is_the_read_write_key_gives_no_token(string roId, string roSecret) =>
        Assert.Null(Client(new BlueBillywigSettings { Publication = "p", ApiId = "7", ApiSecret = "rw", ReadOnlyApiId = roId, ReadOnlyApiSecret = roSecret }).GetReadOnlyToken());

    [Fact]
    public void No_pair_means_no_token() =>
        Assert.Null(Client(new BlueBillywigSettings { Publication = "p", ApiId = "7", ApiSecret = "rw" }).GetReadOnlyToken());
}

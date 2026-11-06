using System.Reflection;
using System.Text.Json;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>Finding 2: the SAPI origin is configuration only, and only on a Blue Billywig domain.</summary>
public class BaseUrlTests
{
    [Theory]
    [InlineData("https://example.dev.bbovp.eu", "https://example.dev.bbovp.eu")]
    [InlineData("https://demo.bbvms.com/", "https://demo.bbvms.com")]
    [InlineData("  https://Demo.BBVMS.com  ", "https://demo.bbvms.com")]
    [InlineData("https://x.ovp.bluebillywig.eu", "https://x.ovp.bluebillywig.eu")]
    [InlineData("https://x.bluebillywig.com", "https://x.bluebillywig.com")]
    [InlineData("https://bbvms.com", "https://bbvms.com")]
    public void Blue_billywig_origins_are_accepted(string value, string expected)
    {
        Assert.True(BlueBillywigHosts.TryNormalizeBaseUrl(value, out string origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://demo.bbvms.com")]
    [InlineData("https://evil.example")]
    [InlineData("https://evilbbvms.com")]
    [InlineData("https://bbvms.com.evil.example")]
    [InlineData("https://demo.bbvms.com.evil.example")]
    [InlineData("https://user:pw@demo.bbvms.com")]
    [InlineData("https://demo.bbvms.com:8443")]
    [InlineData("https://demo.bbvms.com/some/path")]
    [InlineData("https://demo.bbvms.com/?x=1")]
    [InlineData("https://demo.bbvms.com/#x")]
    [InlineData("https://internal.local")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("//demo.bbvms.com")]
    [InlineData("demo.bbvms.com")]
    public void Anything_else_is_refused(string value)
    {
        Assert.False(BlueBillywigHosts.TryNormalizeBaseUrl(value, out _));
    }

    private static BlueBillywigSettingsStore Store(FakeKeyValues kv, BlueBillywigOptions options) =>
        new(kv, new EphemeralDataProtectionProvider(), new FixedOptions(options));

    [Fact]
    public void A_base_url_in_configuration_on_a_blue_billywig_domain_is_used()
    {
        var store = Store(new FakeKeyValues(), new BlueBillywigOptions { Publication = "bb", ApiId = "1", ApiSecret = "s", BaseUrl = "https://example.dev.bbovp.eu/" });

        BlueBillywigSettings settings = store.Get();

        Assert.Equal("https://example.dev.bbovp.eu", settings.BaseUrl);
        Assert.Equal("https://example.dev.bbovp.eu", settings.ResolveBaseUrl());
        Assert.True(settings.HasCredentials);
        Assert.Contains("baseUrl", store.ConfigOverrides());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://example.dev.bbovp.eu")]
    [InlineData("https://example.dev.bbovp.eu.evil.example")]
    public void A_configured_base_url_outside_the_allowlist_switches_the_connection_off_instead_of_sending_the_token_there(string configured)
    {
        var store = Store(new FakeKeyValues(), new BlueBillywigOptions { Publication = "bb", ApiId = "1", ApiSecret = "s", BaseUrl = configured });

        BlueBillywigSettings settings = store.Get();

        Assert.True(settings.BaseUrlRejected);
        Assert.False(settings.HasCredentials);
        Assert.Null(settings.BaseUrl);
        Assert.DoesNotContain("evil", settings.ResolveBaseUrl());
    }

    [Fact]
    public void A_base_url_stored_by_an_earlier_version_is_no_longer_read_and_the_next_save_wipes_it()
    {
        var kv = new FakeKeyValues();
        kv.Rows["BlueBillywig.baseUrl"] = "https://evil.example";
        var store = Store(kv, new BlueBillywigOptions());

        Assert.Null(store.Get().BaseUrl);
        Assert.DoesNotContain("evil", store.Get().ResolveBaseUrl());

        store.Save(new BlueBillywigSettings { Publication = "p", BaseUrl = "https://evil.example" }, null, null);

        Assert.Equal("", kv.Rows["BlueBillywig.baseUrl"]);
        Assert.Null(store.Get().BaseUrl);
    }

    [Fact]
    public void The_settings_requests_have_no_base_url_and_a_body_that_names_one_binds_without_it()
    {
        Assert.Null(typeof(SettingsController.SettingsRequest).GetProperty("BaseUrl"));
        Assert.Null(typeof(SettingsController.TestConnectionRequest).GetProperty("BaseUrl"));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var save = JsonSerializer.Deserialize<SettingsController.SettingsRequest>("""{"publication":"demo","baseUrl":"https://evil.example","apiId":"1","enableCta":true}""", options);
        var test = JsonSerializer.Deserialize<SettingsController.TestConnectionRequest>("""{"publication":"demo","baseUrl":"https://evil.example","apiId":"1","apiSecret":"s"}""", options);

        Assert.NotNull(save);
        Assert.NotNull(test);
        Assert.DoesNotContain("evil", JsonSerializer.Serialize(save));
        Assert.DoesNotContain("evil", JsonSerializer.Serialize(test));
    }

    [Fact]
    public async Task Saving_and_testing_use_the_configured_base_url_whatever_the_caller_sends()
    {
        var settings = new FakeSettings();
        settings.Value = settings.Value with { BaseUrl = "https://example.dev.bbovp.eu" };
        var tester = new FakeTester();
        var client = new FakeClient();
        var controller = new SettingsController(settings, client, new BlueBillywigCleanup(new FakeKeyValues(), client), tester, NullLogger<SettingsController>.Instance);

        await controller.SaveSettings(new SettingsController.SettingsRequest("demo", "1", "typed-secret", null, null, "default", "javascript", 15, true, true));
        await controller.TestConnection(new SettingsController.TestConnectionRequest("demo", "1", "typed-secret"));

        Assert.Equal(2, tester.Asked.Count);
        Assert.All(tester.Asked, a => Assert.Equal("https://example.dev.bbovp.eu", a.BaseUrl));
        Assert.Equal("https://example.dev.bbovp.eu", settings.Value.BaseUrl);
    }

    [Fact]
    public async Task A_rejected_configured_base_url_is_not_given_to_the_connection_tester_as_connected()
    {
        var settings = new FakeSettings();
        settings.Value = settings.Value with { BaseUrlRejected = true };
        var tester = new FakeTester { Answer = new CredentialCheck(false, null, "Enter the publication, API ID and API secret to test the connection") };
        var client = new FakeClient();
        var controller = new SettingsController(settings, client, new BlueBillywigCleanup(new FakeKeyValues(), client), tester, NullLogger<SettingsController>.Instance);

        var ok = Assert.IsType<OkObjectResult>(await controller.TestConnection(new SettingsController.TestConnectionRequest(null, null, null)));

        Assert.True(tester.Asked.All(a => a.BaseUrlRejected && !a.HasCredentials));
        Assert.False(((SettingsController.TestConnectionResponse)ok.Value!).Ok);
    }

    [Fact]
    public void The_settings_response_still_reports_the_base_url_read_only()
    {
        PropertyInfo? property = typeof(SettingsController.SettingsResponse).GetProperty("BaseUrl");
        Assert.NotNull(property);
        Assert.NotNull(typeof(SettingsController.SettingsResponse).GetProperty("BaseUrlRejected"));
    }
}

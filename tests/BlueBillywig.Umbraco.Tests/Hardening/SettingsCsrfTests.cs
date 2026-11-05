using System.Reflection;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Tests.Hardening;

/// <summary>
/// S1: the settings actions that used to take no body (disconnect, clear cache, cleanup) and test-connection (body optional)
/// now require a JSON body. A cross-site HTML form can only send form-encoded or text bodies, which the JSON input formatter
/// refuses with 415, and a cross-site fetch with a JSON content type needs a CORS preflight that this API never grants.
/// </summary>
public class SettingsCsrfTests
{
    private static readonly string[] BodyActions =
    [
        nameof(SettingsController.Disconnect),
        nameof(SettingsController.ClearCache),
        nameof(SettingsController.Cleanup),
        nameof(SettingsController.TestConnection),
    ];

    private static (SettingsController Controller, FakeSettings Settings, FakeClient Client, FakeKeyValues Kv) Rig()
    {
        var settings = new FakeSettings();
        var client = new FakeClient();
        var kv = new FakeKeyValues();
        kv.Rows["BlueBillywig.publication"] = "demo";
        return (new SettingsController(settings, client, new BlueBillywigCleanup(kv, client), new FakeTester(), NullLogger<SettingsController>.Instance), settings, client, kv);
    }

    [Fact]
    public void These_actions_are_posts_with_a_required_body_parameter_that_the_json_binder_must_fill()
    {
        Assert.True(typeof(SettingsController).GetCustomAttribute<ApiControllerAttribute>(true) is not null);

        foreach (string name in BodyActions)
        {
            MethodInfo action = typeof(SettingsController).GetMethod(name)!;
            Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());

            ParameterInfo body = action.GetParameters().First(p => p.ParameterType != typeof(CancellationToken));
            Assert.False(body.HasDefaultValue, $"{name}: the body must not default to null");
            Assert.True(new NullabilityInfoContext().Create(body).WriteState == NullabilityState.NotNull, $"{name}: the body must be non-nullable");

            // [FromBody(EmptyBodyBehavior = Allow)] would let an empty POST through.
            FromBodyAttribute? fromBody = body.GetCustomAttribute<FromBodyAttribute>();
            Assert.True(fromBody is null || fromBody.EmptyBodyBehavior != Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow, name);
        }
    }

    [Fact]
    public void Every_post_in_the_settings_controller_takes_a_body()
    {
        foreach (MethodInfo action in typeof(SettingsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null))
        {
            Assert.Contains(action.GetParameters(), p => p.ParameterType != typeof(CancellationToken));
        }
    }

    [Fact]
    public void Disconnect_without_confirmation_does_nothing()
    {
        (SettingsController controller, FakeSettings settings, FakeClient client, _) = Rig();

        IActionResult result = controller.Disconnect(new SettingsController.ConfirmRequest(false));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, settings.Disconnects);
        Assert.Equal(0, client.Cleared);
        Assert.Equal("demo", settings.Value.Publication);
    }

    [Fact]
    public void Disconnect_with_confirmation_disconnects_and_clears_the_cache()
    {
        (SettingsController controller, FakeSettings settings, FakeClient client, _) = Rig();

        Assert.IsType<OkObjectResult>(controller.Disconnect(new SettingsController.ConfirmRequest(true)));

        Assert.Equal(1, settings.Disconnects);
        Assert.Equal(1, client.Cleared);
    }

    [Fact]
    public void Clear_cache_without_confirmation_does_nothing_and_with_it_clears()
    {
        (SettingsController controller, _, FakeClient client, _) = Rig();

        Assert.IsType<BadRequestObjectResult>(controller.ClearCache(new SettingsController.ConfirmRequest(false)));
        Assert.Equal(0, client.Cleared);

        Assert.IsType<OkObjectResult>(controller.ClearCache(new SettingsController.ConfirmRequest(true)));
        Assert.Equal(1, client.Cleared);
    }

    [Fact]
    public void Cleanup_without_confirmation_empties_nothing_and_with_it_empties_the_package_rows()
    {
        (SettingsController controller, _, _, FakeKeyValues kv) = Rig();

        Assert.IsType<BadRequestObjectResult>(controller.Cleanup(new SettingsController.ConfirmRequest(false)));
        Assert.Equal("demo", kv.Rows["BlueBillywig.publication"]);

        Assert.IsType<OkObjectResult>(controller.Cleanup(new SettingsController.ConfirmRequest(true)));
        Assert.Equal("", kv.Rows["BlueBillywig.publication"]);
    }

    [Fact]
    public void The_confirmation_body_defaults_to_not_confirmed_when_the_field_is_missing()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        Assert.False(System.Text.Json.JsonSerializer.Deserialize<SettingsController.ConfirmRequest>("""{}""", options)!.Confirm);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<SettingsController.ConfirmRequest>("""{"confirm":false}""", options)!.Confirm);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<SettingsController.ConfirmRequest>("""{"confirm":true}""", options)!.Confirm);
    }

    [Fact]
    public async Task Test_connection_uses_the_stored_values_for_an_empty_json_object()
    {
        (SettingsController controller, _, _, _) = Rig();

        IActionResult result = await controller.TestConnection(new SettingsController.TestConnectionRequest(null, null, null));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void The_client_sends_the_body_on_each_of_these_calls()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/BlueBillywig.Umbraco/Client/src"));
        string dashboard = File.ReadAllText(Path.Combine(root, "dashboards/settings.element.ts"));

        Assert.Contains("postSettingsClearCache({ body: { confirm: true } })", dashboard);
        Assert.Contains("postSettingsCleanup({ body: { confirm: true } })", dashboard);
        Assert.Contains("postSettingsDisconnect({ body: { confirm: true } })", dashboard);
        Assert.DoesNotContain("postSettingsClearCache()", dashboard);
        Assert.DoesNotContain("postSettingsCleanup()", dashboard);
        Assert.DoesNotContain("postSettingsDisconnect()", dashboard);

        string sdk = File.ReadAllText(Path.Combine(root, "api/sdk.gen.ts"));
        foreach (string url in new[] { "settings/cleanup", "settings/clear-cache", "settings/disconnect", "settings/test-connection" })
        {
            int at = sdk.IndexOf($"url: '/umbraco/bluebillywig/api/v1/{url}'", StringComparison.Ordinal);
            Assert.True(at > 0, url);
            Assert.Contains("'Content-Type': 'application/json'", sdk[at..Math.Min(sdk.Length, at + 250)]);
        }
    }
}

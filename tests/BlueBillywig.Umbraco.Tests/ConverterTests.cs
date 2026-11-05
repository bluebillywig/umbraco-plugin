using BlueBillywig.Umbraco.Embedding;

namespace BlueBillywig.Umbraco.Tests;

public class ConverterTests
{
    private static BlueBillywigVideoApiModel? Api(string json, FakeSettings? settings = null, bool expanding = false)
    {
        var converter = new BlueBillywigVideoPropertyValueConverter(settings ?? new FakeSettings());
        object? intermediate = converter.ConvertSourceToIntermediate(null!, null!, json, false);
        return converter.ConvertIntermediateToDeliveryApiObject(null!, null!, default, intermediate, false, expanding) as BlueBillywigVideoApiModel;
    }

    [Fact]
    public void Clip_exposes_ids_and_player_urls_only()
    {
        BlueBillywigVideoApiModel? api = Api("""{"type":"clip","id":12,"playout":"wide"}""");

        Assert.NotNull(api);
        Assert.Equal("clip", api.Type);
        Assert.Equal(12, api.Id);
        Assert.Equal("wide", api.Playout);
        Assert.Equal("https://demo.bbvms.com/p/wide/c/12.html", api.PlayerUrl);
        Assert.Equal("https://demo.bbvms.com/p/wide/c/12.js", api.ScriptUrl);
    }

    [Fact]
    public void Expanded_and_shallow_output_are_the_same_because_no_sapi_call_is_made()
    {
        Assert.Equal(Api("""{"type":"clip","id":12}""", expanding: true), Api("""{"type":"clip","id":12}""", expanding: false));
    }

    [Fact]
    public void Channel_and_shorts_urls()
    {
        Assert.Equal("https://demo.bbvms.com/ch/3.html", Api("""{"type":"channel","id":3}""")?.PlayerUrl);
        Assert.Equal("https://demo.bbvms.com/sh/4.html", Api("""{"type":"shorts","id":4}""")?.PlayerUrl);
    }

    [Fact]
    public void Player_url_uses_the_configured_host_and_never_a_token()
    {
        var settings = new FakeSettings();
        settings.Value = settings.Value with { BaseUrl = "https://bb.dev.example.eu", ReadOnlyApiId = "9", ReadOnlyApiSecret = "ro-secret", ApiSecret = "rw-secret" };

        BlueBillywigVideoApiModel? api = Api("""{"type":"clip","id":12}""", settings);

        Assert.StartsWith("https://bb.dev.example.eu/p/default/c/12", api?.PlayerUrl);
        Assert.DoesNotContain("token", api?.PlayerUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(api), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_or_invalid_values_convert_to_null() => Assert.Null(Api("{}"));
}

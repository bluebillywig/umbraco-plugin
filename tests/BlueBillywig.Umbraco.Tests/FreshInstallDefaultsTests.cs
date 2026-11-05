using BlueBillywig.Umbraco.Configuration;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>A fresh install starts with the reference plugin's option defaults (its <c>get_option</c> fallbacks in 2.0.5).</summary>
public class FreshInstallDefaultsTests
{
    [Fact]
    public void A_fresh_install_has_the_reference_plugins_defaults()
    {
        var settings = new BlueBillywigSettings();

        Assert.Equal("javascript", settings.Embed);
        Assert.Equal("default", settings.Playout);
        Assert.Equal(15, settings.PageCount);
        Assert.True(settings.EnableCta);
        Assert.False(settings.EnableCustomFields);
        Assert.Equal("", settings.Publication);
    }
}

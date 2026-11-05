using BlueBillywig.Umbraco.Composing;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Embedding;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Tests;

/// <summary>
/// X5: the Umbraco counterpart of the plugin's data filters is <see cref="IBlueBillywigClient"/>. A site that registers its own
/// implementation changes what the package's own services produce, whichever order it registers in. The container here is
/// built with the same registration methods the composers call, so changing a registration breaks these tests.
/// </summary>
public class ClientReplacementTests
{
    private const string Title = "Title from the site own client";

    private static FakeClient SiteClient()
    {
        var client = new FakeClient();
        client.Json("/sapi/publication", """{"name":"Demo","defaultMediaAssetPath":"https://cdn.example"}""");
        client.Json("/sapi/mediaclip", $$"""{"numfound":1,"items":[{"id":"5","title":"{{Title}}","thumbnails":[],"cat":[]}]}""");
        client.Json("/sapi/mediaclip/5", $$"""{"id":"5","title":"{{Title}}","thumbnails":[]}""");
        return client;
    }

    private static ServiceProvider Build(IBlueBillywigClient site, bool siteFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IKeyValueService>(new FakeKeyValues());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IOptionsMonitor<BlueBillywigOptions>>(new FixedOptions(new BlueBillywigOptions { Publication = "demo", ApiId = "7", ApiSecret = "secret" }));
        if (siteFirst)
        {
            services.AddSingleton(site);
        }

        BlueBillywigComposer.AddServices(services);
        BlueBillywigEmbedComposer.AddServices(services);
        if (!siteFirst)
        {
            services.AddSingleton(site);
        }

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sites_own_client_decides_what_the_library_lists(bool siteFirst)
    {
        using ServiceProvider provider = Build(SiteClient(), siteFirst);
        LibraryQuery.TryParse(null, null, null, null, null, null, 0, out LibraryFilter filter, out _);

        LibraryResult<LibraryClipListResponseModel> result = await provider.GetRequiredService<ILibraryService>().ListClipsAsync(filter, 100, default);

        Assert.True(result.Ok);
        Assert.Equal(Title, result.Value!.Items.Single().Title);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sites_own_client_decides_what_the_embed_renderer_produces(bool siteFirst)
    {
        using ServiceProvider provider = Build(SiteClient(), siteFirst);

        string html = (await provider.GetRequiredService<IBlueBillywigEmbedRenderer>().RenderAsync(new BlueBillywigVideo { Id = 5, LazyLoad = true })).ToHtmlString();

        Assert.Contains("Play: " + Title, html);
        Assert.IsType<FakeClient>(provider.GetRequiredService<IBlueBillywigClient>());
    }

    [Fact]
    public async Task Without_a_replacement_the_package_uses_its_own_client_so_the_test_above_proves_something()
    {
        using ServiceProvider provider = Build(SiteClient(), siteFirst: true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IKeyValueService>(new FakeKeyValues());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IOptionsMonitor<BlueBillywigOptions>>(new FixedOptions(new BlueBillywigOptions()));
        BlueBillywigComposer.AddServices(services);
        using ServiceProvider plain = services.BuildServiceProvider();

        Assert.IsType<BlueBillywigClient>(plain.GetRequiredService<IBlueBillywigClient>());
        Assert.IsType<FakeClient>(provider.GetRequiredService<IBlueBillywigClient>());
        await Task.CompletedTask;
    }
}

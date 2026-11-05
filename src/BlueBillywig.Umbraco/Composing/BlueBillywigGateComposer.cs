using BlueBillywig.Umbraco.Gate;
using BlueBillywig.Umbraco.Sapi;
using BlueBillywig.Umbraco.Seo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BlueBillywig.Umbraco.Composing;

/// <summary>Content protection (G1 to G8) and the video sitemap (Q3).</summary>
public sealed class BlueBillywigGateComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        IServiceCollection services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        // One tally per process; the banner reads it. IGateHealth is the same instance.
        services.AddSingleton<GateHealthTally>();
        services.AddSingleton<IGateHealth>(sp => sp.GetRequiredService<GateHealthTally>());

        services.AddSingleton<IGateWriteLock, ScopeWriteLock>();
        services.AddSingleton<GateSettingsStore>();
        services.AddSingleton<IGateConfigStore>(sp => sp.GetRequiredService<GateSettingsStore>());
        services.AddSingleton<IGateSettings>(sp => sp.GetRequiredService<GateSettingsStore>());
        services.AddSingleton<IGateSecretStore>(sp => sp.GetRequiredService<GateSettingsStore>());

        // TryAdd: a site with its own membership (an external identity provider, say) replaces these two.
        services.TryAddSingleton<IGateMemberSource, UmbracoGateMemberSource>();
        services.TryAddSingleton<IGateViewerProvider, MemberGateViewerProvider>();
        services.TryAddSingleton<IGateMemberGroupSource, UmbracoGateMemberGroupSource>();

        // The same instance serves the gate and the clip editor, so a policy change drops the gate's cached decision. It listens
        // to the SAPI cache signal, so a settings save, the cleanup action and a cache clear drop every cached decision too.
        services.AddSingleton<ICppLookup>(CreateCppLookup);
        services.AddSingleton<ContentGateFactory>();

        services.AddSingleton<ISitemapPageSource, UmbracoSitemapPageSource>();
        services.TryAddSingleton<ISitemapHostPolicy, ConfiguredSitemapHosts>();
        services.AddSingleton<IVideoSitemapService, VideoSitemapService>();
    }

    /// <summary>The lookup as registered; a test resolves it through the same factory to check the wiring.</summary>
    internal static ICppLookup CreateCppLookup(IServiceProvider sp) => new SapiCppLookup(
        sp.GetRequiredService<IBlueBillywigClient>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("BlueBillywig.ContentGate"),
        sp.GetRequiredService<ISapiCacheSignal>());
}

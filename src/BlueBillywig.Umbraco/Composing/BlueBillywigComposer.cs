using BlueBillywig.Umbraco.Analytics;
using BlueBillywig.Umbraco.Authorization;
using BlueBillywig.Umbraco.Configuration;
using BlueBillywig.Umbraco.Cta;
using BlueBillywig.Umbraco.Editing;
using BlueBillywig.Umbraco.Library;
using BlueBillywig.Umbraco.Upload;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BlueBillywig.Umbraco.Composing;

public sealed class BlueBillywigComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.Configure<BlueBillywigOptions>(builder.Config.GetSection(BlueBillywigOptions.SectionName));
        AddServices(builder.Services);
        AddCapabilities(builder);

        // On a load-balanced site the clear reaches the other nodes through Umbraco's distributed cache (see SapiCacheSignal).
        builder.CacheRefreshers().Add<SapiCacheRefresher>();
    }

    /// <summary>
    /// The registrations, apart from Umbraco's own builder so a test can run exactly what the composer runs. The SAPI client
    /// is the replaceable point of this package (see <see cref="IBlueBillywigClient"/>): <c>TryAdd</c>, so a site's own
    /// client registered before or after this composer is the one every other part resolves.
    /// </summary>
    internal static void AddServices(IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<IBlueBillywigSettingsStore, BlueBillywigSettingsStore>();
        services.TryAddSingleton<SapiCacheSignal>();
        services.TryAddSingleton<ISapiCacheSignal>(sp => sp.GetRequiredService<SapiCacheSignal>());
        services.TryAddSingleton<IBlueBillywigClient, BlueBillywigClient>();
        services.TryAddSingleton<ISapiCredentialTester, SapiCredentialTester>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IThumbnailState, ThumbnailState>();
        services.AddSingleton<IClipPlayoutStore, ClipPlayoutStore>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<IClipEditService, ClipEditService>();
        services.AddSingleton<IAnalyticsService, AnalyticsService>();
        services.AddSingleton<IBlueBillywigCleanup, BlueBillywigCleanup>();
        services.AddSingleton<IPendingUploads, PendingUploads>();
        services.AddSingleton<IUploadService, UploadService>();
        services.TryAddSingleton<ICtaIdGenerator, RandomCtaIdGenerator>();
        services.AddSingleton<ICtaService, CtaService>();
    }

    private static void AddCapabilities(IUmbracoBuilder builder)
    {
        // Capabilities: the section policy for reading and uploading, a user-group policy on top of it for delete.
        builder.Services.AddSingleton<IBackOfficeUserInfo, BackOfficeUserInfo>();
        builder.Services.AddSingleton<IAuthorizationHandler, SectionAccessHandler>();
        builder.Services.AddSingleton<IAuthorizationHandler, AnySectionAccessHandler>();
        builder.Services.AddSingleton<IAuthorizationHandler, UserGroupHandler>();
        builder.Services.AddSingleton<IPostConfigureOptions<AuthorizationOptions>, ConfigureBlueBillywigPolicies>();
    }
}

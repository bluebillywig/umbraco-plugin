using BlueBillywig.Umbraco.Embedding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BlueBillywig.Umbraco.Composing;

public sealed class BlueBillywigEmbedComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        AddServices(builder.Services);
        builder.EmbedProviders().Append<BlueBillywigOEmbedProvider>();
    }

    /// <summary>The registrations, apart from Umbraco's builder so a test can run exactly what the composer runs.</summary>
    internal static void AddServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        // TryAdd: a site's own consent or nonce provider, registered before or after, takes over.
        services.TryAddSingleton<IBlueBillywigConsentProvider, AlwaysGrantedConsentProvider>();
        services.TryAddSingleton<IBlueBillywigCspNonceProvider, NoCspNonceProvider>();
        // Who sees the embed problem marker; a site may replace it. Needs IBackOfficeUserInfo from the core composer.
        services.TryAddSingleton<IBlueBillywigEmbedDiagnostics, BackOfficeEmbedDiagnostics>();
        services.AddSingleton<BlueBillywigEmbedRenderer>();
        services.AddSingleton<IBlueBillywigEmbedRenderer>(sp => sp.GetRequiredService<BlueBillywigEmbedRenderer>());
        services.AddSingleton<IBlueBillywigAssets>(sp => sp.GetRequiredService<BlueBillywigEmbedRenderer>());
    }
}

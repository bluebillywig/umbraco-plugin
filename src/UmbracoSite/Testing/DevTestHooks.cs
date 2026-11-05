using System.Security.Cryptography;
using BlueBillywig.Umbraco.Embedding;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace UmbracoSite.Testing;

/// <summary>
/// Development-only hooks that let a person (or a browser check) switch the consent and CSP paths on and off per
/// browser, with cookies, without a restart. Never active outside the Development environment. The package itself
/// ships neither: a real site registers its own <see cref="IBlueBillywigConsentProvider"/> and nonce provider.
/// <list type="bullet">
/// <item><c>bb_test_consent=deny</c>: the consent provider refuses.</item>
/// <item><c>bb_test_csp=1</c>: the response carries a nonce-based Content-Security-Policy, and the nonce provider returns that nonce.</item>
/// <item><c>bb_test_csp=mismatch</c>: the same policy, but the nonce provider returns a different nonce. A negative control: if the browser enforces the policy, the scripts the package emits must be refused.</item>
/// </list>
/// </summary>
public sealed class DevTestHooksComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        if (!builder.BuilderEnvironmentIsDevelopment())
        {
            return;
        }

        builder.Services.AddSingleton<IBlueBillywigConsentProvider, DevConsentProvider>();
        builder.Services.AddSingleton<IBlueBillywigCspNonceProvider, DevNonceProvider>();
    }
}

internal static class BuilderExtensions
{
    public static bool BuilderEnvironmentIsDevelopment(this IUmbracoBuilder builder) =>
        string.Equals(builder.Config["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
}

internal sealed class DevConsentProvider : IBlueBillywigConsentProvider
{
    public bool IsGranted(HttpContext? httpContext) =>
        httpContext?.Request.Cookies["bb_test_consent"] != "deny";
}

internal sealed class DevNonceProvider : IBlueBillywigCspNonceProvider
{
    public string? GetNonce(HttpContext? httpContext) =>
        httpContext?.Items[DevCspMiddleware.RenderedNonceKey] as string;
}

internal sealed class DevCspMiddleware
{
    public const string RenderedNonceKey = "UmbracoSite.DevCspNonce";
    private readonly RequestDelegate _next;

    public DevCspMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        string? mode = context.Request.Cookies["bb_test_csp"];
        if (mode is "1" or "mismatch" && !context.Request.Path.StartsWithSegments("/umbraco"))
        {
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            // In mismatch mode the page's scripts carry a nonce the policy does not list.
            context.Items[RenderedNonceKey] = mode == "mismatch" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) : nonce;
            // 'strict-dynamic' trusts scripts that a nonced script adds, which is how the lazy loader starts the player.
            context.Response.Headers.ContentSecurityPolicy =
                $"default-src 'self'; script-src 'nonce-{nonce}' 'strict-dynamic'; style-src 'self' 'unsafe-inline'; img-src * data:; frame-src https:; connect-src *; media-src *";
        }

        return _next(context);
    }
}

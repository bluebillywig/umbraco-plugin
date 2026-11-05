using Asp.Versioning;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Umbraco.Cms.Api.Common.OpenApi;
using Umbraco.Cms.Api.Management.OpenApi;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BlueBillywig.Umbraco.Composing;

/// <summary>Publishes this package's backoffice API as its own OpenAPI document, for the generated TypeScript client.</summary>
public sealed class BlueBillywigApiComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<IOperationIdHandler, OperationIds>();
        builder.Services.Configure<SwaggerGenOptions>(options =>
        {
            options.SwaggerDoc(Constants.ApiName, new OpenApiInfo { Title = "Blue Billywig Umbraco Backoffice API", Version = "1.0" });
            options.OperationFilter<SecurityFilter>();
        });
    }

    public sealed class SecurityFilter : BackOfficeSecurityRequirementsOperationFilterBase
    {
        protected override string ApiName => Constants.ApiName;
    }

    /// <summary>Operation IDs like <c>GetSettings</c> give the generated client readable function names.</summary>
    public sealed class OperationIds : OperationIdHandler
    {
        public OperationIds(IOptions<ApiVersioningOptions> apiVersioningOptions) : base(apiVersioningOptions)
        {
        }

        protected override bool CanHandle(ApiDescription apiDescription, ControllerActionDescriptor controllerActionDescriptor) =>
            controllerActionDescriptor.ControllerTypeInfo.Namespace?.StartsWith("BlueBillywig.Umbraco.Controllers", StringComparison.InvariantCultureIgnoreCase) is true;
    }
}

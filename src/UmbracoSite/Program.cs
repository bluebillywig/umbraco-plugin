
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

#if DEBUG
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
#endif

// The image-resizing signing key is not kept in the tracked appsettings.json. Set Umbraco:CMS:Imaging:HMACSecretKey in the
// git-ignored appsettings.Local.json or the environment (Umbraco__CMS__Imaging__HMACSecretKey) to keep resized-image URLs
// valid across restarts. Without one, this demo site makes a random key for the life of the process.
if (string.IsNullOrWhiteSpace(builder.Configuration["Umbraco:CMS:Imaging:HMACSecretKey"]))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Umbraco:CMS:Imaging:HMACSecretKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)),
    });
}

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddDeliveryApi()
    .AddComposers()
    .Build();

WebApplication app = builder.Build();

#if DEBUG
// Development-only switches for the consent and CSP checks; see Testing/DevTestHooks.cs.
if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<UmbracoSite.Testing.DevCspMiddleware>();
}
#endif


await app.BootUmbracoAsync();


app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

await app.RunAsync();

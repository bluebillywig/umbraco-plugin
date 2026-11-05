namespace BlueBillywig.Umbraco.Configuration;

/// <summary>Effective settings. Mirrors the reference plugin's <c>blue-billywig-*</c> options.</summary>
public sealed record BlueBillywigSettings
{
    public string Publication { get; init; } = "";

    /// <summary>
    /// The SAPI origin. Comes from configuration only (<c>BlueBillywig:BaseUrl</c>, a Blue Billywig domain); never from the
    /// backoffice, see <see cref="BlueBillywigHosts"/>.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// A <c>BaseUrl</c> was configured but is not an acceptable origin. The settings then count as not connected: no call is
    /// made, so the credentials cannot be sent to a host nobody approved.
    /// </summary>
    public bool BaseUrlRejected { get; init; }

    /// <summary>
    /// A configured or stored publication failed <see cref="PublicationSlug"/>. <see cref="Publication"/> is then empty, so
    /// the settings count as not connected, as with a rejected <c>BaseUrl</c>: nothing is built from a value that is not a host label.
    /// </summary>
    public bool PublicationRejected { get; init; }
    public string ApiId { get; init; } = "";
    public string ApiSecret { get; init; } = "";
    public string ReadOnlyApiId { get; init; } = "";
    public string ReadOnlyApiSecret { get; init; } = "";
    public string Playout { get; init; } = "default";
    public string Embed { get; init; } = "javascript";
    public int PageCount { get; init; } = 15;
    public bool EnableCustomFields { get; init; }
    public bool EnableCta { get; init; } = true;

    /// <summary>The consent placeholder's sentence, plain text. Empty: the built-in text (<see cref="Embedding.ConsentMessage.Default"/>).</summary>
    public string ConsentMessage { get; init; } = "";

    public bool HasCredentials =>
        !BaseUrlRejected && !PublicationRejected && Publication.Length > 0 && ApiId.Length > 0 && ApiSecret.Length > 0;

    public string ResolveBaseUrl() =>
        string.IsNullOrWhiteSpace(BaseUrl) ? $"https://{Publication}.bbvms.com" : BaseUrl.TrimEnd('/');
}

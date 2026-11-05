namespace BlueBillywig.Umbraco.Sapi;

/// <summary>Options for <see cref="IBlueBillywigClient.SendAsync(string, string, SapiRequest, CancellationToken)"/>.</summary>
public sealed record SapiRequest
{
    public IReadOnlyDictionary<string, string>? Query { get; init; }
    public object? Json { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// The single chokepoint for SAPI access, and the package's documented interception point: the Umbraco counterpart of the
/// reference plugin's data filters (<c>blue_billywig_media_clip</c>, <c>_clip_list</c>, <c>_get_embedcode</c>,
/// <c>_get_playout_list</c>, <c>_publication_data</c>, <c>_save_new_data</c>, <c>_delete_clip</c>). Everything the package
/// reads or writes at Blue Billywig (the renderer, the library, the clip editor, the gate, the sitemap, the backoffice
/// API) goes through the registered <see cref="IBlueBillywigClient"/>, so a site that registers its own implementation
/// (for example one that decorates the default, or answers from a fixture) changes what all of those produce. Register it
/// before or after the package's composer; the package uses <c>TryAdd</c>, so either order works.
/// <para>
/// The other replaceable points, each registered the same way: <see cref="Embedding.IBlueBillywigConsentProvider"/> (may
/// this visitor load players, and the text of the consent placeholder), <see cref="Embedding.IBlueBillywigCspNonceProvider"/>
/// (the CSP nonce for the scripts the renderer emits) and <see cref="Gate.IGateViewerProvider"/> (who the viewer is, for
/// content protection). A client that wraps the default should forward <see cref="ClearCache"/> to it: the manual "Clear
/// cache" button and the cleanup action call it, and the default's call is what empties the gate's CPP lookup as well
/// (a settings save does that on its own, through <see cref="ISapiCacheSignal"/>).
/// </para>
/// </summary>
public interface IBlueBillywigClient
{
    bool IsConnected { get; }
    string Publication { get; }
    string BaseUrl { get; }

    Task<SapiResult> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, TimeSpan? cacheFor = null, CancellationToken ct = default);
    Task<SapiResult> PostAsync(string path, object? json = null, CancellationToken ct = default);
    Task<SapiResult> PutAsync(string path, object? json = null, CancellationToken ct = default);
    Task<SapiResult> DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// The general form: any verb with query parameters, a JSON body and extra headers. Used where the
    /// other methods cannot say enough, such as the upload calls, which need the tus headers.
    /// </summary>
    Task<SapiResult> SendAsync(string method, string path, SapiRequest request, CancellationToken ct = default);

    /// <summary>Sends a pre-built body, for multipart calls such as subtitle and thumbnail uploads.</summary>
    Task<SapiResult> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct = default);

    /// <summary>
    /// A short-lived read-only token for previews printed into backoffice HTML. Null when no read-only
    /// pair is configured: the read-write secret must never reach a browser.
    /// </summary>
    string? GetReadOnlyToken();

    Task<SapiResult> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Drops every cached read.</summary>
    void ClearCache();
}

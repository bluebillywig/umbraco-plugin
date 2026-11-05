namespace BlueBillywig.Umbraco.Gate;

/// <summary>What a content protection policy can be attached to, and so what the JWT's <c>entity_access</c> names.</summary>
public enum GateEntityKind
{
    /// <summary>A clip. Claim key <c>mediaclip</c>; SAPI path <c>/sapi/mediaclip/{id}</c>.</summary>
    MediaClip,

    /// <summary>A channel. Claim key <c>channel</c>; SAPI path <c>/sapi/channel/{id}</c>.</summary>
    Channel,
}

public readonly record struct GateEntity(GateEntityKind Kind, long Id)
{
    public static GateEntity Clip(long id) => new(GateEntityKind.MediaClip, id);

    public static GateEntity ChannelOf(long id) => new(GateEntityKind.Channel, id);

    public string ClaimKey => Kind == GateEntityKind.Channel ? "channel" : "mediaclip";

    public string SapiPath => Kind == GateEntityKind.Channel ? $"/sapi/channel/{Id}" : $"/sapi/mediaclip/{Id}";

    /// <summary>Tally reason for "SAPI answered but the entity is not there".</summary>
    public string NotFoundReason => Kind == GateEntityKind.Channel ? GateReasons.ChannelNotFound : GateReasons.ClipNotFound;
}

/// <summary>The reasons the gate closes on a knowable error, as the plugin's tally keys them, plus the ones added here.</summary>
public static class GateReasons
{
    public const string SapiDisconnected = "sapi_disconnected";
    public const string SapiError = "sapi_error";
    public const string ClipNotFound = "clip_not_found";
    public const string ChannelNotFound = "channel_not_found";
    public const string SecretMissing = "secret_missing";

    // Not in the plugin: closing for these used to be silent or a bare log line.
    public const string SaltMissing = "salt_missing";
    public const string InvalidEntity = "invalid_entity";
    public const string InjectFailed = "inject_failed";
    public const string InternalError = "internal_error";

    /// <summary>A token would have been sent but the response could not be marked private (headers already sent).</summary>
    public const string CacheUnsafe = "cache_unsafe";
}

/// <summary>
/// Who is viewing, as decided by the host. The wiring maps the current Umbraco member (or none) to this; the gate never
/// looks at members, roles or HTTP itself.
/// </summary>
/// <param name="IsAuthenticated">Logged in. Anonymous viewers are always denied a protected clip.</param>
/// <param name="UserId">
/// A stable identifier of the member (a Guid key is better than an int; it must not change when a member is renamed). It is
/// never put in a token: only its HMAC is. Null for anonymous viewers.
/// </param>
/// <param name="IsAllowed">The role / member-group decision for this entity. Must default to false; true only on a positive match.</param>
/// <param name="LoginUrl">Where an anonymous viewer can log in and come back. Shown in the gate message when it is a relative path or http(s) URL.</param>
public sealed record GateViewer(bool IsAuthenticated, string? UserId, bool IsAllowed, string? LoginUrl = null)
{
    public static GateViewer Anonymous(string? loginUrl = null) => new(false, null, false, loginUrl);
}

/// <summary>Maps "who is viewing" to an access decision for one entity.</summary>
public interface IGateViewerProvider
{
    GateViewer GetViewer(GateEntity entity);
}

/// <summary>The gate's settings. The wiring reads them from the settings store; unset values use the defaults on <see cref="GateJwt"/>.</summary>
public interface IGateSettings
{
    /// <summary>Master switch. When false nothing is gated (plugin: <c>blue-billywig-cp-enabled</c>).</summary>
    bool Enabled { get; }

    /// <summary>Requested token lifetime in seconds. Clamped to 300 to 7200 whatever is stored.</summary>
    long ExpirySeconds { get; }

    /// <summary>Optional custom gate message. Plain text: it is HTML-encoded, not trusted as markup.</summary>
    string? CustomMessage { get; }
}

/// <summary>
/// Secrets the gate needs. Seam for the wiring: implement over Umbraco's key-value settings with the values protected at
/// rest (Data Protection), never logged, never returned from any API. Every method may return null or false on failure and
/// the gate then closes.
/// </summary>
public interface IGateSecretStore
{
    /// <summary>The content protection policy's shared secret (plugin: <c>blue-billywig-cp-secret</c>). Null or empty fails closed.</summary>
    string? GetSharedSecret();

    /// <summary>
    /// A per-install secret that replaces the plugin's site auth salt, for example derived from the site's Data
    /// Protection key. Must stay the same across restarts. Null fails closed.
    /// </summary>
    string? GetSalt();

    /// <summary>The stored pepper, or null when none is stored.</summary>
    string? GetPepper();

    /// <summary>Stores <paramref name="candidate"/> only if no pepper row exists. Must be atomic (insert-if-absent). False when a row exists or the write failed.</summary>
    bool TryAddPepper(string candidate);

    /// <summary>Overwrites the stored pepper (used once, to replace a too-short or unreadable one). False when the write failed.</summary>
    bool TrySetPepper(string candidate);
}

/// <summary>Where the gate reports that it closed on an error, and that the pepper could not be saved. Feeds the admin banner (G7).</summary>
public interface IGateHealth
{
    /// <summary>The gate closed on a knowable error (not on a legitimate denial). Keyed by <see cref="GateReasons"/>.</summary>
    void RecordGateError(string reason);

    /// <summary>The pepper could not be stored, so tokens cannot be tied to a stable <c>sub</c>.</summary>
    void RecordPepperPersistFailure();
}

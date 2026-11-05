using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Gate;

public enum GateOutcome
{
    /// <summary>The gate is off, or the entity has no policy: the embed is returned untouched.</summary>
    PassThrough,

    /// <summary>The viewer may watch: the embed carries a signed token.</summary>
    TokenInjected,

    /// <summary>Anonymous or not allowed: the gate message replaced the embed. A legitimate denial, not an error.</summary>
    Denied,

    /// <summary>Something could not be verified (SAPI, secret, salt, injection): the gate message replaced the embed and the error was tallied.</summary>
    ClosedOnError,
}

/// <summary>
/// What to render, and whether the response may be stored by a shared cache.
/// </summary>
/// <param name="Reason">For <see cref="GateOutcome.ClosedOnError"/>, the <see cref="GateReasons"/> key that was tallied.</param>
public sealed record GateResult(GateOutcome Outcome, string Html, string? Reason = null)
{
    /// <summary>
    /// True for every outcome except <see cref="GateOutcome.PassThrough"/>. The token holds the viewer's pseudonym and the
    /// gate message varies with login state, so a shared cache must not store either (plugin: <c>mark_response_private</c>,
    /// called from the gate message and from the JWT paths; port of <c>GateMessagePrivateTest</c>). The wiring must send
    /// <c>Cache-Control: private, no-store</c> on the response when this is true, and also keep the umbraco output cache
    /// and any page cache from storing it.
    /// </summary>
    public bool MustBePrivate => Outcome != GateOutcome.PassThrough;
}

/// <summary>
/// The content gate for one embed (plugin: <c>filter_embed_block</c> and <c>filter_channel_block</c>). Pure logic over
/// seams: settings, secrets, who is viewing, whether the entity has a CPP, and a clock. It never throws and every
/// doubt ends in the gate message, never in the embed.
/// <para>
/// Seam for the wiring: call <see cref="ApplyAsync"/> from the render chokepoint with the finished embed HTML, only for
/// clips and channels, and obey <see cref="GateResult.MustBePrivate"/>. Do not render a protected embed, or its noscript
/// fallback, anywhere else.
/// </para>
/// </summary>
public sealed class ContentGate
{
    private readonly IGateSettings _settings;
    private readonly IGateSecretStore _secrets;
    private readonly IGateViewerProvider _viewers;
    private readonly ICppLookup _cpp;
    private readonly IGateHealth _health;
    private readonly GatePepper _pepper;
    private readonly Func<string, bool> _isGatedHost;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ContentGate(
        IGateSettings settings,
        IGateSecretStore secrets,
        IGateViewerProvider viewers,
        ICppLookup cpp,
        IGateHealth health,
        Func<string, bool> isGatedHost,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        _settings = settings;
        _secrets = secrets;
        _viewers = viewers;
        _cpp = cpp;
        _health = health;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _pepper = new GatePepper(secrets, health, _logger);
        _isGatedHost = isGatedHost;
    }

    public async Task<GateResult> ApplyAsync(GateEntity entity, string embedHtml, CancellationToken ct = default)
    {
        GateViewer viewer = GateViewer.Anonymous();
        try
        {
            if (!_settings.Enabled)
            {
                return new GateResult(GateOutcome.PassThrough, embedHtml);
            }

            // The gate message depends on the viewer, so it is fetched before any exit. A provider that fails is an error, not "anonymous".
            viewer = _viewers.GetViewer(entity) ?? GateViewer.Anonymous();
            return await ApplyCoreAsync(entity, embedHtml, viewer, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Blue Billywig ContentGate: unexpected failure gating {Kind}={Id}; rendering the gate message.", entity.Kind, entity.Id);
            return Closed(GateReasons.InternalError, viewer);
        }
    }

    private async Task<GateResult> ApplyCoreAsync(GateEntity entity, string embedHtml, GateViewer viewer, CancellationToken ct)
    {
        // The plugin passed an embed with no id straight through. An embed we cannot identify cannot be shown to be unprotected.
        if (entity.Id <= 0)
        {
            _logger.LogError("Blue Billywig ContentGate: embed without a valid {Kind} id; rendering the gate message.", entity.Kind);
            return Closed(GateReasons.InvalidEntity, viewer);
        }

        CppStatus status = await _cpp.GetStatusAsync(entity, ct);
        switch (status)
        {
            case CppStatus.NoCpp:
                return new GateResult(GateOutcome.PassThrough, embedHtml);
            case CppStatus.Disconnected:
                return Closed(GateReasons.SapiDisconnected, viewer);
            case CppStatus.Error:
                return Closed(GateReasons.SapiError, viewer);
            case CppStatus.NotFound:
                return Closed(entity.NotFoundReason, viewer);
            case CppStatus.HasCpp:
                break;
            default:
                return Closed(GateReasons.InternalError, viewer);
        }

        // A legitimate denial: not tallied, but still private.
        if (!viewer.IsAuthenticated || !viewer.IsAllowed)
        {
            return new GateResult(GateOutcome.Denied, Message(viewer));
        }

        string? secret = _secrets.GetSharedSecret();
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogError("Blue Billywig ContentGate: no shared secret configured for {Kind}={Id}; rendering the gate message.", entity.Kind, entity.Id);
            return Closed(GateReasons.SecretMissing, viewer);
        }

        string? salt = _secrets.GetSalt();
        if (string.IsNullOrEmpty(salt))
        {
            _logger.LogError("Blue Billywig ContentGate: no per-install salt available for {Kind}={Id}; rendering the gate message.", entity.Kind, entity.Id);
            return Closed(GateReasons.SaltMissing, viewer);
        }

        string sub = GateSubject.Anonymous;
        if (!string.IsNullOrEmpty(viewer.UserId))
        {
            sub = GateSubject.For(viewer.UserId, salt, _pepper.GetOrCreate().Pepper);
        }

        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        string jwt = GateJwt.Sign(secret, sub, now, now + GateJwt.ClampExpiry(_settings.ExpirySeconds), entity);

        // One injector per call: its gate message depends on this viewer, and the gate itself is shared between requests.
        InjectResult injected = new GateInjector(_isGatedHost, () => Message(viewer), _logger).Inject(embedHtml, jwt);
        if (injected.FailedClosed)
        {
            _health.RecordGateError(GateReasons.InjectFailed);
            return new GateResult(GateOutcome.ClosedOnError, injected.Html, GateReasons.InjectFailed);
        }

        if (injected.Substitutions == 0)
        {
            // Nothing matched: the viewer gets the embed without a token and the platform will refuse it. Not a leak, but a
            // broken video with no context, so it is surfaced (plugin: a log line only).
            _logger.LogError("Blue Billywig ContentGate: no player URL found to inject the token into for {Kind}={Id}; the embed will fail to play.", entity.Kind, entity.Id);
            _health.RecordGateError(GateReasons.InjectFailed);
        }

        return new GateResult(GateOutcome.TokenInjected, injected.Html);
    }

    private GateResult Closed(string reason, GateViewer viewer)
    {
        _health.RecordGateError(reason);
        return new GateResult(GateOutcome.ClosedOnError, Message(viewer), reason);
    }

    private string Message(GateViewer viewer) => GateMessage.Render(viewer.IsAuthenticated, viewer.LoginUrl, _settings.CustomMessage);
}

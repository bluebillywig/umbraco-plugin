using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>The pepper to sign with, and whether it is stored (so the next request will use the same one).</summary>
public sealed record PepperResult(string Pepper, bool Persisted);

/// <summary>
/// Lazily creates and reads the per-install pepper (plugin: <c>getJwtPepper</c>, <c>bb_jwt_pepper</c>). Rotating the
/// stored value changes every <c>sub</c>, which is the expected effect of a pepper rotation.
/// <para>
/// Seam for the wiring: <see cref="IGateSecretStore.TryAddPepper"/> must be an atomic insert-if-absent, or two requests
/// can each generate a pepper and the losing one signs a token no later request reproduces.
/// </para>
/// <para>
/// Decision when the pepper cannot be stored: degrade like the plugin (sign with the in-memory candidate, log, and flag
/// the admin banner) instead of closing the gate. The pepper only shapes <c>sub</c>, a pseudonym; a token signed with an
/// unstored pepper is still signed with the policy secret and still names only the one entity, so no access is widened,
/// whereas closing would lock every member out of every protected clip over a settings-table write problem.
/// </para>
/// </summary>
public sealed class GatePepper
{
    public const int MinLength = 32;
    public const int GeneratedLength = 64;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly IGateSecretStore _store;
    private readonly IGateHealth _health;
    private readonly ILogger _logger;
    private readonly Func<string> _generate;

    public GatePepper(IGateSecretStore store, IGateHealth health, ILogger? logger = null)
        : this(store, health, logger, () => RandomNumberGenerator.GetString(Alphabet, GeneratedLength))
    {
    }

    internal GatePepper(IGateSecretStore store, IGateHealth health, ILogger? logger, Func<string> generate)
    {
        _store = store;
        _health = health;
        _logger = logger ?? NullLogger.Instance;
        _generate = generate;
    }

    public PepperResult GetOrCreate()
    {
        if (IsUsable(_store.GetPepper()) is { } stored)
        {
            return new PepperResult(stored, true);
        }

        string candidate = _generate();
        if (_store.TryAddPepper(candidate))
        {
            return new PepperResult(candidate, true);
        }

        // Another request won the race: use whatever actually landed.
        if (IsUsable(_store.GetPepper()) is { } winner)
        {
            return new PepperResult(winner, true);
        }

        // The insert failed for another reason (a write that was dropped, or a too-short value in the row). Try an overwrite once.
        bool written = _store.TrySetPepper(candidate);
        string? after = _store.GetPepper();
        if (written && after == candidate)
        {
            return new PepperResult(candidate, true);
        }

        if (IsUsable(after) is { } landed)
        {
            // A concurrent request stored a different pepper in the meantime: it is the canonical one.
            return new PepperResult(landed, true);
        }

        _logger.LogError("Blue Billywig ContentGate: the JWT pepper could not be persisted; this request's tokens may not round-trip. Check that the settings store is writable.");
        if (string.IsNullOrEmpty(after))
        {
            _health.RecordPepperPersistFailure();
        }

        return new PepperResult(candidate, false);
    }

    private static string? IsUsable(string? pepper) => pepper is { Length: >= MinLength } ? pepper : null;
}

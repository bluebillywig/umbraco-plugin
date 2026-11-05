using System.Security.Cryptography;
using System.Text.Json;
using BlueBillywig.Umbraco.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Gate;

/// <summary>
/// The content protection settings (plugin: <c>blue-billywig-cp-enabled</c>, <c>-expiry</c>, <c>-roles</c>, <c>-message</c>;
/// the shared secret is <c>blue-billywig-cp-secret</c>). <see cref="MemberGroups"/> is the Umbraco equivalent of the
/// plugin's role list.
/// </summary>
/// <param name="ExpirySeconds">Already clamped to 300 to 7200 seconds.</param>
/// <param name="MemberGroups">
/// Names of the member groups that may watch protected content. An empty list allows nobody: the plugin's default let every
/// logged-in user in (<c>administrator, editor, author, subscriber</c>), but an Umbraco site has no such built-in
/// "everybody who can log in" group, and a gate that opens on an unconfigured list is the wrong way round.
/// </param>
/// <param name="LoginUrl">
/// Where the gate message sends an anonymous visitor. A relative path or http(s) URL; <c>{returnUrl}</c> is replaced by the
/// current page (URL-encoded) so a login page can send the visitor back. Empty: the message has no login link.
/// </param>
public sealed record GateConfig(bool Enabled, int ExpirySeconds, IReadOnlyList<string> MemberGroups, string? Message, string? LoginUrl);

/// <summary>Read and write access for the settings dashboard. The shared secret is write-only here, like the API secrets.</summary>
public interface IGateConfigStore
{
    GateConfig Get();

    bool HasSharedSecret { get; }

    /// <summary>True when the shared secret comes from configuration and so cannot be changed in the backoffice.</summary>
    bool SharedSecretPinned { get; }

    /// <summary>Saves the config. A null secret keeps the stored one; an empty one clears it.</summary>
    void Save(GateConfig config, string? sharedSecret);
}

/// <summary>Validation of what the dashboard sends. Pure, so it is tested without a database.</summary>
public static class GateConfigRules
{
    public const int MaxGroups = 100;
    public const int MaxGroupName = 255;
    public const int MaxMessage = 2000;
    public const int MaxLoginUrl = 2048;
    /// <summary>
    /// For a new value. A secret stored before the minimum went from 16 to 32 keeps working (it passed the check of its day);
    /// a value pinned in configuration is checked against this on every read.
    /// </summary>
    public const int MinSecret = 32;
    public const int MaxSecret = 512;

    /// <summary>The cleaned config, or an error sentence that is safe to show.</summary>
    public static (GateConfig? Config, string? Error) Validate(bool enabled, long? expirySeconds, IEnumerable<string>? groups, string? message, string? loginUrl)
    {
        var cleaned = new List<string>();
        foreach (string raw in groups ?? [])
        {
            string name = (raw ?? "").Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (name.Length > MaxGroupName || name.Any(char.IsControl))
            {
                return (null, "Invalid member group name");
            }

            if (!cleaned.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                cleaned.Add(name);
            }
        }

        if (cleaned.Count > MaxGroups)
        {
            return (null, "Too many member groups");
        }

        string? text = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (text is { Length: > MaxMessage })
        {
            return (null, $"The gate message is too long (at most {MaxMessage} characters)");
        }

        string? login = string.IsNullOrWhiteSpace(loginUrl) ? null : loginUrl.Trim();
        if (login is not null && (login.Length > MaxLoginUrl || GateMessage.SafeLoginUrl(login.Replace("{returnUrl}", "x", StringComparison.Ordinal)) is null))
        {
            return (null, "The login URL must be a path starting with / or an http(s) URL");
        }

        int expiry = (int)GateJwt.ClampExpiry(expirySeconds ?? GateJwt.DefaultExpirySeconds);
        return (new GateConfig(enabled, expiry, cleaned, text, login), null);
    }

    /// <summary>Null when acceptable. The secret is never echoed in the error.</summary>
    public static string? ValidateSecret(string secret)
    {
        if (secret.Length < MinSecret || secret.Length > MaxSecret)
        {
            return $"The shared secret must be between {MinSecret} and {MaxSecret} characters";
        }

        return secret.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? "The shared secret must not contain spaces or control characters" : null;
    }
}

/// <summary>Runs an action while no other request or server can write the key-value table (insert-if-absent needs it).</summary>
internal interface IGateWriteLock
{
    T Run<T>(Func<T> action);
}

/// <summary>
/// Umbraco's own key-value write lock (<c>Constants.Locks.KeyValues</c>, the one <c>KeyValueService</c> takes for every
/// write), taken in a scope that the service's calls join. Held across servers by the database, not by this process.
/// </summary>
internal sealed class ScopeWriteLock : IGateWriteLock
{
    private readonly ICoreScopeProvider _scopes;

    public ScopeWriteLock(ICoreScopeProvider scopes) => _scopes = scopes;

    public T Run<T>(Func<T> action)
    {
        using ICoreScope scope = _scopes.CreateCoreScope();
        scope.WriteLock(global::Umbraco.Cms.Core.Constants.Locks.KeyValues);
        T result = action();
        scope.Complete();
        return result;
    }
}

/// <summary>
/// Persistence for the gate, in the same key-value table as the other settings (keys <c>BlueBillywig.gate.*</c>).
/// <list type="bullet">
/// <item>The shared secret and the salt are protected with Data Protection. If the keys change they read as unset: the
/// gate then closes with <c>secret_missing</c> (the admin enters the secret again), and a salt is simply made anew.</item>
/// <item>The pepper is stored as it is: it is not a credential, and it is deliberately kept apart from the salt (the
/// salt is encrypted with a Data Protection key), so a dump of the key-value table alone cannot reverse a <c>sub</c>.
/// Note that salt and pepper both live in that one table. How much the encryption adds depends on where the site keeps its
/// Data Protection keys: when they are in the same database too (a common choice for load-balanced sites), a full database
/// dump carries everything needed. The <c>sub</c> is a pseudonym, not a credential; the policy's shared secret is what signs
/// the token.</item>
/// <item>A shared secret in configuration (<c>BlueBillywig:GateSharedSecret</c>) wins over a stored one.</item>
/// </list>
/// <para>
/// Deliberate differences to the reference plugin (G9, G2), pending the owner's decision. Nothing here changes behaviour:
/// </para>
/// <list type="number">
/// <item>Access defaults. The plugin's role list defaults to administrator, editor, author and subscriber, so the moment the
/// gate is on every logged-in user of those roles may watch. Here the member group list defaults to empty and an empty list
/// allows nobody (fail closed) until groups are chosen in the settings dashboard: Umbraco has no built-in "everybody who can
/// log in" group, and a gate that opens on an unconfigured list is the wrong way round. A backoffice login is not a member.</item>
/// <item>The gate message. The plugin shows an admin-supplied message as HTML filtered to a safe subset of tags
/// (an HTML allowlist filter). Here the message is plain text and HTML-encoded on output (<see cref="GateMessage.Render"/>), so markup,
/// links and formatting an admin typed show as text; only line breaks survive. No HTML filter ships with the package that
/// could be relied on, and the message sits on public pages.</item>
/// </list>
/// </summary>
internal sealed class GateSettingsStore : IGateConfigStore, IGateSettings, IGateSecretStore
{
    private const string Prefix = "BlueBillywig.gate.";
    internal const string EnabledKey = Prefix + "enabled";
    internal const string ExpiryKey = Prefix + "expiry";
    internal const string GroupsKey = Prefix + "groups";
    internal const string MessageKey = Prefix + "message";
    internal const string LoginUrlKey = Prefix + "loginUrl";
    internal const string SecretKey = Prefix + "secret";
    internal const string SaltKey = Prefix + "salt";
    internal const string PepperKey = Prefix + "pepper";

    /// <summary>Every key this store writes, for the uninstall action.</summary>
    internal static readonly string[] Keys = [EnabledKey, ExpiryKey, GroupsKey, MessageKey, LoginUrlKey, SecretKey, SaltKey, PepperKey];

    private const int GeneratedSaltLength = 48;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly IKeyValueService _keyValues;
    private readonly IDataProtector _protector;
    private readonly IOptionsMonitor<BlueBillywigOptions> _options;
    private readonly IGateWriteLock _lock;
    private readonly ILogger _logger;
    private bool _pinnedRejectedLogged;

    public GateSettingsStore(
        IKeyValueService keyValues,
        IDataProtectionProvider dataProtection,
        IOptionsMonitor<BlueBillywigOptions> options,
        IGateWriteLock writeLock,
        ILogger<GateSettingsStore>? logger = null)
    {
        _keyValues = keyValues;
        _protector = dataProtection.CreateProtector("BlueBillywig.Umbraco.Gate.v1");
        _options = options;
        _lock = writeLock;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // IGateConfigStore ---------------------------------------------------------------------------------------

    public GateConfig Get()
    {
        int expiry = long.TryParse(Read(ExpiryKey), out long stored) ? (int)GateJwt.ClampExpiry(stored) : GateJwt.DefaultExpirySeconds;
        return new GateConfig(Enabled, expiry, ReadGroups(), Read(MessageKey), Read(LoginUrlKey));
    }

    public bool HasSharedSecret => !string.IsNullOrEmpty(GetSharedSecret());

    public bool SharedSecretPinned => !string.IsNullOrWhiteSpace(_options.CurrentValue.GateSharedSecret);

    public void Save(GateConfig config, string? sharedSecret)
    {
        Write(EnabledKey, config.Enabled ? "1" : "0");
        Write(ExpiryKey, ((int)GateJwt.ClampExpiry(config.ExpirySeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Write(GroupsKey, JsonSerializer.Serialize(config.MemberGroups));
        Write(MessageKey, config.Message ?? "");
        Write(LoginUrlKey, config.LoginUrl ?? "");
        if (sharedSecret is not null)
        {
            Write(SecretKey, sharedSecret.Length == 0 ? "" : _protector.Protect(sharedSecret));
        }
    }

    // IGateSettings ------------------------------------------------------------------------------------------

    public bool Enabled => Read(EnabledKey) == "1";

    public long ExpirySeconds => Get().ExpirySeconds;

    public string? CustomMessage => Read(MessageKey);

    // IGateSecretStore ---------------------------------------------------------------------------------------

    public string? GetSharedSecret()
    {
        string? configured = _options.CurrentValue.GateSharedSecret;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // A pinned value bypassed the dashboard's rules (length, no whitespace). A weak one fails closed: no secret, so
            // the gate renders its message with reason secret_missing, and the log says why.
            if (GateConfigRules.ValidateSecret(configured) is { } problem)
            {
                if (!_pinnedRejectedLogged)
                {
                    _pinnedRejectedLogged = true;
                    _logger.LogError("Blue Billywig ContentGate: BlueBillywig:GateSharedSecret is not acceptable ({Problem}); protected content stays closed until it is fixed.", problem);
                }

                return null;
            }

            return configured;
        }

        return Unprotect(Read(SecretKey));
    }

    public string? GetSalt()
    {
        try
        {
            if (Unprotect(Read(SaltKey)) is { Length: >= 32 } existing)
            {
                return existing;
            }

            return _lock.Run(() =>
            {
                // Re-read inside the lock: another request or server may have made it in the meantime.
                if (Unprotect(Read(SaltKey)) is { Length: >= 32 } raced)
                {
                    return raced;
                }

                // Absent, or unreadable because the Data Protection keys changed: a new one. Every sub changes with it,
                // which is the same effect as rotating the pepper.
                string salt = RandomNumberGenerator.GetString(Alphabet, GeneratedSaltLength);
                Write(SaltKey, _protector.Protect(salt));
                return salt;
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Blue Billywig ContentGate: the per-install salt could not be read or stored.");
            return null;
        }
    }

    public string? GetPepper() => Read(PepperKey);

    public bool TryAddPepper(string candidate)
    {
        try
        {
            return _lock.Run(() =>
            {
                if (!string.IsNullOrEmpty(Read(PepperKey)))
                {
                    return false;
                }

                Write(PepperKey, candidate);
                return Read(PepperKey) == candidate;
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Blue Billywig ContentGate: storing the pepper failed.");
            return false;
        }
    }

    public bool TrySetPepper(string candidate)
    {
        try
        {
            return _lock.Run(() =>
            {
                Write(PepperKey, candidate);
                return Read(PepperKey) == candidate;
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Blue Billywig ContentGate: overwriting the pepper failed.");
            return false;
        }
    }

    // Helpers ------------------------------------------------------------------------------------------------

    private IReadOnlyList<string> ReadGroups()
    {
        string? json = Read(GroupsKey);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray() ?? [];
        }
        catch (JsonException)
        {
            // An unreadable list allows nobody.
            return [];
        }
    }

    private string? Read(string key)
    {
        string? value = _keyValues.GetValue(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private string? Unprotect(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private void Write(string key, string value) => _keyValues.SetValue(key, value);
}

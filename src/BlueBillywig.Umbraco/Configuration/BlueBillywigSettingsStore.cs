using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Configuration;

public interface IBlueBillywigSettingsStore
{
    BlueBillywigSettings Get();

    /// <summary>Which credential fields are pinned by config and so cannot be edited in the backoffice.</summary>
    IReadOnlyCollection<string> ConfigOverrides();

    /// <summary>Saves settings. A null secret keeps the stored one, so the UI never has to read secrets back.</summary>
    void Save(BlueBillywigSettings settings, string? apiSecret, string? readOnlyApiSecret);

    /// <summary>
    /// Empties the stored publication, API pair and read-only pair (the reference plugin's disconnect). Values pinned by
    /// configuration are not stored and stay. Raises <see cref="Changed"/>.
    /// </summary>
    void Disconnect();

    event Action? Changed;
}

/// <summary>Key-value store persistence, with secrets encrypted at rest by Data Protection.</summary>
internal sealed class BlueBillywigSettingsStore : IBlueBillywigSettingsStore
{
    private const string Prefix = "BlueBillywig.";

    private readonly IKeyValueService _keyValues;
    private readonly IDataProtector _protector;
    private readonly IOptionsMonitor<BlueBillywigOptions> _options;
    private readonly ILogger<BlueBillywigSettingsStore>? _logger;
    private string? _rejectedLogged;
    private string? _publicationRejectedLogged;

    public BlueBillywigSettingsStore(
        IKeyValueService keyValues,
        IDataProtectionProvider dataProtection,
        IOptionsMonitor<BlueBillywigOptions> options,
        ILogger<BlueBillywigSettingsStore>? logger = null)
    {
        _keyValues = keyValues;
        _protector = dataProtection.CreateProtector("BlueBillywig.Umbraco.Settings.v1");
        _options = options;
        _logger = logger;
    }

    public event Action? Changed;

    public BlueBillywigSettings Get()
    {
        BlueBillywigOptions o = _options.CurrentValue;
        string? baseUrl = null;
        bool rejected = false;
        if (!string.IsNullOrWhiteSpace(o.BaseUrl))
        {
            // The only source of the SAPI origin is configuration. A value an earlier version stored from the dashboard is
            // not read any more (it is wiped on the next save and by cleanup): the dashboard could be used to point the
            // read-write token at any https host.
            if (BlueBillywigHosts.TryNormalizeBaseUrl(o.BaseUrl, out string origin))
            {
                baseUrl = origin;
            }
            else
            {
                rejected = true;
                LogRejected(o.BaseUrl);
            }
        }

        string publication = Pick(o.Publication, Read("publication")) ?? "";
        bool publicationRejected = publication.Length > 0 && !PublicationSlug.IsValid(publication);
        if (publicationRejected)
        {
            LogPublicationRejected(publication);
            publication = "";
        }

        return new BlueBillywigSettings
        {
            Publication = publication,
            PublicationRejected = publicationRejected,
            BaseUrl = baseUrl,
            BaseUrlRejected = rejected,
            ApiId = Pick(o.ApiId, Read("apiId")) ?? "",
            ApiSecret = Pick(o.ApiSecret, ReadSecret("apiSecret")) ?? "",
            ReadOnlyApiId = Pick(o.ReadOnlyApiId, Read("roApiId")) ?? "",
            ReadOnlyApiSecret = Pick(o.ReadOnlyApiSecret, ReadSecret("roApiSecret")) ?? "",
            Playout = Read("playout") ?? "default",
            Embed = Read("embed") ?? "javascript",
            PageCount = int.TryParse(Read("pageCount"), out int pageCount) ? pageCount : 15,
            EnableCustomFields = Read("enableCustomFields") == "1",
            EnableCta = Read("enableCta") != "0",
            ConsentMessage = Embedding.ConsentMessage.Clean(Pick(o.ConsentPlaceholderMessage, Read("consentMessage"))),
        };
    }

    public IReadOnlyCollection<string> ConfigOverrides()
    {
        BlueBillywigOptions o = _options.CurrentValue;
        var pinned = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) pinned.Add(name);
        }
        Add("publication", o.Publication);
        Add("baseUrl", o.BaseUrl);
        Add("apiId", o.ApiId);
        Add("apiSecret", o.ApiSecret);
        Add("roApiId", o.ReadOnlyApiId);
        Add("roApiSecret", o.ReadOnlyApiSecret);
        Add("consentMessage", o.ConsentPlaceholderMessage);
        return pinned;
    }

    public void Save(BlueBillywigSettings s, string? apiSecret, string? readOnlyApiSecret)
    {
        Write("publication", s.Publication);
        // Never stored any more (configuration only); the key is emptied so a value planted by an earlier version is gone.
        Write("baseUrl", "");
        Write("apiId", s.ApiId);
        Write("roApiId", s.ReadOnlyApiId);
        Write("playout", s.Playout);
        Write("embed", s.Embed);
        Write("pageCount", s.PageCount.ToString());
        Write("enableCustomFields", s.EnableCustomFields ? "1" : "0");
        Write("enableCta", s.EnableCta ? "1" : "0");
        Write("consentMessage", Embedding.ConsentMessage.Clean(s.ConsentMessage));
        if (apiSecret is not null) Write("apiSecret", apiSecret.Length == 0 ? "" : _protector.Protect(apiSecret));
        if (readOnlyApiSecret is not null) Write("roApiSecret", readOnlyApiSecret.Length == 0 ? "" : _protector.Protect(readOnlyApiSecret));
        Changed?.Invoke();
    }

    public void Disconnect()
    {
        Write("publication", "");
        Write("apiId", "");
        Write("apiSecret", "");
        Write("roApiId", "");
        Write("roApiSecret", "");
        Changed?.Invoke();
    }

    private void LogRejected(string configured)
    {
        if (_rejectedLogged == configured)
        {
            return;
        }

        _rejectedLogged = configured;
        _logger?.LogError("BlueBillywig:BaseUrl is not an accepted SAPI origin (https, default port, no path, host under bbvms.com, bluebillywig.eu, bluebillywig.com or bbovp.eu). Blue Billywig is treated as not connected until it is fixed.");
    }

    private void LogPublicationRejected(string publication)
    {
        if (_publicationRejectedLogged == publication)
        {
            return;
        }

        _publicationRejectedLogged = publication;
        _logger?.LogError("The configured or stored Blue Billywig publication is not a valid slug (lowercase letters, digits, dots and dashes; 3 to 63 characters). Blue Billywig is treated as not connected until it is fixed.");
    }

    private static string? Pick(string? configured, string? stored) =>
        string.IsNullOrWhiteSpace(configured) ? stored : configured;

    private string? Read(string key)
    {
        string? value = _keyValues.GetValue(Prefix + key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private string? ReadSecret(string key)
    {
        string? value = Read(key);
        if (value is null) return null;
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Data Protection keys changed: treat as unset so the UI asks for the secret again.
            return null;
        }
    }

    private void Write(string key, string value) => _keyValues.SetValue(Prefix + key, value);
}

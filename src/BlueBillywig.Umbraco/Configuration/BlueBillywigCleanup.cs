using Umbraco.Cms.Core.Services;

namespace BlueBillywig.Umbraco.Configuration;

/// <summary>
/// The Umbraco counterpart of the reference plugin's uninstall script. A NuGet package has no uninstall hook, so
/// removing the package leaves what it stored in Umbraco's key-value table behind, secrets included. This is the explicit
/// action that clears it: an administrator runs it from the settings dashboard before the package is removed, or when
/// credentials must not outlive a handover.
/// <para>
/// It touches only the keys named here. The list is explicit on purpose, like the plugin's: no wildcard, no
/// <c>FindByKeyPrefix("BlueBillywig.")</c>-and-clear, because another package may keep its own entries under a similar
/// name (the plugin's test keeps a <c>blue-billywig-license</c> entry alive for the same reason). The one open-ended
/// family is the per-clip playout, <c>BlueBillywig.ClipPlayout.{clip id}</c>, and only a numeric id matches it.
/// </para>
/// <para>
/// Umbraco's <see cref="IKeyValueService"/> cannot delete a key. "Removed" therefore means the value is set to an empty
/// string, which this package reads as unset; the empty rows remain until the table is cleaned by hand. Caches in memory
/// are cleared too: the SAPI answers and the gate's CPP lookup through <see cref="Sapi.IBlueBillywigClient.ClearCache"/>
/// (on a load-balanced site the other nodes as well, see <see cref="Sapi.ISapiCacheSignal"/>), and the thumbnail state of
/// this node, which a plain cache clear leaves alone (the plugin's uninstall script removes it, its <c>clearCache()</c> does not). The Data Protection key ring is the host's and is not touched.
/// </para>
/// </summary>
public interface IBlueBillywigCleanup
{
    /// <summary>Clears every key that holds a value. Returns the names of the keys it cleared, never their values.</summary>
    IReadOnlyList<string> Run();
}

internal sealed class BlueBillywigCleanup : IBlueBillywigCleanup
{
    internal const string Prefix = "BlueBillywig.";
    internal const string ClipPlayoutPrefix = Prefix + "ClipPlayout.";

    /// <summary>
    /// Every fixed key the package writes (<see cref="BlueBillywigSettingsStore.Save"/>), listed by name. Add a key
    /// here when the package starts storing a new one; the test that compares this list with the store fails until then.
    /// </summary>
    internal static readonly string[] Keys =
    [
        Prefix + "publication",
        Prefix + "baseUrl",
        Prefix + "apiId",
        Prefix + "apiSecret",
        Prefix + "roApiId",
        Prefix + "roApiSecret",
        Prefix + "playout",
        Prefix + "embed",
        Prefix + "pageCount",
        Prefix + "enableCustomFields",
        Prefix + "enableCta",
        Prefix + "consentMessage",

        // Content protection (Gate/GateSettingsStore.cs). The salt and the pepper are in the list on purpose: clearing them
        // rotates every token's sub, which is what an uninstall should do.
        .. Gate.GateSettingsStore.Keys,
    ];

    private readonly IKeyValueService _keyValues;
    private readonly Sapi.IBlueBillywigClient? _client;
    private readonly Editing.IThumbnailState? _thumbnails;

    public BlueBillywigCleanup(IKeyValueService keyValues, Sapi.IBlueBillywigClient? client = null, Editing.IThumbnailState? thumbnails = null)
    {
        _keyValues = keyValues;
        _client = client;
        _thumbnails = thumbnails;
    }

    /// <summary>True for a key this package owns and may clear: a fixed key, or a per-clip playout with a numeric id.</summary>
    internal static bool IsOwnKey(string key) =>
        Keys.Contains(key, StringComparer.Ordinal)
        || (key.StartsWith(ClipPlayoutPrefix, StringComparison.Ordinal)
            && key.Length > ClipPlayoutPrefix.Length
            && key.Length <= ClipPlayoutPrefix.Length + 12
            && key.AsSpan(ClipPlayoutPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0);

    public IReadOnlyList<string> Run()
    {
        var cleared = new List<string>();
        foreach (string key in Keys)
        {
            Clear(key, cleared);
        }

        IReadOnlyDictionary<string, string?>? playouts = _keyValues.FindByKeyPrefix(ClipPlayoutPrefix);
        foreach (string key in playouts?.Keys ?? Enumerable.Empty<string>())
        {
            Clear(key, cleared);
        }

        _thumbnails?.Clear();

        // Last: this can throw (see SapiCacheSignal) and the rows are already emptied by now.
        _client?.ClearCache();
        return cleared;
    }

    private void Clear(string key, List<string> cleared)
    {
        // The guard is the allowlist itself: nothing outside IsOwnKey is ever written, whatever the prefix lookup returned.
        if (!IsOwnKey(key) || string.IsNullOrEmpty(_keyValues.GetValue(key)))
        {
            return;
        }

        _keyValues.SetValue(key, "");
        cleared.Add(key);
    }
}

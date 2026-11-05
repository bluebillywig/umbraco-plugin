using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Cache;

namespace BlueBillywig.Umbraco.Sapi;

/// <summary>
/// The one "SAPI answers may be stale now" signal. Everything in this package that remembers a SAPI answer in memory (the
/// client's read cache, the gate's CPP lookup) subscribes here, so a settings save, the cleanup action and
/// <see cref="IBlueBillywigClient.ClearCache"/> all empty the same set. Counterpart of the reference plugin's
/// <c>SapiClient::clearCache()</c>, which wipes the playout, publication, CPP and per-clip CPP transients in one pass.
/// </summary>
public interface ISapiCacheSignal
{
    /// <summary>Raised on this node when cached SAPI answers must be dropped, whichever node asked for it.</summary>
    event Action? Cleared;

    /// <summary>Raised on this node when the cached content protection answer for one clip must be dropped (its policy changed on any node). The argument is the clip id.</summary>
    event Action<long>? ClipInvalidated;

    /// <summary>Drops the clip's cached answer on this node and tells the other nodes to do the same.</summary>
    void InvalidateClip(long clipId);
}

/// <summary>
/// Raises <see cref="ISapiCacheSignal.Cleared"/> on this node and tells the other nodes of a load-balanced site to do the
/// same through Umbraco's distributed cache (<see cref="DistributedCache"/> and <see cref="ICacheRefresher"/>, both part of
/// Umbraco; no extra library).
/// <para>
/// Multi-node: the caches are in memory, so a plain clear reaches only the node that handled the request. The broadcast
/// writes a cache instruction that Umbraco's messenger hands to the other nodes (a database instruction, picked up on their
/// next sync, normally within seconds, and written when the saving request ends). Until a node has processed it, that node
/// can still decide from an answer cached before the save. The per-clip <see cref="InvalidateClip"/> after a clip's policy
/// changes is broadcast the same way, with a payload of just the clip id, so the other nodes drop their cached policy lookup
/// for that clip as soon as they process the instruction instead of after the lookup's five minutes. On a single node none of
/// this applies. The broadcast is skipped (and the local drop still happens) where no distributed cache is available, such as
/// in unit tests.
/// </para>
/// <para>
/// Failed-clear notice: the reference plugin's notice exists because its clear is a database DELETE that can fail. The local
/// clear here is in memory and cannot fail on its own; what can fail is a subscriber that throws, or writing the broadcast
/// instruction. <see cref="Clear"/> runs every subscriber even if one throws, then throws, so the caller (the settings
/// controller) reports "cache could not be cleared" instead of hiding stale answers.
/// </para>
/// </summary>
internal sealed class SapiCacheSignal : ISapiCacheSignal, IDisposable
{
    private readonly IServiceProvider? _services;
    private readonly ILogger _logger;
    private readonly Configuration.IBlueBillywigSettingsStore? _settings;

    public SapiCacheSignal(Configuration.IBlueBillywigSettingsStore? settings = null, IServiceProvider? services = null, ILogger<SapiCacheSignal>? logger = null)
    {
        _settings = settings;
        _services = services;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChanged;
        }
    }

    public event Action? Cleared;

    public event Action<long>? ClipInvalidated;

    /// <summary>Drops one clip on this node and asks the others to do the same. Throws after trying both if either failed.</summary>
    public void InvalidateClip(long clipId)
    {
        var failures = new List<Exception>();
        Try(() => InvalidateClipLocal(clipId), failures);
        Try(() => _services?.GetService<DistributedCache>()?.RefreshByPayload(SapiCacheRefresher.RefresherId, [new SapiCacheRefresher.ClipPayload(clipId)]), failures);
        Throw(failures);
    }

    /// <summary>This node only. What the cache refresher runs when another node asked; it must not broadcast again.</summary>
    public void InvalidateClipLocal(long clipId)
    {
        var failures = new List<Exception>();
        foreach (Delegate handler in ClipInvalidated?.GetInvocationList() ?? [])
        {
            Try(() => ((Action<long>)handler).Invoke(clipId), failures);
        }

        Throw(failures);
    }

    /// <summary>Clears this node and asks the others to clear theirs. Throws after trying everything if any part failed.</summary>
    public void Clear()
    {
        var failures = new List<Exception>();
        Try(ClearLocal, failures);
        Try(Broadcast, failures);
        Throw(failures);
    }

    /// <summary>Clears this node only. What the cache refresher runs when another node asked; it must not broadcast again.</summary>
    public void ClearLocal()
    {
        var failures = new List<Exception>();
        foreach (Delegate handler in Cleared?.GetInvocationList() ?? [])
        {
            Try(((Action)handler).Invoke, failures);
        }

        Throw(failures);
    }

    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    // A failing clear must not abort the settings save that raised the event: the values are stored by now, and the controller
    // clears again right after and reports the outcome.
    private void OnSettingsChanged()
    {
        try
        {
            Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clearing the Blue Billywig caches after a settings change failed");
        }
    }

    private void Broadcast() =>
        _services?.GetService<DistributedCache>()?.RefreshAll(SapiCacheRefresher.RefresherId);

    private static void Try(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static void Throw(List<Exception> failures)
    {
        if (failures.Count > 0)
        {
            throw new AggregateException("Clearing the Blue Billywig caches failed", failures);
        }
    }
}

/// <summary>
/// Runs on every node of a load-balanced site when Umbraco's messenger delivers the "clear" instruction. Resolves the signal
/// lazily because <see cref="DistributedCache"/> needs the refreshers, and the signal needs the distributed cache.
/// </summary>
internal sealed class SapiCacheRefresher : ICacheRefresher, IJsonCacheRefresher
{
    public static readonly Guid RefresherId = new("b1b9a8c4-6d0e-4f4c-9a45-4e5b8c7d2a11");

    private readonly IServiceProvider _services;

    public SapiCacheRefresher(IServiceProvider services) => _services = services;

    /// <summary>The whole payload of a per-clip instruction: the clip id and nothing else.</summary>
    internal sealed record ClipPayload(long ClipId);

    public Guid RefresherUniqueId => RefresherId;

    public string Name => "Blue Billywig SAPI cache";

    // Local only: the node that asked has already cleared itself and written the instruction for the others.
    public void RefreshAll() => _services.GetRequiredService<SapiCacheSignal>().ClearLocal();

    public void Refresh(int id) => RefreshAll();

    public void Refresh(Guid id) => RefreshAll();

    public void Remove(int id) => RefreshAll();

    /// <summary>A per-clip instruction from another node. Accepts <c>[{"ClipId":1}]</c> in either casing; anything else is ignored.</summary>
    public void Refresh(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        SapiCacheSignal signal = _services.GetRequiredService<SapiCacheSignal>();
        foreach (JsonNode? item in node as JsonArray ?? [])
        {
            JsonNode? id = item is JsonObject o ? o["ClipId"] ?? o["clipId"] : item;
            if (id is JsonValue v && v.TryGetValue(out long clipId) && clipId > 0)
            {
                signal.InvalidateClipLocal(clipId);
            }
        }
    }

    // In-memory only, as RefreshInternal requires.
    public void RefreshInternal(string json) => Refresh(json);
}

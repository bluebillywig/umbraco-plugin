using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BlueBillywig.Umbraco.Sapi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueBillywig.Umbraco.Gate;

public enum CppStatus
{
    /// <summary>SAPI answered and the entity carries a content protection policy.</summary>
    HasCpp,

    /// <summary>SAPI answered and the entity has no policy. The only status that lets content through ungated.</summary>
    NoCpp,

    /// <summary>No SAPI credentials: the gate cannot know, so it closes.</summary>
    Disconnected,

    /// <summary>SAPI failed (outage, 5xx, 401, unreadable body). Closes.</summary>
    Error,

    /// <summary>SAPI answered without the entity (404 or an empty body). Closes.</summary>
    NotFound,
}

/// <summary>Whether a clip or channel is protected. Seam for the gate; <see cref="SapiCppLookup"/> is the SAPI-backed one.</summary>
public interface ICppLookup
{
    Task<CppStatus> GetStatusAsync(GateEntity entity, CancellationToken ct = default);

    /// <summary>
    /// Drops the cached answer for one entity. The wiring must call this whenever a clip's or channel's policy changes
    /// (the clip edit service's CPP write, the bulk CPP action), or a newly protected clip plays ungated until the cache expires.
    /// </summary>
    void Invalidate(GateEntity entity);
}

/// <summary>
/// <see cref="ICppLookup"/> over <see cref="IBlueBillywigClient"/> with the plugin's five-minute cache per entity id
/// (clips and channels keyed apart, as in <c>bb_clip_cpp_</c> and <c>bb_clip_channel_cpp_</c>). Only definite answers are
/// cached; errors, a missing connection and 404s never are, so an outage cannot pin the gate shut after it ends and a
/// transient answer cannot pin it open.
/// <para>
/// The cache is dropped as a whole whenever <see cref="ISapiCacheSignal.Cleared"/> is raised: a settings save (another
/// publication or key means other answers), the cleanup action and <see cref="IBlueBillywigClient.ClearCache"/>, like the
/// plugin's <c>clearCache()</c> wiping <c>bb_clip_cpp_*</c> and <c>bb_clip_channel_cpp_*</c>. An answer fetched while a clear
/// happens is not stored. Multi-node: <see cref="Invalidate"/> of a clip is broadcast through the same signal.
/// </para>
/// </summary>
public sealed class SapiCppLookup : ICppLookup
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private const int MaxEntries = 5000;

    private readonly IBlueBillywigClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<GateEntity, (CppStatus Status, DateTimeOffset Expires)> _cache = new();

    private readonly ISapiCacheSignal? _signal;

    private int _generation;

    public SapiCppLookup(IBlueBillywigClient client, TimeProvider? time = null, ILogger? logger = null, ISapiCacheSignal? signal = null)
    {
        _client = client;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        if (signal is not null)
        {
            signal.Cleared += Clear;
            signal.ClipInvalidated += clipId => _cache.TryRemove(GateEntity.Clip(clipId), out _);
        }
        _signal = signal;
    }

    /// <summary>Drops every cached answer. Raised through <see cref="ISapiCacheSignal"/>.</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
    }

    public async Task<CppStatus> GetStatusAsync(GateEntity entity, CancellationToken ct = default)
    {
        if (entity.Id <= 0)
        {
            return CppStatus.NotFound;
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (_cache.TryGetValue(entity, out var hit) && hit.Expires > now)
        {
            return hit.Status;
        }

        if (!_client.IsConnected)
        {
            _logger.LogError("Blue Billywig ContentGate: SAPI not connected while gating {Kind}={Id}; rendering the gate message.", entity.Kind, entity.Id);
            return CppStatus.Disconnected;
        }

        // A clear that happens while SAPI is being asked must not let the old answer in afterwards.
        int generation = Volatile.Read(ref _generation);

        // Not through the client's own cache: this cache must be droppable per entity.
        SapiResult result = await _client.GetAsync(entity.SapiPath, ct: ct);
        if (!result.Ok)
        {
            if (result.StatusCode == 404)
            {
                _logger.LogError("Blue Billywig ContentGate: SAPI returned 404 for {Kind}={Id}; rendering the gate message.", entity.Kind, entity.Id);
                return CppStatus.NotFound;
            }

            _logger.LogError("Blue Billywig ContentGate: SAPI error ({Status}) fetching {Kind}={Id}; rendering the gate message.", result.StatusCode, entity.Kind, entity.Id);
            return CppStatus.Error;
        }

        if (result.Json is not JsonObject json)
        {
            _logger.LogError("Blue Billywig ContentGate: SAPI returned an unreadable body for {Kind}={Id}; rendering the gate message.", entity.Kind, entity.Id);
            return CppStatus.Error;
        }

        if (SapiJson.PhpEmpty(json["id"]))
        {
            _logger.LogError("Blue Billywig ContentGate: SAPI returned an empty {Kind}={Id} (likely a 404); rendering the gate message.", entity.Kind, entity.Id);
            return CppStatus.NotFound;
        }

        CppStatus status = SapiJson.PhpEmpty(json["cpp"]) ? CppStatus.NoCpp : CppStatus.HasCpp;
        Store(entity, status, now);
        if (generation != Volatile.Read(ref _generation))
        {
            _cache.TryRemove(entity, out _); // a clear slipped in while storing
        }

        return status;
    }

    /// <summary>
    /// Drops this node's answer at once, and for a clip tells the other nodes (the signal carries the clip id only), so a clip
    /// that was just protected is not served ungated from another node's cache.
    /// </summary>
    public void Invalidate(GateEntity entity)
    {
        _cache.TryRemove(entity, out _);
        if (_signal is null || entity.Kind != GateEntityKind.MediaClip)
        {
            return;
        }

        try
        {
            _signal.InvalidateClip(entity.Id);
        }
        catch (Exception ex)
        {
            // The edit has been saved; the other nodes then catch up when their cached answer expires.
            _logger.LogError(ex, "Blue Billywig ContentGate: could not tell the other nodes that clip {Id} changed its protection.", entity.Id);
        }
    }

    private void Store(GateEntity entity, CppStatus status, DateTimeOffset now)
    {
        if (_cache.Count >= MaxEntries)
        {
            foreach (var pair in _cache.Where(p => p.Value.Expires <= now).ToList())
            {
                _cache.TryRemove(pair.Key, out _);
            }

            if (_cache.Count >= MaxEntries)
            {
                _cache.Clear();
            }
        }

        _cache[entity] = (status, now + CacheFor);
    }
}

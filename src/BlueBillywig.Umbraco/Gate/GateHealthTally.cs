namespace BlueBillywig.Umbraco.Gate;

/// <summary>What the admin banner shows: error counts per reason from the last hour, and when the pepper last failed to save.</summary>
public sealed record GateHealthSnapshot(IReadOnlyDictionary<string, int> ErrorCounts, DateTimeOffset? LastErrorAt, DateTimeOffset? PepperFailedAt)
{
    public bool IsHealthy => ErrorCounts.Count == 0 && PepperFailedAt is null;
}

/// <summary>
/// Thread-safe in-memory <see cref="IGateHealth"/> with the plugin's one-hour rolling window: the tally lives for an
/// hour after the latest error (the plugin refreshes its transient on every record), so a transient blip clears itself
/// and a continuing failure keeps the banner up. Per-process: seam for the wiring, which may swap in a store shared by
/// every server of a load-balanced site if the banner should reflect all of them.
/// </summary>
public sealed class GateHealthTally : IGateHealth
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private DateTimeOffset? _last;
    private DateTimeOffset? _pepperFailed;

    public GateHealthTally(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public void RecordGateError(string reason)
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            ExpireLocked(now);
            _counts[reason] = _counts.GetValueOrDefault(reason) + 1;
            _last = now;
        }
    }

    public void RecordPepperPersistFailure()
    {
        lock (_gate)
        {
            _pepperFailed = _time.GetUtcNow();
        }
    }

    public GateHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            ExpireLocked(_time.GetUtcNow());
            return new GateHealthSnapshot(new Dictionary<string, int>(_counts, StringComparer.Ordinal), _last, _pepperFailed);
        }
    }

    private void ExpireLocked(DateTimeOffset now)
    {
        if (_last is { } last && now - last >= Window)
        {
            _counts = new Dictionary<string, int>(StringComparer.Ordinal);
            _last = null;
        }

        if (_pepperFailed is { } failed && now - failed >= Window)
        {
            _pepperFailed = null;
        }
    }
}

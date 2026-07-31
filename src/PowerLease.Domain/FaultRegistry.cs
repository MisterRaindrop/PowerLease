namespace PowerLease.Domain;

/// <summary>
/// Tracks failures that make the product's own judgement untrustworthy.
/// <para>
/// A latched fault is itself a reason to stay awake. If configuration could not be read, or a
/// migration failed, or the system refused a power request, then the evidence used to decide it is
/// safe to release protection cannot be trusted either, and the safe response is to keep holding.
/// </para>
/// </summary>
public sealed class FaultRegistry
{
    private readonly Dictionary<string, Entry> _faults = new(StringComparer.Ordinal);
    private readonly int _consecutiveHealthyToClearTransient;

    /// <param name="consecutiveHealthyToClearTransient">
    /// How many consecutive healthy observations clear a transient fault. Must be at least one.
    /// </param>
    public FaultRegistry(int consecutiveHealthyToClearTransient)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveHealthyToClearTransient, 1);
        _consecutiveHealthyToClearTransient = consecutiveHealthyToClearTransient;
    }

    public bool HasActiveFaults => _faults.Count > 0;

    /// <summary>Active faults, ordered by key so status output is stable.</summary>
    public IReadOnlyList<Fault> Active
    {
        get
        {
            var faults = new List<Fault>(_faults.Count);
            foreach (var (key, entry) in _faults)
            {
                faults.Add(new Fault(key, entry.Severity, entry.Message, entry.FirstSeenUtc, entry.LastSeenUtc));
            }

            faults.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
            return faults;
        }
    }

    /// <summary>
    /// Record a failure against <paramref name="key" />.
    /// <para>
    /// Severity only ever rises. A source that reports a transient failure and later a persistent
    /// one stays persistent, because the persistent diagnosis is the one that says the damage will
    /// not heal on its own.
    /// </para>
    /// </summary>
    public void Report(string key, FaultSeverity severity, string message, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(message);

        if (_faults.TryGetValue(key, out var existing))
        {
            existing.Severity = severity == FaultSeverity.Persistent ? FaultSeverity.Persistent : existing.Severity;
            existing.Message = message;
            existing.LastSeenUtc = nowUtc;
            existing.ConsecutiveHealthy = 0;
            return;
        }

        _faults[key] = new Entry
        {
            Severity = severity,
            Message = message,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
            ConsecutiveHealthy = 0
        };
    }

    /// <summary>
    /// Record one healthy observation of <paramref name="key" />. Clears a transient fault once
    /// enough consecutive healthy observations have accumulated; never clears a persistent one.
    /// </summary>
    /// <returns>True when this observation cleared the fault.</returns>
    public bool ReportHealthy(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!_faults.TryGetValue(key, out var existing) || existing.Severity == FaultSeverity.Persistent)
        {
            return false;
        }

        existing.ConsecutiveHealthy++;
        if (existing.ConsecutiveHealthy < _consecutiveHealthyToClearTransient)
        {
            return false;
        }

        _faults.Remove(key);
        return true;
    }

    /// <summary>
    /// Explicitly clear a fault, whatever its severity. This is the only way out for a persistent
    /// fault and represents something having actually been repaired.
    /// </summary>
    /// <returns>True when a fault was present and has been cleared.</returns>
    public bool Clear(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _faults.Remove(key);
    }

    /// <summary>
    /// Every active fault as a reason to stay awake, for the aggregator.
    /// </summary>
    public IReadOnlyList<Inhibitor> ToInhibitors()
    {
        var inhibitors = new List<Inhibitor>(_faults.Count);
        foreach (var fault in Active)
        {
            inhibitors.Add(new Inhibitor(InhibitorKind.Fault, fault.Message, fault.FirstSeenUtc, fault.Key));
        }

        return inhibitors;
    }

    private sealed class Entry
    {
        public FaultSeverity Severity { get; set; }

        public string Message { get; set; } = string.Empty;

        public DateTimeOffset FirstSeenUtc { get; set; }

        public DateTimeOffset LastSeenUtc { get; set; }

        public int ConsecutiveHealthy { get; set; }
    }
}

namespace PowerLease.Domain;

/// <summary>
/// The sources that must be reporting for a release decision to mean anything.
/// <para>
/// A source that has stopped reporting is the dangerous case: its last known state was probably
/// "nothing happening", and reusing that would release protection based on evidence that has since
/// expired. So a missing or stale heartbeat produces an inhibitor unconditionally, and the machine
/// stays awake until the source is back.
/// </para>
/// </summary>
public sealed class ExpectedProducerSet
{
    private readonly Dictionary<string, MonotonicStamp?> _lastHeartbeat = new(StringComparer.Ordinal);
    private readonly TimeSpan _maxHeartbeatAge;

    /// <param name="expectedSourceIds">Sources that must report. Duplicates are collapsed.</param>
    /// <param name="maxHeartbeatAge">How old a heartbeat may be before its source counts as gone.</param>
    public ExpectedProducerSet(IEnumerable<string> expectedSourceIds, TimeSpan maxHeartbeatAge)
    {
        ArgumentNullException.ThrowIfNull(expectedSourceIds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxHeartbeatAge, TimeSpan.Zero);

        foreach (var sourceId in expectedSourceIds)
        {
            ArgumentException.ThrowIfNullOrEmpty(sourceId, nameof(expectedSourceIds));
            _lastHeartbeat[sourceId] = null;
        }

        _maxHeartbeatAge = maxHeartbeatAge;
    }

    /// <summary>
    /// Heartbeats received for a source that is not expected. Not an error, because a producer
    /// registered under the wrong identifier must not be able to bring down the kernel; the expected
    /// source simply stays unhealthy and the machine keeps holding. Counted so
    /// <c>powerlease status</c> can show the mistake instead of leaving it as an unexplained hold.
    /// </summary>
    public int UnexpectedHeartbeatCount { get; private set; }

    public IReadOnlyCollection<string> ExpectedSourceIds => _lastHeartbeat.Keys;

    public void Heartbeat(string sourceId, MonotonicStamp at)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);

        if (!_lastHeartbeat.ContainsKey(sourceId))
        {
            UnexpectedHeartbeatCount++;
            return;
        }

        _lastHeartbeat[sourceId] = at;
    }

    /// <summary>
    /// Sources that are not currently trustworthy, ordered by identifier.
    /// </summary>
    public IReadOnlyList<string> UnhealthySources(MonotonicStamp now)
    {
        var unhealthy = new List<string>();
        foreach (var (sourceId, heartbeat) in _lastHeartbeat)
        {
            if (!IsHealthy(heartbeat, now))
            {
                unhealthy.Add(sourceId);
            }
        }

        unhealthy.Sort(StringComparer.Ordinal);
        return unhealthy;
    }

    public bool AllHealthy(MonotonicStamp now) => UnhealthySources(now).Count == 0;

    /// <summary>
    /// Every unhealthy source as a reason to stay awake, for the aggregator.
    /// </summary>
    public IReadOnlyList<Inhibitor> ToInhibitors(MonotonicStamp now, DateTimeOffset nowUtc)
    {
        var inhibitors = new List<Inhibitor>();
        foreach (var sourceId in UnhealthySources(now))
        {
            inhibitors.Add(new Inhibitor(
                InhibitorKind.ProducerUnhealthy,
                $"Source '{sourceId}' has not reported recently enough to be trusted.",
                nowUtc,
                sourceId));
        }

        return inhibitors;
    }

    private bool IsHealthy(MonotonicStamp? heartbeat, MonotonicStamp now)
    {
        if (heartbeat is not { } last)
        {
            // Never reported. Startup is exactly when protection matters most, so an unknown source
            // counts as gone rather than as fine.
            return false;
        }

        if (last.EpochId != now.EpochId)
        {
            // Recorded against a different monotonic origin, so its age cannot be computed. A
            // heartbeat from before the last resume says nothing about now.
            return false;
        }

        var age = now.Elapsed - last.Elapsed;

        // A negative age means the monotonic clock went backwards within one epoch, which it must
        // never do. Something is wrong enough that the reading cannot be trusted.
        return age >= TimeSpan.Zero && age <= _maxHeartbeatAge;
    }
}

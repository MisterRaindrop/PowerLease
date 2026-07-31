namespace PowerLease.Domain;

/// <summary>
/// The most recent measurements within a fixed span of monotonic time.
/// <para>
/// Samples are only kept while they remain comparable. A sample from a different monotonic clock
/// epoch, or one that arrives out of order, discards the whole window: elapsed values from two
/// epochs are measured from different origins, and mixing them would let a duration be computed
/// that looks plausible and is wrong. Losing history is the safe outcome, because a caller with too
/// little history concludes it cannot tell and keeps the machine awake.
/// </para>
/// </summary>
public sealed class SlidingWindow<T>
{
    private readonly List<TimedSample<T>> _samples = [];
    private Guid _epochId;

    public SlidingWindow(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        Window = window;
    }

    /// <summary>How far back samples are retained.</summary>
    public TimeSpan Window { get; }

    public int Count => _samples.Count;

    /// <summary>
    /// Retained samples, oldest first. This is the live collection rather than a copy, so it
    /// reflects later calls to <see cref="Add" /> and <see cref="Prune" />.
    /// </summary>
    public IReadOnlyList<TimedSample<T>> Samples => _samples;

    /// <summary>The epoch the retained samples belong to, or null when the window is empty.</summary>
    public Guid? EpochId => _samples.Count == 0 ? null : _epochId;

    /// <summary>
    /// How many times the window has been discarded because of an epoch change or an out-of-order
    /// sample. Surfaced by <c>powerlease status</c>, since a source producing discontinuities cannot
    /// establish quiet and would otherwise just look permanently inconclusive.
    /// </summary>
    public int DiscontinuityCount { get; private set; }

    public TimedSample<T>? Oldest => _samples.Count == 0 ? null : _samples[0];

    public TimedSample<T>? Newest => _samples.Count == 0 ? null : _samples[^1];

    /// <summary>
    /// Record a measurement, then drop anything that has fallen outside the window.
    /// </summary>
    public void Add(MonotonicStamp at, T value)
    {
        if (_samples.Count > 0 && (at.EpochId != _epochId || at.Elapsed < _samples[^1].At.Elapsed))
        {
            Discard();
        }

        _epochId = at.EpochId;
        _samples.Add(new TimedSample<T>(at, value));
        Prune(at);
    }

    /// <summary>
    /// Drop samples older than the window relative to <paramref name="now" />. Callers evaluate at
    /// their own cadence, so they must prune before reading rather than relying on the last
    /// <see cref="Add" />; otherwise a source that stopped reporting would keep looking current.
    /// </summary>
    public void Prune(MonotonicStamp now)
    {
        if (_samples.Count == 0)
        {
            return;
        }

        if (now.EpochId != _epochId)
        {
            Discard();
            return;
        }

        var cutoff = now.Elapsed - Window;
        var drop = 0;
        while (drop < _samples.Count && _samples[drop].At.Elapsed < cutoff)
        {
            drop++;
        }

        if (drop > 0)
        {
            _samples.RemoveRange(0, drop);
        }
    }

    /// <summary>Drop every sample without recording a discontinuity.</summary>
    public void Clear() => _samples.Clear();

    private void Discard()
    {
        _samples.Clear();
        DiscontinuityCount++;
    }
}

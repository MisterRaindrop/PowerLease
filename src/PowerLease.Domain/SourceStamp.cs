namespace PowerLease.Domain;

/// <summary>
/// Provenance of a single observation delivered by an inhibitor source.
/// <para>
/// The kernel uses this to decide whether an arriving observation may be accepted. Two
/// observations are only ordered relative to each other when they come from the same source,
/// the same run of that source, and the same monotonic clock epoch; otherwise their sequence
/// numbers describe different number lines and comparing them is meaningless.
/// </para>
/// </summary>
public readonly record struct SourceStamp(
    string SourceId,
    long SourceGeneration,
    long SourceSequence,
    MonotonicStamp ObservedAt,
    long ConfigGeneration)
{
    /// <summary>
    /// The monotonic clock epoch this observation was taken in. Derived from
    /// <see cref="ObservedAt" /> rather than stored separately so the two can never disagree.
    /// </summary>
    public Guid ClockEpochId => ObservedAt.EpochId;

    /// <summary>
    /// True when <paramref name="other" /> lies on the same number line as this stamp, so that
    /// their sequence numbers may be compared.
    /// </summary>
    public bool IsComparableTo(SourceStamp other) =>
        string.Equals(SourceId, other.SourceId, StringComparison.Ordinal)
        && SourceGeneration == other.SourceGeneration
        && ClockEpochId == other.ClockEpochId;

    /// <summary>
    /// True when this observation is strictly newer than <paramref name="previous" /> and the two
    /// are comparable.
    /// <para>
    /// Returns false for duplicates, for out-of-order arrivals, and for anything not comparable.
    /// A false result means "do not accept this observation as the source's current state". The
    /// consumer must then fall back to holding protection rather than reusing the older state,
    /// because a stale claim that nothing is happening is the one input that can release too early.
    /// </para>
    /// </summary>
    public bool Supersedes(SourceStamp previous) =>
        IsComparableTo(previous) && SourceSequence > previous.SourceSequence;
}

namespace PowerLease.Domain;

/// <summary>
/// A point in a continuous monotonic timing interval.
/// <para>
/// <see cref="EpochId" /> identifies a process instance and boot instance. Values of
/// <see cref="Elapsed" /> from different epochs are not comparable.
/// </para>
/// </summary>
public readonly record struct MonotonicStamp(Guid EpochId, TimeSpan Elapsed);

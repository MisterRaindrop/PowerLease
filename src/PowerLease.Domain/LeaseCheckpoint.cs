namespace PowerLease.Domain;

/// <summary>
/// A persisted snapshot of how much of a lease was left, used to restore it after the service
/// restarts or the machine reboots.
/// <para>
/// <see cref="RemainingAtCheckpoint" /> is the trustworthy field: it was measured with the
/// monotonic clock while the recording epoch was still running. <see cref="CheckpointUtc" /> is
/// kept for display and forensics and is deliberately not used to age the lease, because the wall
/// clock can be adjusted between epochs.
/// </para>
/// </summary>
public readonly record struct LeaseCheckpoint(
    Guid EpochId,
    TimeSpan RemainingAtCheckpoint,
    DateTimeOffset CheckpointUtc);

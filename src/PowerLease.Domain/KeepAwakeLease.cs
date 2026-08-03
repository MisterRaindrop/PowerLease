namespace PowerLease.Domain;

/// <summary>
/// One keep-awake task, as persisted. The fields up to <see cref="EndReason" /> mirror the
/// specified keep-awake model and database row; the rest is what is needed to restore the lease
/// after the monotonic clock epoch changes.
/// </summary>
public sealed record KeepAwakeLease
{
    public required string Id { get; init; }

    public required LeaseSource Source { get; init; }

    /// <summary>Human-readable reason, shown by <c>powerlease list</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>Windows account the lease belongs to, when it has one.</summary>
    public string? OwnerUser { get; init; }

    /// <summary>
    /// The security identifier of the account the lease belongs to, which is what decides who may renew or
    /// release it.
    /// <para>
    /// Persisted rather than derived, because the alternative -- resolving <see cref="OwnerUser" /> back to a
    /// security identifier when the lease is read after a restart -- can resolve to a different account or fail
    /// altogether, and both answers are wrong in a way nobody would notice. Null means the owner is not known,
    /// which the kernel treats as administrator-only.
    /// </para>
    /// </summary>
    public string? OwnerSid { get; init; }

    public string? RemoteIp { get; init; }

    public int? ProcessId { get; init; }

    public string? ProcessName { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// Expiry for display only. The lease's real remaining time is measured monotonically by
    /// <see cref="LeaseDeadline" />; this value is derived from the wall clock and can drift if the
    /// system time is adjusted.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public DateTimeOffset? LastRenewedAtUtc { get; init; }

    public bool AutoRenew { get; init; }

    public required LeaseStatus Status { get; init; }

    public DateTimeOffset? EndedAtUtc { get; init; }

    public string? EndReason { get; init; }

    /// <summary>The monotonic clock epoch the lease was last measured in.</summary>
    public required Guid EpochId { get; init; }

    /// <summary>
    /// The largest hold ever granted for this lease at one time.
    /// <para>
    /// Two jobs: the fallback when a checkpoint is unusable, and the ceiling a restored checkpoint is checked
    /// against. It therefore has to rise when a renewal grants more than the lease began with -- otherwise a
    /// one-hour lease renewed for three would come back from a restart with one hour, and protection the user
    /// was told they had would end two hours early.
    /// </para>
    /// </summary>
    public required TimeSpan OriginalDuration { get; init; }

    /// <summary>The duration asked for by the most recent renewal, for auditing.</summary>
    public TimeSpan? LastRenewDuration { get; init; }

    /// <summary>Monotonically measured time left at the last checkpoint.</summary>
    public TimeSpan? RemainingAtCheckpoint { get; init; }

    /// <summary>Wall-clock time of the last checkpoint, for display and forensics.</summary>
    public DateTimeOffset? CheckpointUtc { get; init; }

    /// <summary>
    /// The checkpoint to restore this lease from, or null when one was never completely written.
    /// A half-written checkpoint counts as absent, which makes
    /// <see cref="LeaseDeadline.Resume" /> grant the full original duration.
    /// </summary>
    public LeaseCheckpoint? TryGetCheckpoint() =>
        RemainingAtCheckpoint is { } remaining && CheckpointUtc is { } capturedAt
            ? new LeaseCheckpoint(EpochId, remaining, capturedAt)
            : null;
}

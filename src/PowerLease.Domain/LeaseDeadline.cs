namespace PowerLease.Domain;

/// <summary>
/// How much longer a lease keeps the machine awake, measured with the monotonic clock so that
/// adjusting the system time cannot cut a lease short or extend it.
/// </summary>
public sealed record LeaseDeadline
{
    private LeaseDeadline(MonotonicStamp grantedAt, TimeSpan remainingAtGrant)
    {
        GrantedAt = grantedAt;
        RemainingAtGrant = remainingAtGrant;
    }

    /// <summary>When the current grant was made.</summary>
    public MonotonicStamp GrantedAt { get; }

    /// <summary>How long the lease had left at <see cref="GrantedAt" />.</summary>
    public TimeSpan RemainingAtGrant { get; }

    /// <summary>The monotonic clock epoch this deadline is measured in.</summary>
    public Guid EpochId => GrantedAt.EpochId;

    /// <summary>Start a lease running for <paramref name="duration" />.</summary>
    public static LeaseDeadline Grant(MonotonicStamp now, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        return new LeaseDeadline(now, duration);
    }

    /// <summary>True when <paramref name="epochId" /> is the epoch this deadline can be read in.</summary>
    public bool IsInEpoch(Guid epochId) => EpochId == epochId;

    /// <summary>
    /// How much longer the lease holds, never negative.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="now" /> comes from a different epoch. Elapsed values from two
    /// epochs measure from different origins, so subtracting them would produce a number that looks
    /// valid and is not. Callers must re-establish the lease with <see cref="Resume" /> when the
    /// epoch changes. Failing loudly is deliberate: the kernel turns the resulting fault into an
    /// inhibitor, so the machine stays awake while the bug is visible.
    /// </exception>
    public TimeSpan RemainingAt(MonotonicStamp now)
    {
        RequireSameEpoch(now);

        var elapsed = now.Elapsed - GrantedAt.Elapsed;
        var remaining = RemainingAtGrant - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>True once the lease has run out. Equivalent to <see cref="RemainingAt" /> being zero.</summary>
    public bool HasExpiredAt(MonotonicStamp now) => RemainingAt(now) == TimeSpan.Zero;

    /// <summary>
    /// Extend the lease so it runs for at least <paramref name="duration" /> from now.
    /// <para>
    /// A renewal never reduces the time left. Renewing a three-hour lease for one hour after a
    /// minute leaves the two hours fifty-nine minutes it already had, because a renewal is a
    /// request for more protection and must not be able to take protection away.
    /// </para>
    /// </summary>
    public LeaseDeadline Renew(MonotonicStamp now, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        var remaining = RemainingAt(now);
        return new LeaseDeadline(now, duration > remaining ? duration : remaining);
    }

    /// <summary>
    /// Capture the lease's remaining time so it can survive a restart.
    /// </summary>
    public LeaseCheckpoint CheckpointAt(MonotonicStamp now, DateTimeOffset nowUtc) =>
        new(EpochId, RemainingAt(now), nowUtc);

    /// <summary>
    /// Re-establish a lease in the current epoch after a service restart or a reboot.
    /// <para>
    /// The checkpoint's remaining time is granted again in full. Wall-clock time that passed since
    /// the checkpoint is deliberately not subtracted: the wall clock is not trustworthy across
    /// epochs, and time the machine spent asleep or the service spent down is not time the user
    /// got the protection they asked for. Granting slightly too much keeps the machine awake;
    /// subtracting too much drops protection while an SSH session is still connected.
    /// </para>
    /// <para>
    /// A missing or self-contradictory checkpoint falls back to the full original duration for the
    /// same reason.
    /// </para>
    /// </summary>
    public static LeaseResumeResult Resume(
        LeaseCheckpoint? checkpoint,
        TimeSpan originalDuration,
        MonotonicStamp now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(originalDuration, TimeSpan.Zero);

        if (checkpoint is not { } saved)
        {
            return new LeaseResumeResult(
                Grant(now, originalDuration),
                LeaseResumeDecision.FullDurationNoCheckpoint);
        }

        // A lease persisted as still running must have had time left, and it cannot have had more
        // left than it was ever granted. Either means the row is not to be trusted.
        if (saved.RemainingAtCheckpoint <= TimeSpan.Zero || saved.RemainingAtCheckpoint > originalDuration)
        {
            return new LeaseResumeResult(
                Grant(now, originalDuration),
                LeaseResumeDecision.FullDurationInconsistentCheckpoint);
        }

        return new LeaseResumeResult(
            Grant(now, saved.RemainingAtCheckpoint),
            LeaseResumeDecision.RemainingFromCheckpoint);
    }

    private void RequireSameEpoch(MonotonicStamp now)
    {
        if (now.EpochId != EpochId)
        {
            throw new InvalidOperationException(
                $"Lease deadline was granted in epoch {EpochId} but was read in epoch {now.EpochId}. " +
                $"Call {nameof(Resume)} to re-establish it in the current epoch.");
        }
    }
}

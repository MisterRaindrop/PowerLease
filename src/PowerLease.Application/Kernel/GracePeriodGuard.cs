using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

/// <summary>
/// Holds the machine awake for a while, no questions asked.
/// <para>
/// Used at the two moments when nothing is known: just after the service starts, and just after the
/// machine wakes up. No source has reported yet, and whatever they last said describes a machine that
/// has since been asleep or unattended. Rather than treat every source as separately untrustworthy, the
/// kernel simply holds until enough time has passed for the picture to be real again.
/// </para>
/// </summary>
public sealed class GracePeriodGuard
{
    private MonotonicStamp? _expiresAt;
    private string _reason = string.Empty;

    public bool IsActive(MonotonicStamp now)
    {
        if (_expiresAt is not { } expiry)
        {
            return false;
        }

        // A deadline measured against a different monotonic clock cannot be compared with now. That
        // means a resume happened without the kernel being told, which is exactly when protection
        // matters, so it counts as still active until something begins a fresh period.
        return expiry.EpochId != now.EpochId || now.Elapsed < expiry.Elapsed;
    }

    /// <summary>
    /// Start a grace period, or extend the one running.
    /// <para>
    /// Never shortens what is already in place. Two reasons to hold arriving together is a reason to
    /// hold for the longer of the two, not for whichever happened to be applied last.
    /// </para>
    /// </summary>
    public void Begin(MonotonicStamp now, TimeSpan duration, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        // Saturating rather than wrapping. Adding a large duration to a large elapsed time can overflow, and an
        // exception escaping here would leave the caller having recorded no reason to stay awake at all -- on the
        // one path whose entire purpose is to record one unconditionally.
        var remaining = TimeSpan.MaxValue - now.Elapsed;
        var candidate = new MonotonicStamp(
            now.EpochId,
            duration < remaining ? now.Elapsed + duration : TimeSpan.MaxValue);

        if (_expiresAt is { } existing
            && existing.EpochId == now.EpochId
            && existing.Elapsed >= candidate.Elapsed)
        {
            return;
        }

        _expiresAt = candidate;
        _reason = reason;
    }

    /// <summary>The reason to stay awake while a grace period is running, or null when none is.</summary>
    public Inhibitor? ToInhibitor(MonotonicStamp now, DateTimeOffset nowUtc) =>
        IsActive(now) ? new Inhibitor(InhibitorKind.GracePeriod, _reason, nowUtc) : null;
}

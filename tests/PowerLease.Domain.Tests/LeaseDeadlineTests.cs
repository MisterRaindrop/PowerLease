using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class LeaseDeadlineTests
{
    private static readonly TimeSpan ThreeHours = TimeSpan.FromHours(3);

    [Fact]
    public void Remaining_time_counts_down_with_the_monotonic_clock()
    {
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0), ThreeHours);

        Assert.Equal(ThreeHours, lease.RemainingAt(Stamps.Minutes(0)));
        Assert.Equal(TimeSpan.FromHours(2), lease.RemainingAt(Stamps.Minutes(60)));
    }

    [Fact]
    public void Remaining_time_stops_at_zero_and_the_lease_reads_as_expired()
    {
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0), TimeSpan.FromMinutes(10));

        Assert.False(lease.HasExpiredAt(Stamps.Minutes(9)));
        Assert.True(lease.HasExpiredAt(Stamps.Minutes(10)));
        Assert.Equal(TimeSpan.Zero, lease.RemainingAt(Stamps.Minutes(99)));
    }

    [Fact]
    public void Grant_requires_a_positive_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LeaseDeadline.Grant(Stamps.Minutes(0), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LeaseDeadline.Grant(Stamps.Minutes(0), TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void A_short_renewal_does_not_cut_a_long_lease_short()
    {
        // The invariant that matters most here. A renewal is a request for more protection, so a
        // one-hour renewal of a three-hour lease must not throw away the two hours fifty-nine
        // minutes it already had.
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0), ThreeHours);

        var renewed = lease.Renew(Stamps.Minutes(1), TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromMinutes(179), renewed.RemainingAt(Stamps.Minutes(1)));
    }

    [Fact]
    public void A_longer_renewal_extends_the_lease()
    {
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0), TimeSpan.FromMinutes(10));

        var renewed = lease.Renew(Stamps.Minutes(5), TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromHours(1), renewed.RemainingAt(Stamps.Minutes(5)));
    }

    [Fact]
    public void Renewing_an_expired_lease_grants_the_requested_duration()
    {
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0), TimeSpan.FromMinutes(10));

        var renewed = lease.Renew(Stamps.Minutes(30), TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(15), renewed.RemainingAt(Stamps.Minutes(30)));
    }

    [Fact]
    public void Renew_requires_a_positive_duration()
    {
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0), ThreeHours);

        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Renew(Stamps.Minutes(1), TimeSpan.Zero));
    }

    [Fact]
    public void Reading_a_lease_granted_in_another_epoch_throws_instead_of_returning_a_wrong_number()
    {
        // Elapsed values from two epochs count from different origins. Subtracting them would produce
        // a plausible-looking number, so this fails loudly and the kernel turns the fault into an
        // inhibitor rather than acting on it.
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0, Stamps.EpochA), ThreeHours);

        Assert.True(lease.IsInEpoch(Stamps.EpochA));
        Assert.False(lease.IsInEpoch(Stamps.EpochB));

        var error = Assert.Throws<InvalidOperationException>(
            () => lease.RemainingAt(Stamps.Minutes(1, Stamps.EpochB)));
        Assert.Contains(nameof(LeaseDeadline.Resume), error.Message);
    }

    [Fact]
    public void Checkpointing_records_the_monotonically_measured_remaining_time()
    {
        var lease = LeaseDeadline.Grant(Stamps.Minutes(0), ThreeHours);
        var nowUtc = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        var checkpoint = lease.CheckpointAt(Stamps.Minutes(60), nowUtc);

        Assert.Equal(Stamps.EpochA, checkpoint.EpochId);
        Assert.Equal(TimeSpan.FromHours(2), checkpoint.RemainingAtCheckpoint);
        Assert.Equal(nowUtc, checkpoint.CheckpointUtc);
    }

    [Fact]
    public void Resuming_from_a_checkpoint_grants_the_saved_remaining_time_in_the_new_epoch()
    {
        var checkpoint = new LeaseCheckpoint(
            Stamps.EpochA,
            TimeSpan.FromHours(2),
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));

        var resumed = LeaseDeadline.Resume(checkpoint, ThreeHours, Stamps.Minutes(0, Stamps.EpochB));

        Assert.Equal(LeaseResumeDecision.RemainingFromCheckpoint, resumed.Decision);
        Assert.Equal(Stamps.EpochB, resumed.Deadline.EpochId);
        Assert.Equal(TimeSpan.FromHours(2), resumed.Deadline.RemainingAt(Stamps.Minutes(0, Stamps.EpochB)));
    }

    [Fact]
    public void Resuming_does_not_age_the_lease_by_the_wall_clock()
    {
        // A year of wall-clock time passed, and the lease still resumes with its full saved
        // remaining time. The wall clock is not trustworthy across epochs, and time the machine spent
        // asleep or the service spent down is not time the user got the protection they asked for.
        var checkpoint = new LeaseCheckpoint(
            Stamps.EpochA,
            TimeSpan.FromHours(2),
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var resumed = LeaseDeadline.Resume(checkpoint, ThreeHours, Stamps.Minutes(0, Stamps.EpochB));

        Assert.Equal(LeaseResumeDecision.RemainingFromCheckpoint, resumed.Decision);
        Assert.Equal(TimeSpan.FromHours(2), resumed.Deadline.RemainingAt(Stamps.Minutes(0, Stamps.EpochB)));
    }

    [Fact]
    public void Resuming_without_a_checkpoint_grants_the_full_original_duration()
    {
        var resumed = LeaseDeadline.Resume(checkpoint: null, ThreeHours, Stamps.Minutes(0, Stamps.EpochB));

        Assert.Equal(LeaseResumeDecision.FullDurationNoCheckpoint, resumed.Decision);
        Assert.Equal(ThreeHours, resumed.Deadline.RemainingAt(Stamps.Minutes(0, Stamps.EpochB)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(181)]
    public void A_self_contradictory_checkpoint_falls_back_to_the_full_duration(int remainingMinutes)
    {
        // Zero or negative: a lease persisted as still running had time left. More than the original
        // grant: the row cannot be right. Either way, err long rather than drop protection.
        var checkpoint = new LeaseCheckpoint(
            Stamps.EpochA,
            TimeSpan.FromMinutes(remainingMinutes),
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));

        var resumed = LeaseDeadline.Resume(checkpoint, ThreeHours, Stamps.Minutes(0, Stamps.EpochB));

        Assert.Equal(LeaseResumeDecision.FullDurationInconsistentCheckpoint, resumed.Decision);
        Assert.Equal(ThreeHours, resumed.Deadline.RemainingAt(Stamps.Minutes(0, Stamps.EpochB)));
    }

    [Fact]
    public void A_checkpoint_holding_exactly_the_original_duration_is_accepted()
    {
        var checkpoint = new LeaseCheckpoint(
            Stamps.EpochA,
            ThreeHours,
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));

        var resumed = LeaseDeadline.Resume(checkpoint, ThreeHours, Stamps.Minutes(0, Stamps.EpochB));

        Assert.Equal(LeaseResumeDecision.RemainingFromCheckpoint, resumed.Decision);
    }

    [Fact]
    public void Resume_requires_a_positive_original_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LeaseDeadline.Resume(checkpoint: null, TimeSpan.Zero, Stamps.Minutes(0)));
    }
}

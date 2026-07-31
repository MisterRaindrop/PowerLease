using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class KeepAwakeLeaseTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static KeepAwakeLease Lease(TimeSpan? remainingAtCheckpoint, DateTimeOffset? checkpointUtc) =>
        new()
        {
            Id = "lease-1",
            Source = LeaseSource.SshSession,
            Reason = "SSH session from 10.0.0.5",
            OwnerUser = "liu",
            RemoteIp = "10.0.0.5",
            ProcessId = 4242,
            ProcessName = "sshd",
            StartedAtUtc = Origin,
            ExpiresAtUtc = Origin.AddHours(3),
            AutoRenew = true,
            Status = LeaseStatus.Active,
            EpochId = Stamps.EpochA,
            OriginalDuration = TimeSpan.FromHours(3),
            LastRenewDuration = TimeSpan.FromHours(1),
            RemainingAtCheckpoint = remainingAtCheckpoint,
            CheckpointUtc = checkpointUtc
        };

    [Fact]
    public void A_complete_checkpoint_is_returned_against_the_lease_epoch()
    {
        var lease = Lease(TimeSpan.FromHours(2), Origin.AddHours(1));

        var checkpoint = lease.TryGetCheckpoint();

        Assert.NotNull(checkpoint);
        Assert.Equal(Stamps.EpochA, checkpoint.Value.EpochId);
        Assert.Equal(TimeSpan.FromHours(2), checkpoint.Value.RemainingAtCheckpoint);
        Assert.Equal(Origin.AddHours(1), checkpoint.Value.CheckpointUtc);
    }

    [Fact]
    public void A_lease_that_was_never_checkpointed_has_no_checkpoint()
    {
        Assert.Null(Lease(remainingAtCheckpoint: null, checkpointUtc: null).TryGetCheckpoint());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_half_written_checkpoint_counts_as_absent(bool hasRemaining, bool hasTimestamp)
    {
        // Treating a partial row as usable would mean guessing the missing half, and the guess decides
        // how long the machine stays awake.
        var lease = Lease(
            hasRemaining ? TimeSpan.FromHours(2) : null,
            hasTimestamp ? Origin : null);

        Assert.Null(lease.TryGetCheckpoint());
    }

    [Fact]
    public void A_finished_lease_records_how_and_when_it_ended()
    {
        // The fields the history and audit trail are built from. Persistence has to round-trip all of
        // them, so they are pinned here rather than discovered to be missing later.
        var lease = new KeepAwakeLease
        {
            Id = "lease-2",
            Source = LeaseSource.Cli,
            StartedAtUtc = Origin,
            LastRenewedAtUtc = Origin.AddMinutes(30),
            AutoRenew = false,
            Status = LeaseStatus.Released,
            EndedAtUtc = Origin.AddMinutes(45),
            EndReason = "released by liu",
            EpochId = Stamps.EpochA,
            OriginalDuration = TimeSpan.FromHours(1)
        };

        Assert.Equal(LeaseStatus.Released, lease.Status);
        Assert.Equal(Origin.AddMinutes(30), lease.LastRenewedAtUtc);
        Assert.Equal(Origin.AddMinutes(45), lease.EndedAtUtc);
        Assert.Equal("released by liu", lease.EndReason);
        Assert.Null(lease.TryGetCheckpoint());
    }

    [Fact]
    public void A_lease_with_no_usable_checkpoint_resumes_with_its_full_duration()
    {
        var lease = Lease(remainingAtCheckpoint: null, checkpointUtc: Origin);

        var resumed = LeaseDeadline.Resume(
            lease.TryGetCheckpoint(),
            lease.OriginalDuration,
            Stamps.Minutes(0, Stamps.EpochB));

        Assert.Equal(LeaseResumeDecision.FullDurationNoCheckpoint, resumed.Decision);
        Assert.Equal(TimeSpan.FromHours(3), resumed.Deadline.RemainingAt(Stamps.Minutes(0, Stamps.EpochB)));
    }
}

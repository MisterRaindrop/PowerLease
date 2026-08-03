using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// What survives the process ending.
/// <para>
/// Every other lease test drives one kernel from start to finish, which is the one shape that cannot show
/// whether a lease outlives the process that granted it. It did not: the rows were written, the epoch and
/// checkpoint columns were stored, and nothing ever read them back, so restarting the service released a
/// hold that still had hours left on it. These tests are about that boundary and nothing else.
/// </para>
/// </summary>
public sealed class KernelRestartTests
{
    private static readonly CallerSnapshot Liu = new()
    {
        Sid = "S-1-5-21-1",
        AccountName = "liu",
        IsAdministrator = false
    };

    private static readonly CallerSnapshot Someone = new()
    {
        Sid = "S-1-5-21-2",
        AccountName = "someone",
        IsAdministrator = false
    };

    private static readonly CallerSnapshot Administrator = new()
    {
        Sid = "S-1-5-21-500",
        AccountName = "admin",
        IsAdministrator = true
    };

    /// <summary>A lease as storage would hand it back: active, with a checkpoint from the previous epoch.</summary>
    /// <param name="withCheckpoint">
    /// False for a row that says nothing trustworthy about how much is left. A nullable
    /// <paramref name="remaining" /> cannot express this: its own default would swallow the null.
    /// </param>
    private static KeepAwakeLease Stored(
        string id = "lease-1",
        TimeSpan? original = null,
        TimeSpan? remaining = null,
        LeaseStatus status = LeaseStatus.Active,
        string? ownerSid = "S-1-5-21-1",
        bool withCheckpoint = true) => new()
        {
            Id = id,
            Source = LeaseSource.Cli,
            Reason = "a long build",
            OwnerUser = "liu",
            OwnerSid = ownerSid,
            StartedAtUtc = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero),
            Status = status,
            EpochId = Guid.Parse("cccccccc-3333-3333-3333-333333333333"),
            OriginalDuration = original ?? TimeSpan.FromHours(3),
            RemainingAtCheckpoint = withCheckpoint ? remaining ?? TimeSpan.FromHours(2) : null,
            CheckpointUtc = withCheckpoint ? new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero) : null
        };

    [Fact]
    public void A_lease_from_before_the_restart_still_holds_the_machine_awake()
    {
        // The whole point. Without restoration the first evaluation after a restart sees no leases, every
        // source confirms it has nothing, and the machine is released while somebody is still using it.
        var harness = new KernelHarness();
        harness.Kernel.Restore([Stored()]);
        harness.ConfirmAbsent("ssh");

        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Contains(result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
    }

    [Fact]
    public void Nothing_restored_means_nothing_held()
    {
        // The other half of the previous test: it must be the restored lease doing the holding, not the
        // kernel being unable to release in the first place.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");

        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_restored_lease_is_granted_the_time_its_checkpoint_said_was_left()
    {
        // Two hours left of a three-hour hold, measured from now. Not three, and not two minus however long
        // the service was down -- time the service spent down is not time the user got what they asked for.
        var harness = new KernelHarness();
        harness.Kernel.Restore([Stored(original: TimeSpan.FromHours(3), remaining: TimeSpan.FromHours(2))]);
        harness.ConfirmAbsent("ssh");
        harness.Step();

        harness.Clock.Advance(TimeSpan.FromMinutes(119));
        harness.ConfirmAbsent("ssh");
        Assert.True(harness.Step().Snapshot.ShouldHold);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_restored_lease_with_no_usable_checkpoint_is_granted_its_full_duration()
    {
        // Fail safe: a row that cannot be trusted about how much is left gets the whole lease again rather
        // than nothing. Holding too long wastes electricity; the other way releases a machine in use.
        var harness = new KernelHarness();
        harness.Kernel.Restore([Stored(original: TimeSpan.FromHours(3), withCheckpoint: false)]);
        harness.ConfirmAbsent("ssh");
        harness.Step();

        harness.Clock.Advance(TimeSpan.FromMinutes(179));
        harness.ConfirmAbsent("ssh");
        Assert.True(harness.Step().Snapshot.ShouldHold);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_restored_lease_keeps_who_it_belongs_to()
    {
        // The reason the owner is stored rather than re-derived. If restoring reset it, the person who
        // created the hold could no longer end it, and someone else might be able to.
        var harness = new KernelHarness();
        harness.Kernel.Restore([Stored(ownerSid: Liu.Sid)]);
        harness.ConfirmAbsent("ssh");
        harness.Step();

        Assert.Equal(LeaseCommandStatus.Rejected, Release(harness, Someone, "req-stranger").Status);

        harness.Kernel.Execute(Releasing(harness, Liu, "req-owner"));
        var effect = Assert.Single(harness.Step().Effects, e => e.Kind == EffectKind.PersistLeaseRelease);
        Assert.Equal("lease-1", effect.Lease!.Id);
    }

    [Fact]
    public void A_restored_lease_with_no_recorded_owner_may_only_be_ended_by_an_administrator()
    {
        // Reachable for a lease written before the owner was stored. Guessing the other way would hand a
        // stranger's hold to whoever asked first.
        var harness = new KernelHarness();
        harness.Kernel.Restore([Stored(ownerSid: null)]);
        harness.ConfirmAbsent("ssh");
        harness.Step();

        Assert.Equal(LeaseCommandStatus.Rejected, Release(harness, Liu, "req-1").Status);

        harness.Kernel.Execute(Releasing(harness, Administrator, "req-2"));
        Assert.Single(harness.Step().Effects, e => e.Kind == EffectKind.PersistLeaseRelease);
    }

    [Fact]
    public void A_lease_that_had_already_ended_is_not_brought_back()
    {
        var harness = new KernelHarness();
        var taken = harness.Kernel.Restore(
        [
            Stored("expired", status: LeaseStatus.Expired),
            Stored("released", status: LeaseStatus.Released),
            Stored("active")
        ]);

        Assert.Equal(["active"], taken);
    }

    [Fact]
    public void A_restored_lease_is_already_durable_and_is_not_written_again_as_if_it_were_new()
    {
        // A restored lease exists in the database. Treating it as provisional would mean the first write
        // failure deleted a lease that is really there, and its holder would lose protection without asking.
        var harness = new KernelHarness();
        harness.Kernel.Restore([Stored()]);
        harness.ConfirmAbsent("ssh");

        var effect = Assert.Single(harness.Step().Effects, e => e.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(effect.EffectId, EffectOutcome.Failed, Error: "the disk is full")));

        harness.ConfirmAbsent("ssh");
        var after = harness.Step();

        // Still held. The write that failed was only the checkpoint.
        Assert.True(after.Snapshot.ShouldHold);
        Assert.Contains(after.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
    }

    [Fact]
    public void Restoring_after_the_kernel_has_evaluated_is_refused()
    {
        // A lease appearing out of storage mid-decision would mean at least one published snapshot had said
        // the machine had no reason to stay awake when it did.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Step();

        Assert.Throws<InvalidOperationException>(() => harness.Kernel.Restore([Stored()]));
    }

    [Fact]
    public void The_remaining_time_of_a_running_lease_is_written_down_as_it_runs()
    {
        // What makes a restart honest. The checkpoint is the only evidence of how much of a lease is left,
        // so if it were written only at creation, every restart would hand back the original duration.
        var harness = new KernelHarness(Options(TimeSpan.FromMinutes(2)));
        harness.Kernel.Restore([Stored(original: TimeSpan.FromHours(3), remaining: TimeSpan.FromHours(2))]);
        harness.ConfirmAbsent("ssh");
        harness.Step();

        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        harness.ConfirmAbsent("ssh");
        var effect = Assert.Single(harness.Step().Effects, e => e.Kind == EffectKind.PersistLease);

        Assert.Equal(TimeSpan.FromMinutes(90), effect.Lease!.RemainingAtCheckpoint);

        // Nobody is waiting on it: it is the kernel keeping its own record current, not a caller's request.
        Assert.Null(effect.RequestId);
    }

    [Fact]
    public void The_remaining_time_is_not_rewritten_more_often_than_asked()
    {
        var harness = new KernelHarness(Options(TimeSpan.FromMinutes(2)));
        harness.Kernel.Restore([Stored()]);
        harness.ConfirmAbsent("ssh");
        harness.Step();

        harness.Clock.Advance(TimeSpan.FromSeconds(90));
        harness.ConfirmAbsent("ssh");
        Assert.DoesNotContain(harness.Step().Effects, e => e.Kind == EffectKind.PersistLease);

        harness.Clock.Advance(TimeSpan.FromSeconds(40));
        harness.ConfirmAbsent("ssh");
        Assert.Contains(harness.Step().Effects, e => e.Kind == EffectKind.PersistLease);
    }

    [Fact]
    public void A_lease_restored_twice_from_a_refreshed_checkpoint_finishes_instead_of_running_for_ever()
    {
        // The failure this exists to prevent: a machine that restarts often enough would re-grant the same
        // stale checkpoint every time, and a three-hour hold would never end.
        var first = new KernelHarness(Options(TimeSpan.FromMinutes(2)));
        first.Kernel.Restore([Stored(original: TimeSpan.FromHours(3), remaining: TimeSpan.FromHours(2))]);
        first.ConfirmAbsent("ssh");
        first.Step();

        first.Clock.Advance(TimeSpan.FromMinutes(110));
        first.ConfirmAbsent("ssh");
        var written = Assert.Single(first.Step().Effects, e => e.Kind == EffectKind.PersistLease).Lease!;

        // The service restarts and reads back what was last written, not what was there at the start.
        var second = new KernelHarness(Options(TimeSpan.FromMinutes(2)));
        second.Kernel.Restore([written]);
        second.ConfirmAbsent("ssh");
        Assert.True(second.Step().Snapshot.ShouldHold);

        second.Clock.Advance(TimeSpan.FromMinutes(11));
        second.ConfirmAbsent("ssh");
        Assert.False(second.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_failed_checkpoint_write_stops_holding_the_machine_awake_once_one_succeeds()
    {
        // Checkpoints are written over and over, so a fault raised by a passing disk problem has to be able
        // to clear. Latching it for the life of the process would pin the machine awake over a write whose
        // failure only ever means holding for longer.
        var harness = new KernelHarness(Options(TimeSpan.FromMinutes(2)));
        harness.Kernel.Restore([Stored()]);
        harness.ConfirmAbsent("ssh");
        harness.Step();

        harness.Clock.Advance(TimeSpan.FromMinutes(3));
        harness.ConfirmAbsent("ssh");
        var failing = Assert.Single(harness.Step().Effects, e => e.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(failing.EffectId, EffectOutcome.Failed, Error: "the disk is full")));

        harness.ConfirmAbsent("ssh");
        Assert.Contains(harness.Step().Snapshot.Faults, fault => fault.Key == "lease-persist:lease-1");

        // Enough successful writes later, the fault is gone. The lease itself is still holding, so the
        // assertion is about the fault list rather than about whether the machine is awake.
        for (var round = 0; round < harness.Options.ConsecutiveHealthyToClearTransient; round++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(3));
            harness.ConfirmAbsent("ssh");
            var effect = Assert.Single(harness.Step().Effects, e => e.Kind == EffectKind.PersistLease);
            harness.Kernel.Apply(new EffectFinished(
                new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded)));
        }

        harness.ConfirmAbsent("ssh");
        Assert.DoesNotContain(harness.Step().Snapshot.Faults, fault => fault.Key == "lease-persist:lease-1");
    }

    private static KernelOptions Options(TimeSpan checkpointInterval) => new()
    {
        ObservationFreshness = TimeSpan.FromHours(24),
        HeartbeatFreshness = TimeSpan.FromHours(24),
        StartupGracePeriod = TimeSpan.Zero,
        LeaseCheckpointInterval = checkpointInterval,
        ExpectedSources = ["ssh"],
        CoveredKinds = [InhibitorKind.SshSession, InhibitorKind.CliLease]
    };

    private static LeaseCommandResult Release(KernelHarness harness, CallerSnapshot caller, string requestId)
    {
        harness.Kernel.Execute(Releasing(harness, caller, requestId));
        return Assert.Single(harness.Step().CompletedCommands);
    }

    private static LeaseCommand Releasing(KernelHarness harness, CallerSnapshot caller, string requestId) => new()
    {
        RequestId = requestId,
        Caller = caller,
        Kind = LeaseCommandKind.Release,
        LeaseId = "lease-1",
        Deadline = new MonotonicStamp(harness.Clock.EpochId, harness.Clock.Elapsed + TimeSpan.FromSeconds(10)),
        PayloadHash = "hash-release"
    };
}

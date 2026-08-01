using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// Two ways the machine could be left awake for ever, or released with nothing to go on. Both were found
/// by asking where the other half of a stated rule actually happens.
/// </summary>
public sealed class KernelResumeAndEvidenceTests
{
    private static readonly CallerSnapshot Liu = new() { Sid = "S-1-5-21-1", AccountName = "liu" };

    private static LeaseCommand Create(KernelHarness harness, TimeSpan duration) => new()
    {
        RequestId = "req-1",
        Caller = Liu,
        Kind = LeaseCommandKind.Create,
        LeaseId = "lease-1",
        Duration = duration,
        Deadline = new MonotonicStamp(harness.Clock.EpochId, harness.Clock.Elapsed + TimeSpan.FromSeconds(10)),
        PayloadHash = "hash-a"
    };

    private static KernelHarness WithLease(TimeSpan duration)
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness, duration));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded)));
        harness.Step();
        return harness;
    }

    [Fact]
    public void A_lease_that_lived_through_a_resume_still_runs_out()
    {
        // A lease is measured on the monotonic clock, which starts again after a resume. Something has to
        // re-establish it against the new clock, or it can never be found to have expired -- and a lease that
        // can never expire holds the machine awake for as long as the service runs. That is the exact failure
        // that makes a power-management tool worse than not having one.
        var harness = WithLease(TimeSpan.FromMinutes(10));

        harness.Clock.BeginNewEpoch();
        harness.Kernel.Apply(new ResumedFromSleep("power event"));
        harness.Step();

        // Past the grace period and well past the lease's ten minutes.
        harness.Clock.Advance(harness.Options.ResumeGracePeriod + TimeSpan.FromMinutes(30));
        harness.ConfirmAbsent("ssh");
        var result = harness.Step();

        Assert.DoesNotContain(
            result.Snapshot.Decision.Inhibitors,
            inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
        Assert.False(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void A_lease_resumed_across_a_clock_change_keeps_the_time_it_had_left_and_no_more()
    {
        // Re-establishing it must not silently hand back the whole original duration every time the machine
        // wakes up: a lease repeatedly renewed by sleeping would never end.
        var harness = WithLease(TimeSpan.FromMinutes(10));

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        harness.ConfirmAbsent("ssh");
        harness.Step();

        harness.Clock.BeginNewEpoch();
        harness.Kernel.Apply(new ResumedFromSleep("power event"));
        harness.Step();

        // Four minutes were left. After five it must be gone, grace period aside.
        harness.Clock.Advance(harness.Options.ResumeGracePeriod + TimeSpan.FromMinutes(5));
        harness.ConfirmAbsent("ssh");

        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_kernel_told_what_to_expect_holds_until_it_has_heard_from_all_of_it()
    {
        // Before a source has reported for the first time it contributes nothing at all, so what stops the
        // kernel concluding "no reason to stay awake" from having heard nobody is the expected set. This is the
        // shipped configuration: producers are named, and until each has spoken the machine is held.
        var harness = new KernelHarness(new KernelOptions
        {
            ExpectedSources = ["ssh", "activity"],
            CoveredKinds = [InhibitorKind.SshSession],
            StartupGracePeriod = TimeSpan.Zero
        });

        Assert.True(harness.Step().Snapshot.ShouldHold);

        harness.ConfirmAbsent("ssh");
        Assert.True(harness.Step().Snapshot.ShouldHold);

        harness.ConfirmAbsent("activity");
        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void Expecting_nothing_is_a_deliberate_statement_that_nothing_is_being_watched()
    {
        // An empty expected set releases, and that is the right answer for a build watching nothing: Windows
        // then decides on its own, which is what it would do without PowerLease installed. The hazard was never
        // this behaviour but the silence of it -- the property is required with no default, so a host cannot
        // arrive here by forgetting to name its producers.
        var harness = new KernelHarness(new KernelOptions { ExpectedSources = [], StartupGracePeriod = TimeSpan.Zero });

        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_lease_running_out_is_recorded_so_a_restart_cannot_bring_it_back()
    {
        // Protection is already gone when a lease expires, so unlike a release there is no ordering to respect.
        // It still has to be written: a row left saying "active" with time on it would be granted that time again
        // by the restore path, and a three-hour hold that ran out weeks ago would come back for three more hours
        // on every restart.
        var harness = WithLease(TimeSpan.FromMinutes(10));

        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.ConfirmAbsent("ssh");
        var result = harness.Step();

        Assert.False(result.Snapshot.ShouldHold);
        var recorded = Assert.Single(result.Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        Assert.Equal(LeaseStatus.Expired, recorded.Lease!.Status);
        Assert.NotNull(recorded.Lease.EndedAtUtc);
        Assert.Equal(TimeSpan.Zero, recorded.Lease.RemainingAtCheckpoint);
    }

    [Fact]
    public void Switching_a_rule_off_does_not_leave_its_source_holding_the_machine_awake_for_ever()
    {
        // Disabling SSH detection is an ordinary thing to do. Without forgetting the source, its last report goes
        // stale and becomes a reason to stay awake on every cycle from then on -- so the machine would never
        // sleep again until the service restarted, and status would blame a producer that is no longer running.
        var harness = new KernelHarness(new KernelOptions
        {
            ExpectedSources = ["ssh", "activity"],
            ObservationFreshness = TimeSpan.FromSeconds(30),
            HeartbeatFreshness = TimeSpan.FromSeconds(60),
            StartupGracePeriod = TimeSpan.Zero
        });

        harness.ConfirmAbsent("ssh");
        harness.ConfirmAbsent("activity");
        Assert.False(harness.Step().Snapshot.ShouldHold);

        harness.ReplaceConfiguration(["activity"]);
        harness.ConfirmAbsent("activity");
        var result = harness.Step();

        Assert.False(result.Snapshot.ShouldHold);
        Assert.DoesNotContain(result.Snapshot.Sources, source => source.SourceId == "ssh");
        Assert.Empty(result.Snapshot.UnhealthySources);

        // And it stays gone as time passes, which is when the stale entry used to start holding.
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.ConfirmAbsent("activity");
        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void Losing_the_history_is_reported_but_does_not_hold_the_machine_awake()
    {
        // A fault is a reason to stay awake, and the test for that is whether the evidence behind a release
        // decision can be trusted. The history is the record of decisions already made, not evidence for the
        // current one. Latching it would also deadlock: the fault holds, so the protection state stops changing,
        // so no further history is written, so nothing could ever report it healthy again.
        var harness = new KernelHarness();
        harness.Observe("ssh", harness.SshInhibitor());
        var journal = Assert.Single(
            harness.Step().Effects, candidate => candidate.Kind == EffectKind.RecordInhibitChange);

        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(journal.EffectId, EffectOutcome.Failed, Error: "disk full")));

        harness.ConfirmAbsent("ssh");
        var result = harness.Step();

        // Visible, so a database that has been failing for a month does not look like one with nothing to say.
        Assert.Equal(1, result.Snapshot.HistoryWriteFailures);
        Assert.Equal("disk full", result.Snapshot.LastHistoryWriteError);

        // And the machine is free to sleep, because nothing about the decision changed.
        Assert.Empty(result.Snapshot.Faults);
        Assert.False(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void A_lease_write_failure_does_hold_and_clears_once_a_write_for_it_succeeds()
    {
        // The other side of the same rule. A lease whose state could not be written means the kernel's view and
        // the stored view disagree, and a restart would restore something different -- so this one does undermine
        // the decision and is latched. It clears through the real path: the next successful write for that lease.
        var harness = new KernelHarness(new KernelOptions
        {
            ExpectedSources = ["ssh"],
            ObservationFreshness = TimeSpan.FromSeconds(30),
            HeartbeatFreshness = TimeSpan.FromSeconds(60),
            ConsecutiveHealthyToClearTransient = 1,
            StartupGracePeriod = TimeSpan.Zero
        });

        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness, TimeSpan.FromMinutes(10)));
        var failing = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(failing.EffectId, EffectOutcome.Failed, Error: "disk full")));
        harness.ConfirmAbsent("ssh");
        var failed = harness.Step();

        Assert.Contains(failed.Snapshot.Faults, fault => fault.Key.StartsWith("lease-persist:", StringComparison.Ordinal));
        Assert.True(failed.Snapshot.ShouldHold);

        // The disk comes back and the caller retries.
        harness.Kernel.Execute(Create(harness, TimeSpan.FromMinutes(10)) with { RequestId = "req-2" });
        var retry = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(retry.EffectId, EffectOutcome.Succeeded)));

        harness.ConfirmAbsent("ssh");
        var recovered = harness.Step();

        Assert.DoesNotContain(
            recovered.Snapshot.Faults,
            fault => fault.Key.StartsWith("lease-persist:", StringComparison.Ordinal));

        // The lease itself is now holding, which is the point of having retried.
        Assert.Contains(recovered.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
    }

    [Fact]
    public void Failing_to_record_that_a_lease_ended_is_latched_but_succeeding_is_silent()
    {
        // Nobody is waiting on this write -- the kernel ended the lease itself -- so only its failure matters. It
        // matters because a row left saying "active" would be re-granted its remaining time on the next restart.
        var harness = WithLease(TimeSpan.FromMinutes(10));
        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.ConfirmAbsent("ssh");
        var ending = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(ending.EffectId, EffectOutcome.Failed, Error: "disk full")));
        harness.ConfirmAbsent("ssh");
        var failed = harness.Step();

        Assert.Contains(
            failed.Snapshot.Faults,
            fault => fault.Key.StartsWith("lease-persist:", StringComparison.Ordinal));
        Assert.True(failed.Snapshot.ShouldHold);
        Assert.Empty(failed.CompletedCommands);
    }

    [Fact]
    public void Recording_that_a_lease_ended_successfully_tells_nobody_anything()
    {
        var harness = WithLease(TimeSpan.FromMinutes(10));
        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.ConfirmAbsent("ssh");
        var ending = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(ending.EffectId, EffectOutcome.Succeeded)));
        harness.ConfirmAbsent("ssh");
        var result = harness.Step();

        Assert.Empty(result.CompletedCommands);
        Assert.Empty(result.Snapshot.Faults);
        Assert.False(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void An_effect_the_host_never_reports_back_is_eventually_forgotten()
    {
        // The host is meant to report every effect exactly once. One that dies between issuing a write and
        // reporting it would otherwise leave an entry for the lifetime of a process designed to run for months.
        // Dropping it changes nothing about protection: the lease it belonged to expires on its own deadline.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness, TimeSpan.FromHours(4)));
        var abandoned = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        for (var i = 0; i < 1200; i++)
        {
            harness.ConfirmAbsent("ssh");
            harness.Step();
        }

        // Reporting it now finds nothing waiting, which is how the drop is observable from outside.
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(abandoned.EffectId, EffectOutcome.Succeeded)));

        Assert.Empty(harness.Step().CompletedCommands);
    }

    [Fact]
    public void An_effect_that_is_never_reported_back_does_not_hold_the_machine_awake_for_ever()
    {
        // A lease is provisional until its write is confirmed. If the host dies between emitting the effect and
        // reporting it, nothing ever confirms or fails it, and the provisional lease holds indefinitely.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness, TimeSpan.FromMinutes(10)));
        Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        // No EffectFinished ever arrives. Well past both the lease duration and any sane write timeout.
        harness.Clock.Advance(TimeSpan.FromHours(2));
        harness.ConfirmAbsent("ssh");

        Assert.False(harness.Step().Snapshot.ShouldHold);
    }
}

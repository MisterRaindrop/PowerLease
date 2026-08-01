using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// The safety properties the kernel exists to hold. Each one names a way the machine could be allowed
/// to sleep while it is in use, which is the only dangerous outcome this product has.
/// </summary>
public sealed class InhibitKernelTests
{
    [Fact]
    public void Every_source_confirming_there_is_nothing_to_hold_for_releases_protection()
    {
        // The control for everything below: release is reachable, so a test that shows protection being
        // held means something.
        var harness = new KernelHarness();

        var result = harness.AllQuiet();

        Assert.False(result.Snapshot.ShouldHold);
        Assert.Equal(ProtectionState.Released, result.Snapshot.ProtectionState);
    }

    [Fact]
    public void A_stale_report_that_nothing_is_happening_does_not_release_protection()
    {
        // The single most dangerous input in the product. The source said there was no SSH session, and
        // that was true when it said it; acting on it later would drop protection while someone is
        // connected.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);

        harness.Clock.Advance(TimeSpan.FromSeconds(45));
        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Contains(
            result.Snapshot.Decision.Inhibitors,
            inhibitor => inhibitor.Kind == InhibitorKind.ProducerUnhealthy
                && inhibitor.Reason.Contains("too old", StringComparison.Ordinal));
        Assert.False(Assert.Single(result.Snapshot.Sources).Trusted);
    }

    [Fact]
    public void A_duplicate_report_is_ignored_because_what_is_held_is_no_older()
    {
        var harness = new KernelHarness();
        harness.Observe("ssh", harness.SshInhibitor());
        var stamp = harness.StampAt("ssh", 1);

        Assert.Equal(
            ObservationDisposition.DiscardedDuplicate,
            harness.Deliver(stamp, InhibitorSourceReport.ConfirmedAbsent("ssh")));

        // The duplicate claimed nothing was happening. It must not have taken effect.
        Assert.True(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void An_out_of_order_report_is_ignored()
    {
        var harness = new KernelHarness();
        harness.Observe("ssh", harness.SshInhibitor());
        harness.Observe("ssh", harness.SshInhibitor());

        Assert.Equal(
            ObservationDisposition.DiscardedOutOfOrder,
            harness.Deliver(harness.StampAt("ssh", 1), InhibitorSourceReport.ConfirmedAbsent("ssh")));

        Assert.True(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_source_that_lost_reports_stops_being_believed_rather_than_being_read_as_idle()
    {
        // A gap means the source may be dropping messages, so the report that arrived cannot be relied on
        // either -- and crucially, dropping it instead would leave the older report in place.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);

        var gapped = harness.StampAt("ssh", 5);

        Assert.Equal(
            ObservationDisposition.DistrustedSequenceGap,
            harness.Deliver(gapped, InhibitorSourceReport.ConfirmedAbsent("ssh")));

        var result = harness.Step();
        Assert.True(result.Snapshot.ShouldHold);
        Assert.Contains(
            result.Snapshot.Decision.Inhibitors,
            inhibitor => inhibitor.Reason.Contains("Missed 3", StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_recovers_once_it_reports_contiguously_again()
    {
        // Without adopting the new position, one lost message would silence a source for good and the
        // machine would never sleep again.
        var harness = new KernelHarness();

        // A baseline first: the very first report from a source has no predecessor, so there is no gap to
        // detect against and it is simply believed.
        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);

        harness.Deliver(harness.StampAt("ssh", 5), InhibitorSourceReport.ConfirmedAbsent("ssh"));
        Assert.True(harness.Step().Snapshot.ShouldHold);

        Assert.Equal(
            ObservationDisposition.Accepted,
            harness.Deliver(harness.StampAt("ssh", 6), InhibitorSourceReport.ConfirmedAbsent("ssh")));

        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_report_computed_under_replaced_configuration_is_not_acted_on()
    {
        var harness = new KernelHarness();
        harness.Observe("ssh", harness.SshInhibitor());
        harness.ReplaceConfiguration();

        var stale = harness.StampAt("ssh", 9, configGeneration: 0);

        Assert.Equal(
            ObservationDisposition.DistrustedConfigGeneration,
            harness.Deliver(stale, InhibitorSourceReport.ConfirmedAbsent("ssh")));

        Assert.True(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_report_from_another_clock_epoch_is_not_acted_on()
    {
        var harness = new KernelHarness();

        var foreign = harness.StampAt("ssh", 1, epoch: FakeClock.EpochB);

        Assert.Equal(
            ObservationDisposition.DistrustedClockEpoch,
            harness.Deliver(foreign, InhibitorSourceReport.ConfirmedAbsent("ssh")));

        Assert.True(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_restarted_source_is_not_believed_until_its_next_report()
    {
        // Its sequence starts over, so a low number from the new run says nothing about the old one.
        var harness = new KernelHarness();
        harness.Observe("ssh", harness.SshInhibitor());
        harness.Observe("ssh", harness.SshInhibitor());
        harness.RestartSource("ssh");

        Assert.Equal(
            ObservationDisposition.DistrustedSequenceGap,
            harness.Report("ssh", InhibitorSourceReport.ConfirmedAbsent("ssh")));
        Assert.True(harness.Step().Snapshot.ShouldHold);

        Assert.Equal(ObservationDisposition.Accepted, harness.ConfirmAbsent("ssh"));
        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void An_expected_source_that_has_never_reported_holds_unconditionally()
    {
        var harness = new KernelHarness();

        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Equal(["ssh"], result.Snapshot.UnhealthySources);
    }

    [Fact]
    public void An_expected_source_that_goes_quiet_holds_unconditionally_again()
    {
        var harness = new KernelHarness();
        harness.AllQuiet();

        harness.Clock.Advance(TimeSpan.FromSeconds(90));
        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Equal(["ssh"], result.Snapshot.UnhealthySources);
    }

    [Fact]
    public void Resuming_throws_away_every_observation_retakes_the_request_and_holds_for_a_while()
    {
        var harness = new KernelHarness();
        harness.AllQuiet();
        Assert.Equal(ProtectionState.Released, harness.Kernel.Snapshot.ProtectionState);

        harness.Observe("ssh", harness.SshInhibitor());
        harness.Step();
        var heldBefore = harness.Coordinator.HeldGeneration;
        Assert.NotNull(heldBefore);

        harness.Clock.BeginNewEpoch();
        harness.Kernel.Apply(new ResumedFromSleep("power event"));
        var result = harness.Step();

        // The operating system dropped the request when the machine suspended, so the old handle is closed
        // and a new one taken out.
        Assert.Contains(heldBefore.Value, harness.Inhibitor.Closed);
        Assert.NotEqual(heldBefore, harness.Coordinator.HeldGeneration);

        // Nothing observed before the machine slept is believed, and the grace period holds regardless.
        Assert.True(result.Snapshot.GracePeriodActive);
        Assert.True(result.Snapshot.ShouldHold);
        Assert.All(result.Snapshot.Sources, source => Assert.False(source.Trusted));
    }

    [Fact]
    public void A_time_change_makes_every_source_report_again()
    {
        // A schedule is written in local time, so a source that evaluated one against the old zone may now
        // be wrong. The kernel does not try to work out which sources those are.
        var harness = new KernelHarness();
        harness.AllQuiet();
        Assert.False(harness.Kernel.Snapshot.ShouldHold);

        harness.Kernel.Apply(new TimeAdjusted("time zone changed"));
        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.All(result.Snapshot.Sources, source => Assert.False(source.Trusted));

        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_latched_fault_is_itself_a_reason_to_stay_awake()
    {
        var harness = new KernelHarness();
        harness.Kernel.Apply(new FaultObserved("config", FaultSeverity.Persistent, "config.json failed validation"));

        var result = harness.AllQuiet();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Contains(result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.Fault);
        Assert.Single(result.Snapshot.Faults);
    }

    [Fact]
    public void A_persistent_fault_is_not_cleared_by_things_looking_healthy()
    {
        var harness = new KernelHarness();
        harness.Kernel.Apply(new FaultObserved("config", FaultSeverity.Persistent, "config.json failed validation"));

        for (var i = 0; i < 10; i++)
        {
            harness.Kernel.Apply(new FaultHealthy("config"));
        }

        Assert.True(harness.AllQuiet().Snapshot.ShouldHold);

        harness.Kernel.Apply(new FaultRepaired("config"));
        Assert.False(harness.AllQuiet().Snapshot.ShouldHold);
    }

    [Fact]
    public void A_refused_power_request_is_reported_as_unprotected_and_latches_a_fault()
    {
        // Not merely logged. A tool that says "protected" while the system is ignoring the request is worse
        // than one that says nothing, and the latched fault keeps it trying.
        var harness = new KernelHarness();
        harness.Inhibitor.Default = PowerInhibitResult.Rejected;
        harness.Observe("ssh", harness.SshInhibitor());

        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Equal(ProtectionState.Unprotected, result.Snapshot.ProtectionState);
        Assert.True(result.Snapshot.IsProtectionFailing);
        Assert.Contains(result.Snapshot.Faults, fault => fault.Key == "power-request");
    }

    [Fact]
    public void The_emergency_switch_only_moves_towards_more_protection()
    {
        var harness = new KernelHarness();
        harness.AllQuiet();
        Assert.False(harness.Kernel.Snapshot.ShouldHold);

        Assert.True(harness.Kernel.RaiseEmergencyInhibit("a source saw something alarming"));
        Assert.False(harness.Kernel.RaiseEmergencyInhibit("again"));

        var result = harness.Step();
        Assert.True(result.Snapshot.ShouldHold);
        Assert.True(result.Snapshot.EmergencyInhibitRaised);
        Assert.Equal("a source saw something alarming", result.Snapshot.EmergencyInhibitReason);
    }

    [Fact]
    public void The_emergency_switch_is_only_lowered_after_every_expected_source_reports_afresh()
    {
        // A source that raised the alarm must not also get to decide it is over. Reports from before the
        // alarm describe the situation that caused it.
        var harness = new KernelHarness();
        harness.AllQuiet();
        harness.Kernel.RaiseEmergencyInhibit("a source saw something alarming");

        // First evaluation only records that a resynchronisation is now required.
        Assert.True(harness.Step().Snapshot.EmergencyInhibitRaised);

        // Evaluating again changes nothing while the reports still predate the alarm.
        Assert.True(harness.Step().Snapshot.EmergencyInhibitRaised);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        harness.ConfirmAbsent("ssh");
        var result = harness.Step();

        Assert.False(result.Snapshot.EmergencyInhibitRaised);
        Assert.False(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void The_published_state_is_never_observed_out_of_order_while_the_loop_is_working()
    {
        // Answering a status query must not wait behind the mutation path. What can be asserted here is the
        // weaker but still load-bearing half: a reader on another thread only ever sees whole snapshots, in
        // order.
        //
        // The overlap is made certain rather than hoped for. An earlier version of this test simply started both
        // and checked afterwards that they had run together, which held locally and failed on a loaded CI runner
        // where the reader was not scheduled until the writer had finished -- so every recorded revision was the
        // last one and the ordering assertion was satisfied by a list of identical values. Here the writer blocks
        // after its first turn until the reader has recorded something, which gives up the core and leaves the
        // reader no way not to run.
        var harness = new KernelHarness();
        var revisions = new List<long>();
        var readerHasRecorded = new ManualResetEventSlim(false);
        var writerDone = false;

        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref writerDone))
            {
                revisions.Add(harness.Kernel.Snapshot.Revision);
                readerHasRecorded.Set();
            }

            revisions.Add(harness.Kernel.Snapshot.Revision);
        });

        reader.Start();

        harness.ConfirmAbsent("ssh");
        harness.Step();

        Assert.True(
            readerHasRecorded.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            "the reader thread never ran");

        for (var i = 0; i < 499; i++)
        {
            harness.ConfirmAbsent("ssh");
            harness.Step();
        }

        Volatile.Write(ref writerDone, true);
        reader.Join();
        readerHasRecorded.Dispose();

        Assert.Equal(revisions.OrderBy(revision => revision), revisions);
        Assert.Equal(500, harness.Kernel.Snapshot.Revision);
        Assert.True(
            revisions.Distinct().Count() > 1,
            $"the reader never overlapped the writer: it only ever saw revision {revisions[0]}");
    }

    [Fact]
    public void Only_the_kernel_moves_the_revision_and_it_only_goes_forward()
    {
        var harness = new KernelHarness();

        Assert.Equal(0, harness.Kernel.Snapshot.Revision);
        Assert.Equal(1, harness.Step().Snapshot.Revision);
        harness.Kernel.RaiseEmergencyInhibit("nothing to do with the revision");
        Assert.Equal(1, harness.Kernel.Snapshot.Revision);
        Assert.Equal(2, harness.Step().Snapshot.Revision);
    }

    [Fact]
    public void The_kernel_cannot_perform_input_or_output()
    {
        // Structural, not a convention. Persistence depends on this project, so this project cannot depend
        // on persistence: the kernel could not block on a disk even if someone tried to make it.
        var referenced = typeof(InhibitKernel).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("PowerLease.Persistence", referenced);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", referenced);
        Assert.Contains("PowerLease.Domain", referenced);
    }

    [Fact]
    public void Uncovered_inhibitor_kinds_are_published_so_the_boundary_of_protection_is_visible()
    {
        var harness = new KernelHarness();

        var snapshot = harness.AllQuiet().Snapshot;

        Assert.DoesNotContain(InhibitorKind.SshSession, snapshot.Decision.UncoveredKinds);
        Assert.Contains(InhibitorKind.LockFile, snapshot.Decision.UncoveredKinds);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        var harness = new KernelHarness();

        Assert.Throws<ArgumentNullException>(
            () => new InhibitKernel(null!, harness.Coordinator, harness.Clock));
        Assert.Throws<ArgumentNullException>(() => new InhibitKernel(harness.Options, null!, harness.Clock));
        Assert.Throws<ArgumentNullException>(
            () => new InhibitKernel(harness.Options, harness.Coordinator, null!));
        Assert.Throws<ArgumentNullException>(() => harness.Kernel.Apply((SourceObservation)null!));
        Assert.Throws<ArgumentNullException>(() => harness.Kernel.Apply((ControlMessage)null!));
        Assert.Throws<ArgumentNullException>(() => harness.Kernel.Execute(null!));
    }
}

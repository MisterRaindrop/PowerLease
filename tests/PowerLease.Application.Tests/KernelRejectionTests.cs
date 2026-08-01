using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// The paths where the kernel says no, and the startup path. Each one either refuses something that would
/// be wrong to do or holds the machine awake while nothing is known yet.
/// </summary>
public sealed class KernelRejectionTests
{
    private static readonly CallerSnapshot Liu = new()
    {
        Sid = "S-1-5-21-1",
        AccountName = "liu",
        IsElevated = false,
        IsAdministrator = false
    };

    private static LeaseCommand Command(
        KernelHarness harness,
        LeaseCommandKind kind,
        string requestId = "req-1",
        string? leaseId = "lease-1",
        CallerSnapshot? caller = null,
        TimeSpan? duration = null) => new()
        {
            RequestId = requestId,
            Caller = caller ?? Liu,
            Kind = kind,
            LeaseId = leaseId,
            Duration = duration ?? TimeSpan.FromHours(1),
            Deadline = new MonotonicStamp(harness.Clock.EpochId, harness.Clock.Elapsed + TimeSpan.FromSeconds(10)),
            PayloadHash = "hash-a"
        };

    private static KernelHarness WithCommittedLease(out KernelHarness harness)
    {
        harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Command(harness, LeaseCommandKind.Create));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded)));
        harness.Step();
        return harness;
    }

    [Fact]
    public void Starting_the_service_holds_the_machine_awake_while_nothing_is_known_yet()
    {
        // The operating system dropped the power request when the previous process died, so the machine was
        // unprotected for however long the restart took, and no source has reported since.
        var harness = new KernelHarness(new KernelOptions
        {
            ExpectedSources = ["ssh"],
            ObservationFreshness = TimeSpan.FromSeconds(30),
            HeartbeatFreshness = TimeSpan.FromSeconds(60),
            StartupGracePeriod = TimeSpan.FromMinutes(15)
        });

        // The kernel notices its own first evaluation and treats it as a start, so wait that one out before
        // testing what an explicit notification does.
        harness.ConfirmAbsent("ssh");
        harness.Step();
        harness.Clock.Advance(harness.Options.StartupGracePeriod);
        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);

        harness.Kernel.Apply(new ServiceStarted("after a crash"));
        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.True(result.Snapshot.GracePeriodActive);
        Assert.Contains(result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.GracePeriod);
        Assert.All(result.Snapshot.Sources, source => Assert.False(source.Trusted));

        // It ends on its own once enough time has passed for the picture to be real again.
        harness.Clock.Advance(harness.Options.StartupGracePeriod);
        harness.ConfirmAbsent("ssh");
        var later = harness.Step();
        Assert.False(later.Snapshot.GracePeriodActive);
        Assert.False(later.Snapshot.ShouldHold);
    }

    [Fact]
    public void Creating_a_lease_that_already_exists_is_refused()
    {
        WithCommittedLease(out var harness);

        harness.Kernel.Execute(Command(harness, LeaseCommandKind.Create, "req-2"));

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void Renewing_a_lease_that_is_being_released_is_refused()
    {
        // Its ending is already being written. Extending it now would race that write.
        WithCommittedLease(out var harness);
        harness.Kernel.Execute(Command(harness, LeaseCommandKind.Release, "req-2"));
        harness.Step();

        harness.Kernel.Execute(Command(harness, LeaseCommandKind.Renew, "req-3"));

        var answer = Assert.Single(harness.Step().CompletedCommands);
        Assert.Equal(LeaseCommandStatus.Rejected, answer.Status);
        Assert.Contains("being released", answer.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Renewing_someone_elses_lease_is_refused()
    {
        WithCommittedLease(out var harness);
        var stranger = new CallerSnapshot { Sid = "S-1-5-21-2", AccountName = "someone-else" };

        harness.Kernel.Execute(Command(harness, LeaseCommandKind.Renew, "req-2", caller: stranger));

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void Renewing_for_no_time_at_all_is_refused()
    {
        WithCommittedLease(out var harness);

        harness.Kernel.Execute(Command(harness, LeaseCommandKind.Renew, "req-2", duration: TimeSpan.Zero));

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void Renewing_a_lease_that_has_not_been_re_established_on_this_clock_is_refused()
    {
        // Its remaining time was measured against a different monotonic origin, so it cannot be read yet.
        // Refusing costs nothing: the lease goes on holding until the startup path restores it.
        WithCommittedLease(out var harness);
        harness.Clock.BeginNewEpoch();

        harness.Kernel.Execute(Command(harness, LeaseCommandKind.Renew, "req-2"));

        var answer = Assert.Single(harness.Step().CompletedCommands);
        Assert.Equal(LeaseCommandStatus.Rejected, answer.Status);
        Assert.Contains("re-established", answer.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lease_measured_against_another_clock_keeps_holding_rather_than_being_read_wrongly()
    {
        WithCommittedLease(out var harness);
        harness.Clock.BeginNewEpoch();
        harness.ConfirmAbsent("ssh");

        var result = harness.Step();

        Assert.Contains(result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
    }

    [Fact]
    public void A_protection_change_is_recorded_with_the_reasons_and_the_revision()
    {
        var harness = new KernelHarness();
        harness.Observe("ssh", harness.SshInhibitor());

        var result = harness.Step();

        Assert.True(result.ProtectionChanged);
        var journal = Assert.Single(result.Effects, candidate => candidate.Kind == EffectKind.RecordInhibitChange);
        Assert.Equal(ProtectionState.Protected, journal.ProtectionState);
        Assert.Contains(InhibitorKind.SshSession, journal.InhibitorKinds);
        Assert.Equal(result.Snapshot.Revision, journal.Revision);

        // Nothing changed the second time, so there is nothing to record.
        harness.Observe("ssh", harness.SshInhibitor());
        var again = harness.Step();
        Assert.False(again.ProtectionChanged);
        Assert.DoesNotContain(again.Effects, candidate => candidate.Kind == EffectKind.RecordInhibitChange);
    }

    [Fact]
    public void The_alarm_stays_up_while_an_expected_source_is_unhealthy_even_after_it_reports()
    {
        // Reporting once is not enough if the source is then quiet long enough to count as gone.
        var harness = new KernelHarness();
        harness.AllQuiet();
        harness.Kernel.RaiseEmergencyInhibit("something alarming");
        harness.Step();

        harness.Clock.Advance(TimeSpan.FromSeconds(90));
        harness.ConfirmAbsent("ssh");
        harness.Clock.Advance(TimeSpan.FromSeconds(90));

        var result = harness.Step();

        Assert.True(result.Snapshot.EmergencyInhibitRaised);
        Assert.True(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void An_unrecognised_control_message_is_refused_rather_than_ignored()
    {
        // Silently ignoring one would mean a future message that changes what the kernel believes could be
        // dropped without a trace.
        var harness = new KernelHarness();

        Assert.Throws<ArgumentException>(() => harness.Kernel.Apply(new UnknownControlMessage()));
    }

    [Fact]
    public void An_unrecognised_command_kind_is_refused()
    {
        var harness = new KernelHarness();

        harness.Kernel.Execute(Command(harness, (LeaseCommandKind)99));

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void The_published_state_carries_what_status_has_to_show()
    {
        var harness = new KernelHarness();
        harness.ReplaceConfiguration();
        harness.ConfirmAbsent("ssh");

        var snapshot = harness.Step().Snapshot;

        Assert.Equal(harness.ConfigGeneration, snapshot.ConfigGeneration);
        var source = Assert.Single(snapshot.Sources);
        Assert.Equal("ssh", source.SourceId);
        Assert.Equal(ObservationDisposition.Accepted, source.LastDisposition);
        Assert.True(source.Trusted);
        Assert.Null(source.Detail);
        Assert.False(harness.Kernel.EmergencyInhibitRaised);
    }

    [Fact]
    public void A_callers_elevation_is_captured_with_the_rest_of_its_identity()
    {
        // Decided when the request arrived, never re-read: a token handle held across the queue could be
        // revoked by the time the command is carried out.
        var elevated = Liu with { IsElevated = true };

        Assert.True(elevated.IsElevated);
        Assert.False(Liu.IsElevated);
    }

    private sealed record UnknownControlMessage : ControlMessage;
}

using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// The paths an independent review found by which protection could be given up while a reason to hold it still
/// existed. Each test fails against the code as it was.
/// </summary>
public sealed class ArchitectReviewFixTests
{
    private static readonly Guid Epoch = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    private static readonly DateTimeOffset Noon = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static MonotonicStamp At(double seconds) => new(Epoch, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Quiet_is_never_claimed_from_a_measurement_that_was_itself_busy()
    {
        // One busy sample and then silence. Crediting the silence since that sample as quiet would let the
        // activity source confirm absence while the work that produced the sample is still running -- the
        // machine sleeping mid-build.
        var tracker = new DurationThresholdTracker(
            threshold: 10, requiredQuietDuration: TimeSpan.FromSeconds(10), maxSampleGap: TimeSpan.FromSeconds(15));

        tracker.Observe(At(0), 80);

        var assessment = tracker.Assess(At(10));

        Assert.Equal(ActivityVerdict.Active, assessment.Verdict);
        Assert.True(assessment.Inhibits);
        Assert.Equal(TimeSpan.Zero, assessment.QuietFor);
    }

    [Fact]
    public void Quiet_still_accrues_normally_once_there_are_quiet_measurements()
    {
        // The control: the stricter rule must not make quiet unreachable.
        var tracker = new DurationThresholdTracker(
            threshold: 10, requiredQuietDuration: TimeSpan.FromSeconds(10), maxSampleGap: TimeSpan.FromSeconds(15));

        for (var second = 0.0; second <= 20; second += 5)
        {
            tracker.Observe(At(second), 1);
        }

        Assert.Equal(ActivityVerdict.QuietLongEnough, tracker.Assess(At(20)).Verdict);
    }

    [Fact]
    public void A_lease_renewed_for_longer_than_it_began_with_keeps_that_longer_time_across_a_restart()
    {
        // A one-hour lease renewed for three. The stored duration is the ceiling a restored checkpoint is checked
        // against, so leaving it at one hour makes the renewal look self-contradictory and silently cuts the hold
        // back -- protection ending two hours before the user was told it would.
        var granted = LeaseDeadline.Grant(At(0), TimeSpan.FromHours(1));
        var renewed = granted.Renew(At(60), TimeSpan.FromHours(3));
        var remaining = renewed.RemainingAt(At(60));

        var lease = new KeepAwakeLease
        {
            Id = "lease-1",
            Source = LeaseSource.Cli,
            StartedAtUtc = Noon,
            Status = LeaseStatus.Active,
            EpochId = Epoch,
            OriginalDuration = remaining > TimeSpan.FromHours(1) ? remaining : TimeSpan.FromHours(1),
            RemainingAtCheckpoint = remaining,
            CheckpointUtc = Noon
        };

        var resumed = LeaseDeadline.Resume(
            lease.TryGetCheckpoint(),
            lease.OriginalDuration,
            new MonotonicStamp(Guid.NewGuid(), TimeSpan.Zero));

        Assert.Equal(LeaseResumeDecision.RemainingFromCheckpoint, resumed.Decision);
        Assert.Equal(remaining, resumed.Deadline.RemainingAtGrant);
    }

    [Fact]
    public void An_unannounced_clock_restart_is_noticed_and_the_request_taken_out_again()
    {
        // The operating system drops the power request when the machine suspends. If the notification saying so
        // never arrives, the coordinator goes on believing it holds one -- so every later attempt returns early
        // without reacquiring, and the machine reports itself protected while holding nothing.
        var harness = new KernelHarness();
        harness.Observe("ssh", harness.SshInhibitor());
        harness.Step();
        var before = harness.Coordinator.HeldGeneration;
        Assert.NotNull(before);

        // No ResumedFromSleep. Only the clock says anything happened.
        harness.Clock.BeginNewEpoch();
        harness.Observe("ssh", harness.SshInhibitor());
        var result = harness.Step();

        Assert.Contains(before.Value, harness.Inhibitor.Closed);
        Assert.NotEqual(before, harness.Coordinator.HeldGeneration);
        Assert.True(result.Snapshot.GracePeriodActive);
        Assert.True(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void An_adapter_that_throws_while_letting_go_does_not_leave_the_request_believed_held()
    {
        // If Close released the request and then threw, keeping the generation would make every later attempt
        // return early without reacquiring: protected in name, holding nothing.
        var inhibitor = new ThrowingInhibitor { ThrowOnClose = true };
        var coordinator = new PowerInhibitCoordinator(inhibitor);
        coordinator.Ensure(shouldHold: true);

        coordinator.Ensure(shouldHold: false);

        Assert.Null(coordinator.HeldGeneration);
        Assert.Equal(ProtectionState.Released, coordinator.State);

        // And it can take one out again.
        Assert.Equal(ProtectionState.Protected, coordinator.Ensure(shouldHold: true));
    }

    [Fact]
    public void An_adapter_that_throws_while_taking_out_the_request_reports_it_as_unprotected()
    {
        // A throw says nothing about whether the request took effect, which is exactly what uncertain means.
        var inhibitor = new ThrowingInhibitor { ThrowOnAcquire = true };
        var coordinator = new PowerInhibitCoordinator(inhibitor);

        Assert.Equal(ProtectionState.Unprotected, coordinator.Ensure(shouldHold: true));
        Assert.Null(coordinator.HeldGeneration);
        Assert.Equal([1], inhibitor.Closed);
    }

    [Fact]
    public void The_emergency_switch_raised_during_a_release_takes_effect_on_the_very_next_turn()
    {
        // The latch is the one thing another thread may change without waiting for a turn, so it can be raised
        // after the decision has been taken. The decision now re-reads it immediately before acting, which closes
        // all of that window except the few instructions between the re-read and the platform call. Closing even
        // that would mean locking the raise path, which has to stay lock-free for a producer that cannot wait --
        // so what is guaranteed instead is that the very next evaluation puts the request back.
        var harness = new KernelHarness();

        // Hold something first, so there is a request to let go of.
        harness.Observe("ssh", harness.SshInhibitor());
        Assert.Equal(ProtectionState.Protected, harness.Step().Snapshot.ProtectionState);

        harness.ConfirmAbsent("ssh");
        harness.Inhibitor.RaiseBeforeNextClose = harness.Kernel;

        var released = harness.Step();
        Assert.Equal(ProtectionState.Released, released.Snapshot.ProtectionState);

        harness.ConfirmAbsent("ssh");
        var recovered = harness.Step();

        Assert.True(recovered.Snapshot.ShouldHold);
        Assert.Equal(ProtectionState.Protected, recovered.Snapshot.ProtectionState);
        Assert.True(recovered.Snapshot.EmergencyInhibitRaised);
    }

    [Fact]
    public void The_emergency_switch_raised_before_the_decision_acts_is_honoured_at_once()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.RaiseEmergencyInhibit("a source saw something alarming");

        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Equal(ProtectionState.Protected, result.Snapshot.ProtectionState);
    }

    [Fact]
    public void A_grace_period_that_would_overflow_the_clock_saturates_instead_of_throwing()
    {
        // An exception escaping here would leave the caller having recorded no reason to stay awake at all, on the
        // one path whose whole purpose is to record one unconditionally.
        var guard = new GracePeriodGuard();

        guard.Begin(new MonotonicStamp(Epoch, TimeSpan.MaxValue - TimeSpan.FromSeconds(1)), TimeSpan.FromDays(1), "resumed");

        Assert.True(guard.IsActive(new MonotonicStamp(Epoch, TimeSpan.MaxValue - TimeSpan.FromSeconds(1))));
    }

    private sealed class ThrowingInhibitor : IPowerInhibitor
    {
        public bool ThrowOnAcquire { get; init; }

        public bool ThrowOnClose { get; init; }

        public List<long> Closed { get; } = [];

        public PowerInhibitResult Acquire(long generation) =>
            ThrowOnAcquire && generation == 1
                ? throw new InvalidOperationException("the platform call failed")
                : PowerInhibitResult.Held;

        public void Close(long generation)
        {
            Closed.Add(generation);
            if (ThrowOnClose)
            {
                throw new InvalidOperationException("the handle was already gone");
            }
        }
    }
}

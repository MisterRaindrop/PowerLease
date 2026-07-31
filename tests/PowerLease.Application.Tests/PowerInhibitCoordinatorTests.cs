using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// The reference-counted Windows call is the trap here. A count that drifts upwards can never be brought
/// back to zero reliably, which would pin the machine awake for as long as the service runs -- the exact
/// failure that made a power-management tool worse than not having one.
/// </summary>
public sealed class PowerInhibitCoordinatorTests
{
    private static (PowerInhibitCoordinator Coordinator, FakePowerInhibitor Inhibitor) Build()
    {
        var inhibitor = new FakePowerInhibitor();
        return (new PowerInhibitCoordinator(inhibitor), inhibitor);
    }

    [Fact]
    public void Asking_to_hold_takes_out_one_request()
    {
        var (coordinator, inhibitor) = Build();

        Assert.Equal(ProtectionState.Protected, coordinator.Ensure(shouldHold: true));
        Assert.Equal([1], inhibitor.Acquired);
        Assert.Equal(1, coordinator.HeldGeneration);
    }

    [Fact]
    public void Asking_to_hold_again_does_not_take_out_a_second_one()
    {
        var (coordinator, inhibitor) = Build();
        coordinator.Ensure(shouldHold: true);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ProtectionState.Protected, coordinator.Ensure(shouldHold: true));
        }

        Assert.Single(inhibitor.Acquired);
        Assert.Equal(1, coordinator.AcquireCount);
    }

    [Fact]
    public void Letting_go_closes_the_handle_that_was_held()
    {
        var (coordinator, inhibitor) = Build();
        coordinator.Ensure(shouldHold: true);

        Assert.Equal(ProtectionState.Released, coordinator.Ensure(shouldHold: false));
        Assert.Equal([1], inhibitor.Closed);
        Assert.Null(coordinator.HeldGeneration);
    }

    [Fact]
    public void Letting_go_when_nothing_is_held_closes_nothing()
    {
        var (coordinator, inhibitor) = Build();

        Assert.Equal(ProtectionState.Released, coordinator.Ensure(shouldHold: false));
        Assert.Empty(inhibitor.Closed);
    }

    [Fact]
    public void A_refusal_reports_unprotected_and_leaves_nothing_to_close()
    {
        // A refusal means the request never existed, so there is no handle. Wanting to hold and being
        // refused is not the same as not wanting to hold, which is why the state is Unprotected rather than
        // Released.
        var (coordinator, inhibitor) = Build();
        inhibitor.Default = PowerInhibitResult.Rejected;

        Assert.Equal(ProtectionState.Unprotected, coordinator.Ensure(shouldHold: true));
        Assert.Empty(inhibitor.Closed);
        Assert.Null(coordinator.HeldGeneration);
    }

    [Fact]
    public void An_uncertain_result_closes_that_whole_generation_rather_than_guessing()
    {
        // The request may or may not have taken effect. Guessing how many clears the reference count needs
        // is how a count drifts; closing the handle outright cannot.
        var (coordinator, inhibitor) = Build();
        inhibitor.Next(PowerInhibitResult.Uncertain);

        Assert.Equal(ProtectionState.Unprotected, coordinator.Ensure(shouldHold: true));
        Assert.Equal([1], inhibitor.Acquired);
        Assert.Equal([1], inhibitor.Closed);
        Assert.Null(coordinator.HeldGeneration);
    }

    [Fact]
    public void After_an_uncertain_result_the_next_attempt_uses_a_fresh_generation()
    {
        var (coordinator, inhibitor) = Build();
        inhibitor.Next(PowerInhibitResult.Uncertain);
        coordinator.Ensure(shouldHold: true);

        Assert.Equal(ProtectionState.Protected, coordinator.Ensure(shouldHold: true));
        Assert.Equal([1, 2], inhibitor.Acquired);
        Assert.Equal(2, coordinator.HeldGeneration);
    }

    [Fact]
    public void After_a_refusal_the_next_attempt_can_still_succeed()
    {
        // A power plan that was changed to allow it should start working without a restart.
        var (coordinator, inhibitor) = Build();
        inhibitor.Next(PowerInhibitResult.Rejected);
        coordinator.Ensure(shouldHold: true);

        Assert.Equal(ProtectionState.Protected, coordinator.Ensure(shouldHold: true));
    }

    [Fact]
    public void Invalidating_closes_the_handle_even_though_the_system_may_already_have_dropped_it()
    {
        // What a resume needs. Closing one the system has discarded is harmless; leaving one open that we
        // believe is held is not.
        var (coordinator, inhibitor) = Build();
        coordinator.Ensure(shouldHold: true);

        coordinator.Invalidate();

        Assert.Equal([1], inhibitor.Closed);
        Assert.Equal(ProtectionState.Released, coordinator.State);
        Assert.Null(coordinator.HeldGeneration);
    }

    [Fact]
    public void Every_acquisition_gets_its_own_generation()
    {
        var (coordinator, inhibitor) = Build();

        coordinator.Ensure(shouldHold: true);
        coordinator.Ensure(shouldHold: false);
        coordinator.Ensure(shouldHold: true);

        Assert.Equal([1, 2], inhibitor.Acquired);
        Assert.Equal(2, coordinator.HeldGeneration);
    }

    [Fact]
    public void An_inhibitor_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => new PowerInhibitCoordinator(null!));
    }
}

public sealed class EmergencyInhibitLatchTests
{
    [Fact]
    public void It_can_only_be_raised_once()
    {
        var latch = new EmergencyInhibitLatch();

        Assert.True(latch.Raise("first"));
        Assert.False(latch.Raise("second"));
        Assert.Equal("first", latch.Reason);
    }

    [Fact]
    public void Anything_seeing_it_raised_can_see_why()
    {
        var latch = new EmergencyInhibitLatch();
        Assert.False(latch.IsRaised);
        Assert.Null(latch.Reason);

        latch.Raise("a source saw something alarming");

        Assert.True(latch.IsRaised);
        Assert.Equal("a source saw something alarming", latch.Reason);
    }

    [Fact]
    public void Lowering_it_twice_reports_that_it_was_already_down()
    {
        var latch = new EmergencyInhibitLatch();
        latch.Raise("reason");

        Assert.True(latch.Clear());
        Assert.False(latch.Clear());
        Assert.Null(latch.Reason);
    }

    [Fact]
    public void Raising_it_from_many_threads_at_once_produces_one_winner()
    {
        var latch = new EmergencyInhibitLatch();
        var winners = 0;

        Parallel.For(0, 64, _ =>
        {
            if (latch.Raise("concurrent"))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
        Assert.True(latch.IsRaised);
    }

    [Fact]
    public void A_reason_is_required()
    {
        Assert.ThrowsAny<ArgumentException>(() => new EmergencyInhibitLatch().Raise(string.Empty));
    }
}

public sealed class GracePeriodGuardTests
{
    private static readonly Guid Epoch = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private static MonotonicStamp At(double seconds, Guid? epoch = null) =>
        new(epoch ?? Epoch, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Nothing_holds_before_a_period_begins()
    {
        Assert.False(new GracePeriodGuard().IsActive(At(0)));
        Assert.Null(new GracePeriodGuard().ToInhibitor(At(0), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_period_holds_until_it_runs_out()
    {
        var guard = new GracePeriodGuard();
        guard.Begin(At(0), TimeSpan.FromSeconds(60), "resumed");

        Assert.True(guard.IsActive(At(59)));
        Assert.False(guard.IsActive(At(60)));

        var inhibitor = guard.ToInhibitor(At(30), DateTimeOffset.UnixEpoch);
        Assert.NotNull(inhibitor);
        Assert.Equal(InhibitorKind.GracePeriod, inhibitor.Kind);
        Assert.Equal("resumed", inhibitor.Reason);
    }

    [Fact]
    public void Beginning_again_never_shortens_what_is_already_running()
    {
        // Two reasons to hold arriving together is a reason to hold for the longer of the two.
        var guard = new GracePeriodGuard();
        guard.Begin(At(0), TimeSpan.FromMinutes(15), "service started");

        guard.Begin(At(10), TimeSpan.FromSeconds(30), "resumed");

        Assert.True(guard.IsActive(At(14 * 60)));
    }

    [Fact]
    public void A_longer_period_does_extend_it()
    {
        var guard = new GracePeriodGuard();
        guard.Begin(At(0), TimeSpan.FromSeconds(30), "resumed");

        guard.Begin(At(10), TimeSpan.FromMinutes(15), "service started");

        Assert.True(guard.IsActive(At(15 * 60)));
    }

    [Fact]
    public void A_period_measured_against_another_clock_counts_as_still_running()
    {
        // Means a resume happened without the kernel being told, which is exactly when protection matters.
        var guard = new GracePeriodGuard();
        guard.Begin(At(0), TimeSpan.FromSeconds(1), "resumed");

        Assert.True(guard.IsActive(At(3600, Guid.Parse("dddddddd-4444-4444-4444-444444444444"))));
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        var guard = new GracePeriodGuard();

        Assert.Throws<ArgumentOutOfRangeException>(() => guard.Begin(At(0), TimeSpan.Zero, "reason"));
        Assert.ThrowsAny<ArgumentException>(() => guard.Begin(At(0), TimeSpan.FromSeconds(1), string.Empty));
    }
}

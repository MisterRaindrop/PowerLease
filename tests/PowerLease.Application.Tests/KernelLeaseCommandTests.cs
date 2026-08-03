using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// Lease commands. The rule running through all of these: protection is added before it is durable and
/// given up only after, so the direction that can leave a machine asleep while in use is the one that
/// has to wait for the disk.
/// </summary>
public sealed class KernelLeaseCommandTests
{
    private static readonly CallerSnapshot Liu = new()
    {
        Sid = "S-1-5-21-1",
        AccountName = "liu",
        IsAdministrator = false
    };

    private static readonly CallerSnapshot Administrator = new()
    {
        Sid = "S-1-5-21-500",
        AccountName = "admin",
        IsAdministrator = true
    };

    private static LeaseCommand Create(
        KernelHarness harness,
        string requestId = "req-1",
        string leaseId = "lease-1",
        CallerSnapshot? caller = null,
        TimeSpan? duration = null,
        TimeSpan? deadlineIn = null) => new()
        {
            RequestId = requestId,
            Caller = caller ?? Liu,
            Kind = LeaseCommandKind.Create,
            LeaseId = leaseId,
            Duration = duration ?? TimeSpan.FromHours(1),
            Source = LeaseSource.Cli,
            Reason = "manual hold",
            Deadline = new MonotonicStamp(
                harness.Clock.EpochId,
                harness.Clock.Elapsed + (deadlineIn ?? TimeSpan.FromSeconds(10))),
            PayloadHash = "hash-a"
        };

    [Fact]
    public void A_new_lease_holds_the_machine_awake_before_it_is_durable()
    {
        // Adding protection early is safe. Waiting for the disk first would leave a window in which the
        // user has been told to expect protection and does not have it.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));

        var result = harness.Step();

        Assert.True(result.Snapshot.ShouldHold);
        var effect = Assert.Single(result.Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        Assert.Equal("lease-1", effect.Lease!.Id);
        Assert.Equal("hash-a", effect.PayloadHash);

        // No answer yet: the caller is told once the change is durable.
        Assert.Empty(result.CompletedCommands);
    }

    [Fact]
    public void A_lease_is_confirmed_to_the_caller_once_it_is_durable()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded)));
        var result = harness.Step();

        var answer = Assert.Single(result.CompletedCommands);
        Assert.Equal(LeaseCommandStatus.Created, answer.Status);
        Assert.Equal("lease-1", answer.LeaseId);
        Assert.True(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void A_lease_that_cannot_be_made_durable_still_leaves_the_machine_awake()
    {
        // The provisional lease goes, which on its own would reduce protection, so the failure is latched --
        // and a latched fault is itself a reason to stay awake.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(effect.EffectId, EffectOutcome.Failed, Error: "disk full")));
        var result = harness.Step();

        Assert.Equal(LeaseCommandStatus.Failed, Assert.Single(result.CompletedCommands).Status);
        Assert.True(result.Snapshot.ShouldHold);
        Assert.Contains(result.Snapshot.Faults, fault => fault.Key.StartsWith("lease-persist:", StringComparison.Ordinal));
    }

    [Fact]
    public void Releasing_a_lease_keeps_holding_until_the_ending_is_durable()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        harness.Kernel.Execute(Create(harness, "req-2") with
        {
            Kind = LeaseCommandKind.Release,
            PayloadHash = "hash-b"
        });
        var releasing = harness.Step();

        // Still holding: the inhibitor stays in place while the ending is being written.
        Assert.True(releasing.Snapshot.ShouldHold);
        Assert.Single(releasing.Effects, candidate => candidate.Kind == EffectKind.PersistLeaseRelease);
    }

    [Fact]
    public void A_release_that_cannot_be_made_durable_does_not_lift_protection()
    {
        // The one direction that reduces protection. If the write fails, the lease goes back to holding and
        // the failure is latched; anything else would mean a machine allowed to sleep on the strength of a
        // write that did not happen.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        harness.Kernel.Execute(Create(harness, "req-2") with { Kind = LeaseCommandKind.Release });
        var release = Assert.Single(
            harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLeaseRelease);

        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(release.EffectId, EffectOutcome.Failed, Error: "disk full")));
        var result = harness.Step();

        Assert.Equal(LeaseCommandStatus.Failed, Assert.Single(result.CompletedCommands).Status);
        Assert.True(result.Snapshot.ShouldHold);
        Assert.Contains(result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
    }

    [Fact]
    public void A_release_that_succeeds_lets_the_machine_sleep_again()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        harness.Kernel.Execute(Create(harness, "req-2") with { Kind = LeaseCommandKind.Release });
        var release = Assert.Single(
            harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLeaseRelease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(release.EffectId, EffectOutcome.Succeeded)));

        harness.ConfirmAbsent("ssh");
        var result = harness.Step();

        Assert.Equal(LeaseCommandStatus.Released, Assert.Single(result.CompletedCommands).Status);
        Assert.False(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void A_retried_command_does_not_create_a_second_lease()
    {
        // The database is what decides this, in the same transaction as the lease itself. The kernel reports
        // the stored answer and drops the copy it made for the retry.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(
            effect.EffectId, EffectOutcome.AlreadyDone, ResultJson: """{"leaseId":"lease-1"}""")));
        var result = harness.Step();

        var answer = Assert.Single(result.CompletedCommands);
        Assert.Equal(LeaseCommandStatus.AlreadyDone, answer.Status);
        Assert.Equal("""{"leaseId":"lease-1"}""", answer.ResultJson);
        Assert.DoesNotContain(result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
    }

    [Fact]
    public void A_retried_release_lets_the_machine_sleep_instead_of_pinning_it_awake()
    {
        // Retrying is what the idempotency record exists to make safe, so this is reachable by design. The
        // database says the release already happened; if the lease were left waiting for that write it would go
        // on holding the machine awake -- and expiry deliberately skips a lease in that state, so the one
        // request whose whole purpose is to let the machine sleep would pin it awake for good.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        harness.Kernel.Execute(Create(harness, "req-2") with { Kind = LeaseCommandKind.Release });
        var release = Assert.Single(
            harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLeaseRelease);

        harness.Kernel.Apply(new EffectFinished(
            new EffectCompletion(release.EffectId, EffectOutcome.AlreadyDone)));

        harness.ConfirmAbsent("ssh");
        var result = harness.Step();

        Assert.Equal(LeaseCommandStatus.AlreadyDone, Assert.Single(result.CompletedCommands).Status);
        Assert.DoesNotContain(
            result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
        Assert.False(result.Snapshot.ShouldHold);
    }

    [Fact]
    public void The_same_request_identifier_used_for_different_content_is_refused()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(effect.EffectId, EffectOutcome.Conflict)));
        var result = harness.Step();

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(result.CompletedCommands).Status);
    }

    [Fact]
    public void A_command_that_sat_in_the_queue_past_its_deadline_is_not_carried_out()
    {
        // Nothing is changed, so the caller may safely retry with the same identifier.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        var command = Create(harness, deadlineIn: TimeSpan.FromSeconds(5));

        harness.Clock.Advance(TimeSpan.FromSeconds(6));
        harness.Kernel.Execute(command);
        var result = harness.Step();

        Assert.Equal(LeaseCommandStatus.DeadlineExpired, Assert.Single(result.CompletedCommands).Status);
        Assert.DoesNotContain(result.Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        Assert.DoesNotContain(result.Snapshot.Decision.Inhibitors, inhibitor => inhibitor.Kind == InhibitorKind.CliLease);
    }

    [Fact]
    public void A_deadline_measured_against_another_clock_counts_as_expired()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");

        harness.Kernel.Execute(Create(harness) with
        {
            Deadline = new MonotonicStamp(FakeClock.EpochB, TimeSpan.FromHours(1))
        });

        Assert.Equal(
            LeaseCommandStatus.DeadlineExpired,
            Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void An_answer_is_still_delivered_after_the_caller_has_given_up()
    {
        // A client that timed out and disconnected does not roll anything back. The change is durable and
        // its answer is available, which is what makes retrying with the same identifier safe.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded)));
        var result = harness.Step();

        var answer = Assert.Single(result.CompletedCommands);
        Assert.Equal(LeaseCommandStatus.Created, answer.Status);
        Assert.Equal("req-1", answer.RequestId);
    }

    [Fact]
    public void A_renewal_never_shortens_the_time_left()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness, duration: TimeSpan.FromHours(3)));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.Kernel.Execute(Create(harness, "req-2", duration: TimeSpan.FromHours(1)) with
        {
            Kind = LeaseCommandKind.Renew
        });

        var renewal = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        Assert.Equal(TimeSpan.FromMinutes(179), renewal.Lease!.RemainingAtCheckpoint);
    }

    [Theory]
    [InlineData(EffectOutcome.AlreadyDone)]
    [InlineData(EffectOutcome.Conflict)]
    public void A_replayed_or_conflicting_renewal_restores_the_committed_deadline(EffectOutcome outcome)
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness, duration: TimeSpan.FromMinutes(10)));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        harness.Kernel.Execute(Create(harness, "req-renew", duration: TimeSpan.FromMinutes(30)) with
        {
            Kind = LeaseCommandKind.Renew
        });
        var renewal = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(renewal.EffectId, outcome)));
        harness.Step();

        // The rejected replay must not restart a ten-minute lease as a fresh thirty-minute hold in memory.
        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.ConfirmAbsent("ssh");
        var ending = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        Assert.Equal(LeaseStatus.Expired, ending.Lease!.Status);
    }

    [Fact]
    public void A_renewal_longer_than_the_kernel_maximum_is_rejected()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        harness.Kernel.Execute(Create(harness, "req-renew", duration: TimeSpan.MaxValue) with
        {
            Kind = LeaseCommandKind.Renew
        });

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void A_lease_expires_on_its_own_and_stops_holding()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness, duration: TimeSpan.FromMinutes(10)));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));

        harness.ConfirmAbsent("ssh");
        Assert.True(harness.Step().Snapshot.ShouldHold);

        harness.Clock.Advance(TimeSpan.FromMinutes(11));
        harness.ConfirmAbsent("ssh");
        var expiring = harness.Step();

        // Expiry is a reduction in protection, so the expired lease remains an inhibitor until storage has
        // durably recorded the ending.
        Assert.True(expiring.Snapshot.ShouldHold);
        var ending = Assert.Single(expiring.Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(ending.EffectId, EffectOutcome.Succeeded)));
        harness.ConfirmAbsent("ssh");
        Assert.False(harness.Step().Snapshot.ShouldHold);
    }

    [Fact]
    public void Another_user_cannot_release_a_lease_but_an_administrator_can()
    {
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");
        harness.Kernel.Execute(Create(harness));
        var created = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);
        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(created.EffectId, EffectOutcome.Succeeded)));
        harness.Step();

        var stranger = new CallerSnapshot { Sid = "S-1-5-21-2", AccountName = "someone-else" };
        harness.Kernel.Execute(Create(harness, "req-2", caller: stranger) with { Kind = LeaseCommandKind.Release });
        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);

        harness.Kernel.Execute(Create(harness, "req-3", caller: Administrator) with
        {
            Kind = LeaseCommandKind.Release
        });
        Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLeaseRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_lease_must_be_created_with_an_identifier_so_a_retry_refers_to_the_same_one(string? leaseId)
    {
        var harness = new KernelHarness();
        harness.Kernel.Execute(Create(harness) with { LeaseId = leaseId });

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void A_lease_needs_a_positive_duration()
    {
        var harness = new KernelHarness();
        harness.Kernel.Execute(Create(harness, duration: TimeSpan.Zero));

        Assert.Equal(LeaseCommandStatus.Rejected, Assert.Single(harness.Step().CompletedCommands).Status);
    }

    [Fact]
    public void A_lease_longer_than_the_kernel_maximum_is_rejected_without_deadline_arithmetic()
    {
        var harness = new KernelHarness();
        harness.Kernel.Execute(Create(harness, duration: TimeSpan.MaxValue));

        var answer = Assert.Single(harness.Step().CompletedCommands);

        Assert.Equal(LeaseCommandStatus.Rejected, answer.Status);
        Assert.Contains("30 days", answer.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_expiry_saturates_when_the_wall_clock_is_near_its_maximum()
    {
        var harness = new KernelHarness();
        harness.Clock.UtcNow = DateTimeOffset.MaxValue.AddDays(-1);

        harness.Kernel.Execute(Create(harness, duration: InhibitKernel.MaximumLeaseDuration));
        var effect = Assert.Single(harness.Step().Effects, candidate => candidate.Kind == EffectKind.PersistLease);

        Assert.Equal(DateTimeOffset.MaxValue, effect.Lease!.ExpiresAtUtc);
    }

    [Fact]
    public void Renewing_or_releasing_something_that_does_not_exist_is_refused()
    {
        var harness = new KernelHarness();

        harness.Kernel.Execute(Create(harness, "req-1") with { Kind = LeaseCommandKind.Renew });
        harness.Kernel.Execute(Create(harness, "req-2") with { Kind = LeaseCommandKind.Release });

        Assert.All(harness.Step().CompletedCommands, answer => Assert.Equal(LeaseCommandStatus.Rejected, answer.Status));
    }

    [Fact]
    public void An_unknown_effect_completion_is_ignored()
    {
        // Arrives when the host reports the same effect twice, or reports one from before a restart. There
        // is nothing to undo either way.
        var harness = new KernelHarness();
        harness.ConfirmAbsent("ssh");

        harness.Kernel.Apply(new EffectFinished(new EffectCompletion(9999, EffectOutcome.Succeeded)));

        Assert.Empty(harness.Step().CompletedCommands);
    }
}

using PowerLease.Application;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

public sealed class SystemClockTests
{
    [Fact]
    public void Now_uses_a_stable_epoch_and_non_decreasing_elapsed_time_per_instance()
    {
        var clock = new SystemClock();
        var first = clock.Now;
        var second = clock.Now;

        Assert.Equal(first.EpochId, second.EpochId);
        Assert.True(second.Elapsed >= first.Elapsed, "Elapsed time must not decrease within an epoch.");
    }

    [Fact]
    public void Separate_instances_have_different_epochs()
    {
        var first = new SystemClock();
        var second = new SystemClock();

        Assert.NotEqual(first.Now.EpochId, second.Now.EpochId);
    }

    [Fact]
    public void UtcNow_has_a_zero_offset()
    {
        var clock = new SystemClock();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }

    [Fact]
    public async Task Beginning_a_new_epoch_changes_the_identifier_and_restarts_elapsed_time()
    {
        var clock = new SystemClock();
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        var before = clock.Now;

        clock.BeginNewEpoch();
        var after = clock.Now;

        Assert.NotEqual(before.EpochId, after.EpochId);
        Assert.True(after.Elapsed < before.Elapsed, "The new epoch must restart elapsed time near zero.");
    }

    [Fact]
    public async Task Readers_never_observe_an_epoch_identifier_with_the_other_epochs_elapsed_time()
    {
        var clock = new SystemClock();
        var stopping = false;
        string? failure = null;

        var reader = Task.Run(() =>
        {
            var previous = clock.Now;
            while (!Volatile.Read(ref stopping))
            {
                var current = clock.Now;
                if (current.EpochId == previous.EpochId && current.Elapsed < previous.Elapsed)
                {
                    failure = $"Elapsed time went backwards inside epoch {current.EpochId}.";
                    return;
                }

                previous = current;
            }
        }, TestContext.Current.CancellationToken);

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            clock.BeginNewEpoch();
        }

        Volatile.Write(ref stopping, true);
        await reader;

        Assert.Null(failure);
    }

    [Fact]
    public async Task A_real_clock_epoch_change_reestablishes_a_lease_instead_of_expiring_sleep_time()
    {
        var clock = new SystemClock();
        var inhibitor = new FakePowerInhibitor();
        var kernel = new InhibitKernel(Options(), new PowerInhibitCoordinator(inhibitor), clock);
        var now = clock.Now;
        kernel.Execute(new LeaseCommand
        {
            RequestId = "create",
            Caller = new CallerSnapshot { Sid = "S-1-5-21-1", AccountName = "liu" },
            Kind = LeaseCommandKind.Create,
            LeaseId = "lease-1",
            Duration = TimeSpan.FromMilliseconds(200),
            Deadline = new MonotonicStamp(now.EpochId, now.Elapsed + TimeSpan.FromSeconds(5)),
            PayloadHash = "create-hash"
        });
        var create = Assert.Single(kernel.Step().Effects, effect => effect.Kind == EffectKind.PersistLease);
        kernel.Apply(new EffectFinished(new EffectCompletion(create.EffectId, EffectOutcome.Succeeded)));
        kernel.Step();

        // QueryPerformanceCounter includes this interval on Windows. Starting a new epoch at resume keeps it
        // from consuming lease time while the machine was asleep.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        clock.BeginNewEpoch();
        kernel.Apply(new ResumedFromSleep("real clock test"));
        var resumed = kernel.Step();

        Assert.True(resumed.Snapshot.ShouldHold);
        Assert.DoesNotContain(
            resumed.Effects,
            effect => effect.Lease?.Status == LeaseStatus.Expired);
    }

    [Fact]
    public void A_real_clock_epoch_change_triggers_the_unannounced_resume_fallback()
    {
        var clock = new SystemClock();
        var inhibitor = new FakePowerInhibitor();
        var coordinator = new PowerInhibitCoordinator(inhibitor);
        var kernel = new InhibitKernel(Options(expectedSources: ["ssh"], resumeGracePeriod: TimeSpan.FromMinutes(1)), coordinator, clock);
        var observedAt = clock.Now;
        kernel.Apply(new SourceObservation(
            new SourceStamp("ssh", 1, 1, observedAt, 0),
            InhibitorSourceReport.Observed(
                "ssh",
                [new Inhibitor(InhibitorKind.SshSession, "SSH session", clock.UtcNow)])));
        kernel.Step();
        var heldBefore = coordinator.HeldGeneration;

        clock.BeginNewEpoch();
        var resumed = kernel.Step();

        Assert.NotNull(heldBefore);
        Assert.Contains(heldBefore.Value, inhibitor.Closed);
        Assert.NotEqual(heldBefore, coordinator.HeldGeneration);
        Assert.True(resumed.Snapshot.GracePeriodActive);
        Assert.All(resumed.Snapshot.Sources, source => Assert.False(source.Trusted));
    }

    private static KernelOptions Options(
        IReadOnlyList<string>? expectedSources = null,
        TimeSpan? resumeGracePeriod = null) => new()
        {
            ExpectedSources = expectedSources ?? [],
            CoveredKinds = [InhibitorKind.SshSession, InhibitorKind.CliLease],
            StartupGracePeriod = TimeSpan.Zero,
            ResumeGracePeriod = resumeGracePeriod ?? TimeSpan.Zero,
            LeaseCheckpointInterval = TimeSpan.Zero
        };
}

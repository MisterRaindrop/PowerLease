using PowerLease.Application.Hosting;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

/// <summary>
/// An executor whose outcome and behaviour the test chooses, including refusing to behave.
/// </summary>
internal sealed class FakeEffectExecutor : IEffectExecutor
{
    public List<KernelEffect> Executed { get; } = [];

    public EffectOutcome Outcome { get; set; } = EffectOutcome.Succeeded;

    public Exception? Throws { get; set; }

    public bool ObservesCancellation { get; set; }

    public Task<EffectCompletion> ExecuteAsync(KernelEffect effect, CancellationToken cancellationToken)
    {
        Executed.Add(effect);

        if (ObservesCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Throws is not null
            ? throw Throws
            : Task.FromResult(new EffectCompletion(effect.EffectId, Outcome));
    }
}

/// <summary>
/// The guarantees the kernel is written against but cannot enforce itself. Each one was named by the review as
/// a contract the host had to keep; they are kept here, where they can be tested in milliseconds, rather than
/// in the Windows service where only CI could ever look at them.
/// </summary>
public sealed class KernelLoopTests
{
    private static readonly CallerSnapshot Liu = new() { Sid = "S-1-5-21-1", AccountName = "liu" };

    private sealed record Harness(
        KernelHarness Kernel,
        FakeEffectExecutor Effects,
        FakeTimeZoneProvider TimeZones,
        KernelLoop Loop);

    private static Harness Build(int observationCapacity = 256)
    {
        var kernel = new KernelHarness();
        var effects = new FakeEffectExecutor();
        var zones = new FakeTimeZoneProvider(TimeZoneInfo.Utc);
        return new Harness(
            kernel,
            effects,
            zones,
            new KernelLoop(kernel.Kernel, effects, zones, observationCapacity));
    }

    private static LeaseCommand Create(Harness harness, string requestId = "req-1") => new()
    {
        RequestId = requestId,
        Caller = Liu,
        Kind = LeaseCommandKind.Create,
        LeaseId = "lease-1",
        Duration = TimeSpan.FromHours(1),
        Deadline = new MonotonicStamp(
            harness.Kernel.Clock.EpochId, harness.Kernel.Clock.Elapsed + TimeSpan.FromSeconds(10)),
        PayloadHash = "hash-a"
    };

    private static SourceObservation Absent(Harness harness, string sourceId = "ssh") =>
        new(harness.Kernel.NextStamp(sourceId), InhibitorSourceReport.ConfirmedAbsent(sourceId));

    [Fact]
    public async Task A_turn_takes_in_what_arrived_evaluates_and_carries_out_the_work()
    {
        var harness = Build();
        harness.Loop.Post(Absent(harness));
        harness.Loop.Post(Create(harness));

        var result = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Contains(harness.Effects.Executed, effect => effect.Kind == EffectKind.PersistLease);
        Assert.True(result.Snapshot.ShouldHold);

        // The answer arrives on the following turn, once the write has been reported back.
        var next = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LeaseCommandStatus.Created, Assert.Single(next.CompletedCommands).Status);
    }

    [Fact]
    public async Task An_executor_that_throws_still_produces_an_answer()
    {
        // An effect that is issued and never answered leaves the lease provisional and the caller waiting for
        // ever. Nothing an executor does may be allowed to strand one.
        var harness = Build();
        harness.Effects.Throws = new InvalidOperationException("the database is locked");
        harness.Loop.Post(Absent(harness));
        harness.Loop.Post(Create(harness));

        await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);
        harness.Loop.Post(Absent(harness));
        var result = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        var answer = Assert.Single(result.CompletedCommands);
        Assert.Equal(LeaseCommandStatus.Failed, answer.Status);
        Assert.Equal("the database is locked", answer.Error);

        // And the failure is latched, so the machine stays awake rather than losing protection to a write that
        // did not happen.
        Assert.True(result.Snapshot.ShouldHold);
    }

    [Fact]
    public async Task Shutting_down_mid_effect_still_produces_an_answer()
    {
        // Cancellation is reported as a failure rather than propagated, for the same reason: the loop has to
        // close the books on every effect it issued. A failure is the safe answer, because the kernel responds
        // to one by keeping protection.
        var harness = Build();
        harness.Effects.ObservesCancellation = true;
        harness.Loop.Post(Absent(harness));
        harness.Loop.Post(Create(harness));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await harness.Loop.PumpAsync(cancelled.Token);

        harness.Loop.Post(Absent(harness));
        var result = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        var answer = Assert.Single(result.CompletedCommands);
        Assert.Equal(LeaseCommandStatus.Failed, answer.Status);
        Assert.Contains("shutting down", answer.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_effect_is_answered_exactly_once()
    {
        var harness = Build();
        harness.Loop.Post(Absent(harness));
        harness.Loop.Post(Create(harness));
        await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        harness.Loop.Post(Absent(harness));
        var second = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Single(second.CompletedCommands);

        // Pumping again produces nothing further: the answer was delivered once, not on every turn.
        harness.Loop.Post(Absent(harness));
        Assert.Empty((await harness.Loop.PumpAsync(TestContext.Current.CancellationToken)).CompletedCommands);
    }

    [Fact]
    public async Task A_measurement_dropped_because_the_queue_was_full_stops_the_source_being_believed()
    {
        // The last of the kernel's safety rules that only the host can keep. A dropped report may have been the
        // one saying something was happening, and what is still held may be an older one saying nothing was --
        // so a full queue must never quietly become a claim that the machine is idle.
        var harness = Build(observationCapacity: 2);

        for (var i = 0; i < 10; i++)
        {
            harness.Loop.Post(Absent(harness));
        }

        var result = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Snapshot.ShouldHold);
        Assert.Contains(
            result.Snapshot.Decision.Inhibitors,
            inhibitor => inhibitor.Reason.Contains("were dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_is_believed_again_once_it_reports_without_being_dropped()
    {
        var harness = Build(observationCapacity: 2);
        for (var i = 0; i < 10; i++)
        {
            harness.Loop.Post(Absent(harness));
        }

        Assert.True((await harness.Loop.PumpAsync(TestContext.Current.CancellationToken)).Snapshot.ShouldHold);

        harness.Loop.Post(Absent(harness));

        Assert.False((await harness.Loop.PumpAsync(TestContext.Current.CancellationToken)).Snapshot.ShouldHold);
    }

    [Fact]
    public async Task Something_that_changes_what_the_kernel_believes_is_never_dropped()
    {
        // The control channel is unbounded on purpose. A measurement that is lost costs resolution; a resume
        // notification that is lost means the kernel goes on trusting samples taken before the machine slept.
        var harness = Build(observationCapacity: 1);
        harness.Loop.Post(Absent(harness));
        Assert.False((await harness.Loop.PumpAsync(TestContext.Current.CancellationToken)).Snapshot.ShouldHold);

        for (var i = 0; i < 500; i++)
        {
            harness.Loop.Post(new FaultObserved($"fault-{i}", FaultSeverity.Persistent, "something broke"));
        }

        var result = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(500, result.Snapshot.Faults.Count);
        Assert.True(result.Snapshot.ShouldHold);
    }

    [Fact]
    public async Task A_time_change_discards_the_cached_time_zone()
    {
        // The provider caches, so something has to tell it when to stop. Doing that here keeps a process-wide
        // side effect off the path that evaluates a schedule every few seconds.
        var harness = Build();
        Assert.Equal(0, harness.TimeZones.RefreshCount);

        harness.Loop.Post(new TimeAdjusted("the time zone changed"));
        await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.TimeZones.RefreshCount);
    }

    [Fact]
    public async Task Ordinary_messages_do_not_touch_the_time_zone_cache()
    {
        var harness = Build();

        harness.Loop.Post(new ResumedFromSleep("power event"));
        harness.Loop.Post(new FaultObserved("config", FaultSeverity.Persistent, "broken"));
        await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, harness.TimeZones.RefreshCount);
    }

    [Fact]
    public async Task What_changes_belief_is_taken_in_before_what_merely_measures()
    {
        // A resume invalidates every measurement. Applying the measurements first would let a report taken
        // before the machine slept be believed for one turn.
        var harness = Build();
        harness.Loop.Post(Absent(harness));
        harness.Loop.Post(new ResumedFromSleep("power event"));

        var result = await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Snapshot.ShouldHold);
        Assert.True(result.Snapshot.GracePeriodActive);
    }

    [Fact]
    public async Task The_published_state_is_reachable_without_pumping()
    {
        var harness = Build();
        harness.Loop.Post(Absent(harness));
        await harness.Loop.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Same(harness.Kernel.Kernel.Snapshot, harness.Loop.Snapshot);
    }

    [Fact]
    public void The_emergency_switch_does_not_go_through_the_queue()
    {
        var harness = Build();

        Assert.True(harness.Loop.RaiseEmergencyInhibit("a source saw something alarming"));

        Assert.True(harness.Kernel.Kernel.EmergencyInhibitRaised);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        var harness = Build();

        Assert.Throws<ArgumentNullException>(
            () => new KernelLoop(null!, harness.Effects, harness.TimeZones));
        Assert.Throws<ArgumentNullException>(
            () => new KernelLoop(harness.Kernel.Kernel, null!, harness.TimeZones));
        Assert.Throws<ArgumentNullException>(
            () => new KernelLoop(harness.Kernel.Kernel, harness.Effects, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KernelLoop(harness.Kernel.Kernel, harness.Effects, harness.TimeZones, 0));

        Assert.Throws<ArgumentNullException>(() => harness.Loop.Post((SourceObservation)null!));
        Assert.Throws<ArgumentNullException>(() => harness.Loop.Post((ControlMessage)null!));
        Assert.Throws<ArgumentNullException>(() => harness.Loop.Post((LeaseCommand)null!));
    }
}

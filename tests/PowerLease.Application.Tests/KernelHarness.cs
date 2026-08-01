using PowerLease.Application.Kernel;
using PowerLease.Domain;

namespace PowerLease.Application.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock : IClock
{
    public static readonly Guid EpochA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    public static readonly Guid EpochB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    public TimeSpan Elapsed { get; set; }

    public Guid EpochId { get; set; } = EpochA;

    public MonotonicStamp Now => new(EpochId, Elapsed);

    public void Advance(TimeSpan amount)
    {
        UtcNow = UtcNow.Add(amount);
        Elapsed += amount;
    }

    /// <summary>What a resume looks like: a new monotonic origin, so nothing measured before is comparable.</summary>
    public void BeginNewEpoch()
    {
        EpochId = EpochId == EpochA ? EpochB : EpochA;
        Elapsed = TimeSpan.Zero;
    }
}

/// <summary>
/// A stand-in for the Windows power request, with programmable outcomes and a record of every call.
/// Hand written rather than generated: the whole point is to see exactly which generation was acquired
/// and which was closed.
/// </summary>
internal sealed class FakePowerInhibitor : IPowerInhibitor
{
    private readonly Queue<PowerInhibitResult> _scripted = new();

    public PowerInhibitResult Default { get; set; } = PowerInhibitResult.Held;

    public List<long> Acquired { get; } = [];

    public List<long> Closed { get; } = [];

    /// <summary>
    /// Raises the kernel's emergency latch from inside the next Close, which is the only way to land in the
    /// window between the decision being taken and the request being let go.
    /// </summary>
    public InhibitKernel? RaiseBeforeNextClose { get; set; }

    /// <summary>Queue one outcome for the next acquisition, ahead of <see cref="Default" />.</summary>
    public void Next(PowerInhibitResult result) => _scripted.Enqueue(result);

    public PowerInhibitResult Acquire(long generation)
    {
        Acquired.Add(generation);
        return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
    }

    public void Close(long generation)
    {
        Closed.Add(generation);

        if (RaiseBeforeNextClose is { } kernel)
        {
            RaiseBeforeNextClose = null;
            kernel.RaiseEmergencyInhibit("a source saw something alarming");
        }
    }
}

/// <summary>
/// Drives a kernel the way the loop would, keeping the per-source sequence numbering that the kernel's
/// input rules are written against.
/// </summary>
internal sealed class KernelHarness
{
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);

    public KernelHarness(KernelOptions? options = null)
    {
        // No startup grace by default. The kernel notices its own first evaluation and holds unconditionally for
        // fifteen minutes, which is right for a service and unhelpful for a test that is about something else.
        // The tests that are about the grace period ask for one.
        Options = options ?? new KernelOptions
        {
            ObservationFreshness = TimeSpan.FromSeconds(30),
            HeartbeatFreshness = TimeSpan.FromSeconds(60),
            StartupGracePeriod = TimeSpan.Zero,
            ExpectedSources = ["ssh"],
            CoveredKinds = [InhibitorKind.SshSession, InhibitorKind.CliLease]
        };

        Inhibitor = new FakePowerInhibitor();
        Coordinator = new PowerInhibitCoordinator(Inhibitor);
        Kernel = new InhibitKernel(Options, Coordinator, Clock);
    }

    public FakeClock Clock { get; } = new();

    public KernelOptions Options { get; }

    public FakePowerInhibitor Inhibitor { get; }

    public PowerInhibitCoordinator Coordinator { get; }

    public InhibitKernel Kernel { get; }

    public long ConfigGeneration { get; private set; }

    public Inhibitor SshInhibitor(string detail = "10.0.0.5") =>
        new(InhibitorKind.SshSession, "SSH session", Clock.UtcNow, detail);

    /// <summary>Deliver a report with the next contiguous sequence number for that source.</summary>
    public ObservationDisposition Report(string sourceId, InhibitorSourceReport report) =>
        Deliver(NextStamp(sourceId), report);

    public ObservationDisposition ConfirmAbsent(string sourceId) =>
        Report(sourceId, InhibitorSourceReport.ConfirmedAbsent(sourceId));

    public ObservationDisposition Observe(string sourceId, params Inhibitor[] inhibitors) =>
        Report(sourceId, InhibitorSourceReport.Observed(sourceId, inhibitors));

    public ObservationDisposition Deliver(SourceStamp stamp, InhibitorSourceReport report) =>
        Kernel.Apply(new SourceObservation(stamp, report));

    /// <summary>The stamp the next contiguous report from that source would carry.</summary>
    public SourceStamp NextStamp(string sourceId)
    {
        var sequence = _sequences.GetValueOrDefault(sourceId) + 1;
        _sequences[sourceId] = sequence;
        return StampAt(sourceId, sequence);
    }

    /// <summary>Build a stamp by hand, for the tests about what happens when one is wrong.</summary>
    public SourceStamp StampAt(
        string sourceId,
        long sequence,
        long? sourceGeneration = null,
        Guid? epoch = null,
        long? configGeneration = null) =>
        new(
            sourceId,
            sourceGeneration ?? _generations.GetValueOrDefault(sourceId, 1),
            sequence,
            new MonotonicStamp(epoch ?? Clock.EpochId, Clock.Elapsed),
            configGeneration ?? ConfigGeneration);

    /// <summary>Restart a source: its sequence begins again under a new generation.</summary>
    public void RestartSource(string sourceId)
    {
        _generations[sourceId] = _generations.GetValueOrDefault(sourceId, 1) + 1;
        _sequences[sourceId] = 0;
    }

    /// <param name="expectedSources">
    /// What the host will run from now on. Defaults to what it ran before, so a test that only cares about the
    /// generation does not have to restate it.
    /// </param>
    public void ReplaceConfiguration(IReadOnlyList<string>? expectedSources = null)
    {
        ConfigGeneration++;
        Kernel.Apply(new ConfigurationReplaced(ConfigGeneration, expectedSources ?? Options.ExpectedSources));
    }

    public KernelStepResult Step() => Kernel.Step();

    /// <summary>Every expected source confirms there is nothing to hold for, then evaluate.</summary>
    public KernelStepResult AllQuiet()
    {
        foreach (var source in Options.ExpectedSources)
        {
            ConfirmAbsent(source);
        }

        return Step();
    }
}

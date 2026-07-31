using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

/// <summary>How much a source is currently worth believing.</summary>
public sealed record SourceState(
    string SourceId,
    ObservationDisposition LastDisposition,
    bool Trusted,
    string? Detail);

/// <summary>
/// The kernel's decision, published as one immutable value.
/// <para>
/// Readers take the whole thing at once and never touch the kernel's own state, which is what lets
/// <c>powerlease status</c> answer while the loop is busy without waiting behind the queue that changes
/// safety state.
/// </para>
/// <para>
/// It carries the boundary of what protection covers, not just the answer: which inhibitor kinds nobody
/// is evaluating, which sources are not being believed and why, and whether the system is actually
/// honouring the request. A tool that says "protected" while the power request is being ignored is worse
/// than one that says nothing.
/// </para>
/// </summary>
public sealed class KernelSnapshot
{
    public static KernelSnapshot Initial { get; } = new(
        revision: 0,
        protectionState: ProtectionState.Released,
        decision: new InhibitDecision([], []),
        faults: [],
        unhealthySources: [],
        sources: [],
        emergencyInhibitRaised: false,
        emergencyInhibitReason: null,
        gracePeriodActive: false,
        configGeneration: 0,
        historyWriteFailures: 0,
        lastHistoryWriteError: null);

    public KernelSnapshot(
        long revision,
        ProtectionState protectionState,
        InhibitDecision decision,
        IReadOnlyList<Fault> faults,
        IReadOnlyList<string> unhealthySources,
        IReadOnlyList<SourceState> sources,
        bool emergencyInhibitRaised,
        string? emergencyInhibitReason,
        bool gracePeriodActive,
        long configGeneration,
        long historyWriteFailures,
        string? lastHistoryWriteError)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(unhealthySources);
        ArgumentNullException.ThrowIfNull(sources);

        Revision = revision;
        ProtectionState = protectionState;
        Decision = decision;
        Faults = faults;
        UnhealthySources = unhealthySources;
        Sources = sources;
        EmergencyInhibitRaised = emergencyInhibitRaised;
        EmergencyInhibitReason = emergencyInhibitReason;
        GracePeriodActive = gracePeriodActive;
        ConfigGeneration = configGeneration;
        HistoryWriteFailures = historyWriteFailures;
        LastHistoryWriteError = lastHistoryWriteError;
    }

    /// <summary>Increases with every evaluation. Only the kernel ever moves it.</summary>
    public long Revision { get; }

    public ProtectionState ProtectionState { get; }

    public InhibitDecision Decision { get; }

    public IReadOnlyList<Fault> Faults { get; }

    public IReadOnlyList<string> UnhealthySources { get; }

    public IReadOnlyList<SourceState> Sources { get; }

    public bool EmergencyInhibitRaised { get; }

    public string? EmergencyInhibitReason { get; }

    public bool GracePeriodActive { get; }

    public long ConfigGeneration { get; }

    /// <summary>
    /// How many times the protection history could not be written.
    /// <para>
    /// Reported rather than latched as a fault. Losing the record of past decisions does not make the current
    /// decision untrustworthy, so it must not hold the machine awake -- but it must be visible, or a database
    /// that has been failing to write for a month looks exactly like one that has had nothing to say.
    /// </para>
    /// </summary>
    public long HistoryWriteFailures { get; }

    public string? LastHistoryWriteError { get; }

    public bool ShouldHold => Decision.ShouldHold;

    /// <summary>
    /// True when the kernel wants to hold the machine awake and the system is not honouring it. The one
    /// condition that must make <c>powerlease status</c> exit non-zero.
    /// </summary>
    public bool IsProtectionFailing => ProtectionState == ProtectionState.Unprotected;
}

namespace PowerLease.Ipc.Contracts;

/// <summary>How the machine is being held, or why it is not.</summary>
public enum ReportedProtectionState
{
    Released,
    Protected,

    /// <summary>Protection is wanted and the system will not honour it.</summary>
    Unprotected
}

/// <summary>One reason the machine is being kept awake.</summary>
public sealed record ReportedInhibitor(string Kind, string Reason, DateTimeOffset SinceUtc, string? Detail);

/// <summary>What is known about one source, including whether it is being believed.</summary>
public sealed record ReportedSource(string SourceId, bool Trusted, string? Detail);

/// <summary>
/// The answer to <c>powerlease status</c>.
/// <para>
/// It carries the boundary of what protection covers, not just the answer. A tool that says "protected" while
/// the system is ignoring the power request, or while a rule the user believes is on is not being evaluated by
/// anyone, is worse than one that says nothing: it converts a problem into confidence.
/// </para>
/// </summary>
public sealed record StatusResponse
{
    public required long Revision { get; init; }

    public required ReportedProtectionState ProtectionState { get; init; }

    public required bool ShouldHold { get; init; }

    /// <summary>Every reason the machine is being kept awake, in a stable order.</summary>
    public IReadOnlyList<ReportedInhibitor> Inhibitors { get; init; } = [];

    /// <summary>
    /// Inhibitor kinds nobody is evaluating, because the rule is off or the capability is missing. Shown so the
    /// user can see what protection does not cover.
    /// </summary>
    public IReadOnlyList<string> UncoveredKinds { get; init; } = [];

    /// <summary>Sources that are not currently believed, and why.</summary>
    public IReadOnlyList<ReportedSource> Sources { get; init; } = [];

    public IReadOnlyList<string> UnhealthySources { get; init; } = [];

    public IReadOnlyList<string> Faults { get; init; } = [];

    public bool EmergencyInhibitRaised { get; init; }

    public string? EmergencyInhibitReason { get; init; }

    public bool GracePeriodActive { get; init; }

    /// <summary>How many times the protection history could not be written. Not a reason to stay awake.</summary>
    public long HistoryWriteFailures { get; init; }

    /// <summary>
    /// True when the machine may sleep while someone is using it, because the system is refusing the request.
    /// This is what makes <c>powerlease status</c> exit non-zero.
    /// </summary>
    public bool IsProtectionFailing => ProtectionState == ReportedProtectionState.Unprotected;
}

/// <summary>One hold, as <c>powerlease list</c> shows it.</summary>
public sealed record ReportedLease(
    string Id,
    string Source,
    string? Reason,
    string? OwnerUser,
    DateTimeOffset StartedAtUtc,
    TimeSpan Remaining,
    string Status);

public sealed record ListLeasesResponse
{
    public IReadOnlyList<ReportedLease> Leases { get; init; } = [];
}

/// <summary>
/// The answer to <c>powerlease wake-status</c>: what the machine's power configuration allows.
/// <para>
/// Diagnostic only. This version schedules no wake-ups; it reports what it can see so that a machine which
/// cannot honour a keep-awake request, or cannot be woken, says so rather than being silently ineffective.
/// </para>
/// </summary>
public sealed record WakeStatusResponse
{
    /// <summary>Whether the active power plan lets a program keep the computer awake, on mains and on battery.</summary>
    public bool? SystemRequiredHonouredOnMains { get; init; }

    public bool? SystemRequiredHonouredOnBattery { get; init; }

    /// <summary>Whether the machine uses modern standby, where a request can be terminated while on battery.</summary>
    public bool? ModernStandby { get; init; }

    public bool? RunningOnBattery { get; init; }

    public bool? WakeTimersAllowed { get; init; }

    /// <summary>What could not be determined, so that a blank is never read as a no.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];
}

/// <summary>What a request that changed something reports back.</summary>
public sealed record CommandResponse(string Status, string? LeaseId = null, string? Error = null);

/// <summary>The envelope every answer is wrapped in.</summary>
public sealed record ResponseEnvelope(
    int ProtocolVersion,
    Guid RequestId,
    bool Accepted,
    string? PayloadJson = null,
    string? Error = null)
{
    public static ResponseEnvelope Refused(Guid requestId, string error) =>
        new(IpcProtocol.Version, requestId, Accepted: false, PayloadJson: null, error);
}

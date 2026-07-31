using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

/// <summary>What kind of durable change an effect performs.</summary>
public enum EffectKind
{
    /// <summary>
    /// Write a lease, together with the record that its command was carried out, in one transaction.
    /// </summary>
    PersistLease,

    /// <summary>Make a lease's ending durable. Protection is not lifted until this succeeds.</summary>
    PersistLeaseRelease,

    /// <summary>Record that protection was taken out, given up, or refused.</summary>
    RecordInhibitChange
}

/// <summary>
/// Work the kernel needs done that would block if it did it itself.
/// <para>
/// The kernel loop is the only writer of the safety state, so it must never wait on a disk. It emits
/// these instead and is told the outcome later, which is what keeps a slow or wedged database from
/// stalling the decision about whether to keep the machine awake.
/// </para>
/// </summary>
public sealed record KernelEffect
{
    public required long EffectId { get; init; }

    public required EffectKind Kind { get; init; }

    public string? RequestId { get; init; }

    public CallerSnapshot? Caller { get; init; }

    public string? PayloadHash { get; init; }

    public KeepAwakeLease? Lease { get; init; }

    /// <summary>The protection change to record, for <see cref="EffectKind.RecordInhibitChange" />.</summary>
    public ProtectionState ProtectionState { get; init; }

    public IReadOnlyList<InhibitorKind> InhibitorKinds { get; init; } = [];

    public string? Reason { get; init; }

    public long Revision { get; init; }
}

public enum EffectOutcome
{
    Succeeded,

    /// <summary>
    /// The database already held a completed record for this request identifier. This is a client
    /// retrying, not a second request, so nothing further must be done.
    /// </summary>
    AlreadyDone,

    /// <summary>The identifier had been used before with different content.</summary>
    Conflict,

    Failed
}

public sealed record EffectCompletion(
    long EffectId,
    EffectOutcome Outcome,
    string? ResultJson = null,
    string? Error = null);

using PowerLease.Domain;

namespace PowerLease.Persistence.History;

/// <summary>What happened to a keep-awake change.</summary>
public enum InhibitChange
{
    /// <summary>Protection was taken out.</summary>
    Established,

    /// <summary>Protection was given up because every source confirmed there was no reason to hold.</summary>
    Released,

    /// <summary>Protection was asked for and the system would not honour it.</summary>
    Rejected
}

/// <summary>
/// A change in whether the machine is being held awake.
/// <para>
/// Kept in its own table rather than among the power events, because holding a machine awake is not a
/// power transition and mixing the two is how a history ends up implying this version puts machines
/// to sleep.
/// </para>
/// </summary>
public sealed record InhibitEvent
{
    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required InhibitChange Change { get; init; }

    public required ProtectionState ProtectionState { get; init; }

    /// <summary>Why the machine was being held, in the order the aggregator produced.</summary>
    public IReadOnlyList<InhibitorKind> InhibitorKinds { get; init; } = [];

    public string? Reason { get; init; }

    /// <summary>The kernel revision this change belongs to, so events can be ordered exactly.</summary>
    public required long Revision { get; init; }
}

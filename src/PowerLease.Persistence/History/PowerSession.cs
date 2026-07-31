namespace PowerLease.Persistence.History;

/// <summary>
/// One continuous run of the machine being awake, identified so that samples can be attributed to it.
/// A new session begins at boot and after each resume.
/// </summary>
public sealed record PowerSession
{
    public required string Id { get; init; }

    /// <summary>Identifies the boot this session belongs to, so sessions from before a reboot are distinguishable.</summary>
    public string? BootId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? EndedAtUtc { get; init; }

    public string? StartReason { get; init; }

    public string? EndReason { get; init; }

    public double? EnergyWh { get; init; }

    public string? EnergySource { get; init; }

    /// <summary>
    /// Whether the energy figure was estimated rather than measured. Never presented as a reading when
    /// this is set.
    /// </summary>
    public bool IsEstimated { get; init; }
}

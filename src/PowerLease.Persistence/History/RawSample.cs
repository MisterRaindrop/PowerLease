using PowerLease.Domain;

namespace PowerLease.Persistence.History;

/// <summary>
/// One measurement of the machine.
/// <para>
/// Only the metrics this version actually reads are modelled. The table has columns for temperature,
/// fan speed, graphics load and power draw as well, and they stay null until the monitoring that
/// produces them exists; a model claiming to carry them would invite code that reads zeros as
/// readings.
/// </para>
/// </summary>
public sealed record RawSample
{
    public required string PowerSessionId { get; init; }

    public required DateTimeOffset SampledAtUtc { get; init; }

    public double? CpuPercent { get; init; }

    public double? MemoryPercent { get; init; }

    public double? DiskReadBytesPerSecond { get; init; }

    public double? DiskWriteBytesPerSecond { get; init; }

    public double? NetworkRxBytesPerSecond { get; init; }

    public double? NetworkTxBytesPerSecond { get; init; }

    /// <summary>Whether the machine was being held awake when this was taken.</summary>
    public required ProtectionState State { get; init; }

    public required int SshSessionCount { get; init; }

    public SampleQuality QualityFlags { get; init; }
}

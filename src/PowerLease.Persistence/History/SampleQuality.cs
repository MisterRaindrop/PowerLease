namespace PowerLease.Persistence.History;

/// <summary>
/// What was wrong with a sample. Recorded so that a gap in the history can be told apart from a
/// genuinely quiet machine after the fact.
/// </summary>
[Flags]
public enum SampleQuality
{
    None = 0,
    CpuUnavailable = 1,
    MemoryUnavailable = 2,
    DiskUnavailable = 4,
    NetworkUnavailable = 8,

    /// <summary>A value was estimated rather than measured, so it must not be presented as a reading.</summary>
    Estimated = 16
}

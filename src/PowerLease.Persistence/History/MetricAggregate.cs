namespace PowerLease.Persistence.History;

/// <summary>
/// One time bucket of history.
/// <para>
/// <see cref="BucketStartUtc" /> is the whole identity of a bucket, which is what lets aggregation be
/// run again over the same period without producing duplicates.
/// </para>
/// </summary>
public sealed record MetricAggregate
{
    public required DateTimeOffset BucketStartUtc { get; init; }

    /// <summary>How many measurements the bucket was built from.</summary>
    public required int SampleCount { get; init; }

    /// <summary>
    /// How many were expected at the configured sampling interval. Kept alongside the actual count so
    /// a bucket built from two samples is not read as if it were built from six.
    /// </summary>
    public required int ExpectedSampleCount { get; init; }

    /// <summary>
    /// The fraction of expected measurements that arrived, capped at one. Sampling can overshoot
    /// slightly, and a completeness above one would read as a data error rather than as timing jitter.
    /// </summary>
    public double Completeness => ExpectedSampleCount <= 0
        ? 0
        : Math.Min(1, SampleCount / (double)ExpectedSampleCount);

    public double? CpuAverage { get; init; }

    public double? CpuMinimum { get; init; }

    public double? CpuMaximum { get; init; }

    public double? MemoryAverage { get; init; }

    public double? DiskReadAverage { get; init; }

    public double? DiskReadMaximum { get; init; }

    public double? DiskWriteAverage { get; init; }

    public double? DiskWriteMaximum { get; init; }

    public double? NetworkRxAverage { get; init; }

    public double? NetworkRxMaximum { get; init; }

    public double? NetworkTxAverage { get; init; }

    public double? NetworkTxMaximum { get; init; }

    public int SshSessionMaximum { get; init; }

    /// <summary>How long the machine was being held awake during the bucket.</summary>
    public TimeSpan ProtectedFor { get; init; }

    public TimeSpan ReleasedFor { get; init; }

    /// <summary>
    /// How long protection was wanted but the system would not honour it. The most important number in
    /// the history: it is the machine having been at risk of sleeping while in use.
    /// </summary>
    public TimeSpan UnprotectedFor { get; init; }
}

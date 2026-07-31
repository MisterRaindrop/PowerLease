namespace PowerLease.Persistence.History;

/// <summary>
/// Rolls measurements up into minute buckets.
/// <para>
/// The buckets and the record of how far aggregation has got are written in one transaction. That is
/// the whole safety argument: a crash either leaves both or neither, so the next run rebuilds the same
/// buckets over the same period, and because a bucket is keyed on its start time rebuilding replaces
/// rather than duplicates. Advancing the record separately would let a crash carry it past measurements
/// that were never aggregated, and retention would then delete the only copy.
/// </para>
/// </summary>
public sealed class MinuteAggregator
{
    public const string WatermarkName = "minute";

    private readonly SqliteHistoryStore _store;
    private readonly TimeSpan _sampleInterval;

    public MinuteAggregator(SqliteHistoryStore store, TimeSpan sampleInterval)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleInterval, TimeSpan.Zero);

        _store = store;
        _sampleInterval = sampleInterval;
    }

    /// <summary>
    /// Aggregate every minute that has finished.
    /// </summary>
    public AggregationResult Run(DateTimeOffset nowUtc)
    {
        var watermark = _store.GetWatermark(WatermarkName);

        // The minute in progress is deliberately left alone. Aggregating it would produce a bucket
        // holding part of a minute and then move past it, and the measurements still to arrive would
        // have nowhere to go.
        var to = TimeBuckets.FloorToMinute(nowUtc);

        var from = watermark ?? EarliestBucket(nowUtc);
        if (to <= from)
        {
            // Either nothing new has finished, or the clock moved backwards. Both mean do nothing; the
            // record of progress is never moved back.
            return new AggregationResult(0, watermark);
        }

        var buckets = _store.RollUpRawSamplesByMinute(from, to, _sampleInterval);

        using var transaction = _store.BeginTransaction();
        foreach (var bucket in buckets)
        {
            transaction.UpsertMinuteAggregate(bucket);
        }

        transaction.SetWatermark(WatermarkName, to);
        transaction.Commit();

        return new AggregationResult(buckets.Count, to);
    }

    private DateTimeOffset EarliestBucket(DateTimeOffset nowUtc) =>
        TimeBuckets.FloorToMinute(_store.EarliestRawSampleUtc() ?? nowUtc);
}

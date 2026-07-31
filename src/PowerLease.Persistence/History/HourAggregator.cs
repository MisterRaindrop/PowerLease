namespace PowerLease.Persistence.History;

/// <summary>
/// Rolls minute buckets up into hour buckets.
/// <para>
/// An hour is only aggregated once every minute in it has been. The bound comes from how far minute
/// aggregation has got, not from the clock, so an hour can never be summarised from minutes that are
/// still missing -- which would produce an hour that looks complete and is not, and would then let
/// retention delete the minutes that would have corrected it.
/// </para>
/// </summary>
public sealed class HourAggregator
{
    public const string WatermarkName = "hour";

    private readonly SqliteHistoryStore _store;

    public HourAggregator(SqliteHistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public AggregationResult Run()
    {
        var watermark = _store.GetWatermark(WatermarkName);

        var minuteProgress = _store.GetWatermark(MinuteAggregator.WatermarkName);
        if (minuteProgress is not { } minutesCompleteThrough)
        {
            // Minute aggregation has never run, so no hour is fully covered.
            return new AggregationResult(0, watermark);
        }

        var to = TimeBuckets.FloorToHour(minutesCompleteThrough);
        var from = watermark ?? EarliestBucket(to);
        if (to <= from)
        {
            return new AggregationResult(0, watermark);
        }

        var buckets = _store.RollUpMinuteAggregatesByHour(from, to);

        using var transaction = _store.BeginTransaction();
        foreach (var bucket in buckets)
        {
            transaction.UpsertHourAggregate(bucket);
        }

        transaction.SetWatermark(WatermarkName, to);
        transaction.Commit();

        return new AggregationResult(buckets.Count, to);
    }

    private DateTimeOffset EarliestBucket(DateTimeOffset fallback) =>
        TimeBuckets.FloorToHour(_store.EarliestMinuteBucketUtc() ?? fallback);
}

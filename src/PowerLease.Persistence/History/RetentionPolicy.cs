using PowerLease.Persistence.Configuration;

namespace PowerLease.Persistence.History;

/// <summary>What a retention pass deleted.</summary>
public sealed record RetentionResult(
    int RawSamplesDeleted,
    int MinuteAggregatesDeleted,
    int EventsDeleted,
    DateTimeOffset RawCutoffUtc,
    DateTimeOffset MinuteCutoffUtc);

/// <summary>
/// Deletes history that has aged out.
/// <para>
/// Age alone never decides. Measurements are only deleted up to the point minute aggregation has
/// reached, and minute buckets only up to the point hour aggregation has reached, so data is never
/// discarded before the summary built from it exists. Hour buckets are kept indefinitely: they are the
/// long-term record and are small.
/// </para>
/// </summary>
public sealed class RetentionPolicy
{
    private readonly SqliteHistoryStore _store;
    private readonly RetentionOptions _options;

    public RetentionPolicy(SqliteHistoryStore store, RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _options = options;
    }

    public RetentionResult Apply(DateTimeOffset nowUtc)
    {
        // Whichever is earlier: what age allows, or what aggregation has actually finished.
        var rawCutoff = Earlier(
            nowUtc - TimeSpan.FromHours(_options.RawHours),
            _store.GetWatermark(MinuteAggregator.WatermarkName));

        var minuteCutoff = Earlier(
            nowUtc - TimeSpan.FromDays(_options.MinuteDays),
            _store.GetWatermark(HourAggregator.WatermarkName));

        var eventCutoff = nowUtc - TimeSpan.FromDays(_options.EventDays);

        using var transaction = _store.BeginTransaction();

        var rawDeleted = transaction.DeleteRawSamplesBefore(rawCutoff);
        var minutesDeleted = transaction.DeleteMinuteAggregatesBefore(minuteCutoff);

        var eventsDeleted = transaction.DeleteInhibitEventsBefore(eventCutoff)
            + transaction.DeletePowerEventsBefore(eventCutoff)
            + transaction.DeleteAuditEventsBefore(eventCutoff);

        transaction.Commit();

        return new RetentionResult(rawDeleted, minutesDeleted, eventsDeleted, rawCutoff, minuteCutoff);
    }

    /// <summary>
    /// The earlier of the age limit and how far aggregation has got. A missing watermark means
    /// aggregation has never run, so nothing may be deleted at all.
    /// </summary>
    private static DateTimeOffset Earlier(DateTimeOffset ageLimit, DateTimeOffset? aggregatedThrough)
    {
        if (aggregatedThrough is not { } progress)
        {
            return DateTimeOffset.MinValue;
        }

        return progress < ageLimit ? progress : ageLimit;
    }
}

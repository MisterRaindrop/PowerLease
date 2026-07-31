namespace PowerLease.Persistence.History;

/// <summary>
/// Time bucket arithmetic, and the bridge between a bucket and the text key SQL groups on.
/// <para>
/// Because timestamps are stored in a fixed-width UTC format, the first sixteen characters are the
/// minute a row belongs to and the first thirteen are the hour. Grouping on that prefix lets the
/// database do the bucketing, so aggregation costs one query rather than one per bucket -- and a
/// service that has been down for a week does not have to walk ten thousand empty minutes.
/// </para>
/// </summary>
public static class TimeBuckets
{
    internal const int MinuteKeyLength = 16;

    internal const int HourKeyLength = 13;

    public static DateTimeOffset FloorToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, TimeSpan.Zero);

    public static DateTimeOffset FloorToHour(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);

    internal static DateTimeOffset ParseMinuteKey(string key) => Sqlite.Timestamps.Parse(key + ":00.0000000Z");

    internal static DateTimeOffset ParseHourKey(string key) => Sqlite.Timestamps.Parse(key + ":00:00.0000000Z");
}

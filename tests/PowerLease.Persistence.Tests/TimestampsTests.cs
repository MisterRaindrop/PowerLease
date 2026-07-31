using PowerLease.Persistence.Sqlite;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class TimestampsTests
{
    [Fact]
    public void A_timestamp_survives_the_round_trip()
    {
        var value = new DateTimeOffset(2026, 7, 31, 12, 34, 56, 789, TimeSpan.Zero).AddTicks(1234);

        Assert.Equal(value, Timestamps.Parse(Timestamps.ToText(value)));
    }

    [Fact]
    public void A_local_time_is_stored_as_the_same_instant_in_utc()
    {
        // Storing local time would sort wrongly against every other row and shift twice a year.
        var local = new DateTimeOffset(2026, 7, 31, 20, 0, 0, TimeSpan.FromHours(8));

        Assert.Equal("2026-07-31T12:00:00.0000000Z", Timestamps.ToText(local));
        Assert.Equal(local, Timestamps.Parse(Timestamps.ToText(local)));
    }

    [Fact]
    public void Text_order_matches_time_order()
    {
        // SQL compares these as strings, so the format has to sort the same way time does.
        var earlier = Timestamps.ToText(new DateTimeOffset(2026, 7, 31, 9, 59, 59, TimeSpan.Zero));
        var later = Timestamps.ToText(new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero));
        var nextYear = Timestamps.ToText(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.True(string.CompareOrdinal(later, nextYear) < 0);
    }

    [Fact]
    public void Optional_timestamps_pass_null_through()
    {
        Assert.Null(Timestamps.ToTextOrNull(null));
        Assert.Null(Timestamps.ParseOrNull(null));
        Assert.Null(Timestamps.ParseOrNull(string.Empty));

        var value = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(value, Timestamps.ParseOrNull(Timestamps.ToTextOrNull(value)));
    }

    [Fact]
    public void Text_that_is_not_a_timestamp_is_rejected()
    {
        Assert.Throws<FormatException>(() => Timestamps.Parse("2026-07-31 12:00:00"));
        Assert.Throws<ArgumentException>(() => Timestamps.Parse(string.Empty));
    }
}

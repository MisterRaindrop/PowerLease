using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class TimeWindowTests
{
    [Fact]
    public void An_ordinary_window_does_not_cross_midnight()
    {
        var window = new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.Weekdays);

        Assert.False(window.CrossesMidnight);
        Assert.Equal(new TimeOnly(9, 0), window.Start);
        Assert.Equal(new TimeOnly(18, 0), window.End);
        Assert.Equal(DayOfWeekSet.Weekdays, window.Days);
    }

    [Fact]
    public void A_window_ending_before_it_starts_crosses_midnight()
    {
        Assert.True(new TimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0), DayOfWeekSet.All).CrossesMidnight);
    }

    [Fact]
    public void A_zero_length_window_is_rejected_rather_than_guessed_at()
    {
        // Whether identical bounds mean "never" or "all day" decides whether the machine stays awake.
        // Rejecting it makes the configuration loader latch a fault, and a latched fault is itself a
        // reason to stay awake.
        Assert.Throws<ArgumentException>(
            () => new TimeWindow(new TimeOnly(9, 0), new TimeOnly(9, 0), DayOfWeekSet.All));
    }

    [Fact]
    public void A_window_that_applies_to_no_day_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.Empty));
    }

    [Fact]
    public void Windows_with_the_same_bounds_and_days_are_equal()
    {
        // The schedule relies on this to avoid listing one window twice when it matches on two
        // candidate days.
        var first = new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.Weekdays);
        var second = new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.Weekdays);

        Assert.Equal(first, second);
        Assert.NotEqual(first, new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.All));
    }
}

using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class DayOfWeekSetTests
{
    [Fact]
    public void Weekdays_is_Monday_to_Friday()
    {
        var weekdays = DayOfWeekSet.Weekdays;

        Assert.Equal(5, weekdays.Count);
        Assert.True(weekdays.Contains(DayOfWeek.Monday));
        Assert.True(weekdays.Contains(DayOfWeek.Friday));
        Assert.False(weekdays.Contains(DayOfWeek.Saturday));
        Assert.False(weekdays.Contains(DayOfWeek.Sunday));
    }

    [Fact]
    public void Weekends_is_Saturday_and_Sunday()
    {
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], DayOfWeekSet.Weekends.Days);
    }

    [Fact]
    public void All_and_Empty_are_the_extremes()
    {
        Assert.Equal(7, DayOfWeekSet.All.Count);
        Assert.False(DayOfWeekSet.All.IsEmpty);
        Assert.Equal(0, DayOfWeekSet.Empty.Count);
        Assert.True(DayOfWeekSet.Empty.IsEmpty);
    }

    [Fact]
    public void Days_are_listed_Sunday_first()
    {
        var set = DayOfWeekSet.Of(DayOfWeek.Friday, DayOfWeek.Sunday, DayOfWeek.Wednesday);

        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Wednesday, DayOfWeek.Friday], set.Days);
    }

    [Fact]
    public void Adding_and_removing_produces_a_new_set()
    {
        var monday = DayOfWeekSet.Of(DayOfWeek.Monday);

        Assert.True(monday.With(DayOfWeek.Tuesday).Contains(DayOfWeek.Tuesday));
        Assert.False(monday.Contains(DayOfWeek.Tuesday));
        Assert.True(monday.Without(DayOfWeek.Monday).IsEmpty);
    }

    [Fact]
    public void Repeating_a_day_does_not_add_it_twice()
    {
        Assert.Equal(1, DayOfWeekSet.Of(DayOfWeek.Monday, DayOfWeek.Monday).Count);
    }

    [Fact]
    public void Removing_a_day_that_is_not_present_changes_nothing()
    {
        var monday = DayOfWeekSet.Of(DayOfWeek.Monday);

        Assert.Equal(monday, monday.Without(DayOfWeek.Tuesday));
    }

    [Fact]
    public void Sets_with_the_same_days_are_equal()
    {
        // Equality matters because a set is persisted and compared as one value.
        Assert.Equal(DayOfWeekSet.Of(DayOfWeek.Monday, DayOfWeek.Tuesday), DayOfWeekSet.Of(DayOfWeek.Tuesday, DayOfWeek.Monday));
        Assert.NotEqual(DayOfWeekSet.Weekdays, DayOfWeekSet.All);
    }

    [Fact]
    public void A_value_outside_the_week_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DayOfWeekSet.Of((DayOfWeek)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => DayOfWeekSet.All.Contains((DayOfWeek)(-1)));
    }

    [Fact]
    public void Null_days_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => DayOfWeekSet.Of(null!));
    }

    [Fact]
    public void The_text_form_names_the_days()
    {
        Assert.Equal("none", DayOfWeekSet.Empty.ToString());
        Assert.Equal("Sunday,Saturday", DayOfWeekSet.Weekends.ToString());
    }
}

using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Plus8 = TimeZoneInfo.CreateCustomTimeZone(
        "PowerLease Test +08", TimeSpan.FromHours(8), "PowerLease Test +08", "PLT8");

    private static readonly TimeZoneInfo Minus8 = TimeZoneInfo.CreateCustomTimeZone(
        "PowerLease Test -08", TimeSpan.FromHours(-8), "PowerLease Test -08", "PLTM8");

    /// <summary>
    /// A zone built here rather than looked up from the operating system, so the daylight saving
    /// tests do not depend on which time zone database the machine happens to ship. Standard offset
    /// -08:00, one hour of daylight saving from the second Sunday in March to the first Sunday in
    /// November, both transitions at 02:00 local. In 2026 that is 8 March and 1 November.
    /// </summary>
    private static TimeZoneInfo DaylightSavingZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));

        return TimeZoneInfo.CreateCustomTimeZone(
            "PowerLease Test (DST)",
            TimeSpan.FromHours(-8),
            "PowerLease Test (DST)",
            "PLTS",
            "PLTD",
            [rule]);
    }

    private static Schedule Workdays() =>
        new([new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.Weekdays)]);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void An_instant_inside_a_weekday_window_holds()
    {
        // 2026-07-29 is a Wednesday; 02:00 UTC is 10:00 local in +08:00.
        var evaluation = Workdays().EvaluateAt(Utc(2026, 7, 29, 2), Plus8);

        Assert.True(evaluation.IsInGuaranteedAwakeWindow);
        Assert.Single(evaluation.ActiveWindows);
        Assert.False(evaluation.WidenedForDaylightSavingTransition);
    }

    [Fact]
    public void An_instant_before_the_window_does_not_hold()
    {
        var evaluation = Workdays().EvaluateAt(Utc(2026, 7, 29, 0), Plus8);

        Assert.False(evaluation.IsInGuaranteedAwakeWindow);
        Assert.Empty(evaluation.ActiveWindows);
    }

    [Fact]
    public void The_start_is_inclusive_and_the_end_is_exclusive()
    {
        var schedule = Workdays();

        Assert.True(schedule.EvaluateAt(Utc(2026, 7, 29, 1), Plus8).IsInGuaranteedAwakeWindow);
        Assert.False(schedule.EvaluateAt(Utc(2026, 7, 29, 10), Plus8).IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void A_day_the_window_does_not_apply_to_does_not_hold()
    {
        // 2026-08-01 is a Saturday.
        Assert.False(Workdays().EvaluateAt(Utc(2026, 8, 1, 2), Plus8).IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void A_window_running_past_midnight_is_anchored_to_the_day_it_starts_on()
    {
        // Friday 22:00 to 06:00. 2026-07-31 is a Friday, 2026-08-01 a Saturday.
        var schedule = new Schedule(
            [new TimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0), DayOfWeekSet.Of(DayOfWeek.Friday))]);

        Assert.True(schedule.EvaluateAt(Utc(2026, 7, 31, 15), Plus8).IsInGuaranteedAwakeWindow);
        Assert.True(schedule.EvaluateAt(Utc(2026, 7, 31, 19), Plus8).IsInGuaranteedAwakeWindow);
        Assert.False(schedule.EvaluateAt(Utc(2026, 7, 31, 13), Plus8).IsInGuaranteedAwakeWindow);

        // Saturday night is outside, because the window belongs to Friday.
        Assert.False(schedule.EvaluateAt(Utc(2026, 8, 1, 15), Plus8).IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void An_empty_schedule_never_holds()
    {
        Assert.False(Schedule.Empty.EvaluateAt(Utc(2026, 7, 29, 2), Plus8).IsInGuaranteedAwakeWindow);
        Assert.Empty(Schedule.Empty.GuaranteedAwakeWindows);
    }

    [Fact]
    public void The_same_instant_can_fall_inside_one_time_zone_and_outside_another()
    {
        // Why the time zone is a parameter rather than captured once: changing it must take effect on
        // the next evaluation.
        var schedule = Workdays();
        var instant = Utc(2026, 7, 29, 3);

        Assert.True(schedule.EvaluateAt(instant, Plus8).IsInGuaranteedAwakeWindow);
        Assert.False(schedule.EvaluateAt(instant, Minus8).IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void Several_windows_covering_the_same_instant_are_all_listed()
    {
        var schedule = new Schedule(
        [
            new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.Weekdays),
            new TimeWindow(new TimeOnly(10, 0), new TimeOnly(11, 0), DayOfWeekSet.All)
        ]);

        var evaluation = schedule.EvaluateAt(Utc(2026, 7, 29, 2, 30), Plus8);

        Assert.Equal(2, evaluation.ActiveWindows.Count);
    }

    [Fact]
    public void A_window_the_clock_jumps_over_still_holds()
    {
        // 2026-03-08 02:00 local does not exist: the clock goes straight to 03:00. A window of
        // 02:00 to 02:30 would otherwise never fire, silently removing the protection the user asked
        // for, so the window is widened across the transition instead.
        var zone = DaylightSavingZone();
        var schedule = new Schedule(
            [new TimeWindow(new TimeOnly(2, 0), new TimeOnly(2, 30), DayOfWeekSet.Of(DayOfWeek.Sunday))]);
        var transition = Utc(2026, 3, 8, 10);

        // The local time at this instant is 03:00, past the end of the configured window.
        Assert.Equal(new TimeSpan(3, 0, 0), TimeZoneInfo.ConvertTime(transition, zone).TimeOfDay);
        Assert.True(zone.IsInvalidTime(new DateTime(2026, 3, 8, 2, 15, 0)));

        var evaluation = schedule.EvaluateAt(transition, zone);

        Assert.True(evaluation.IsInGuaranteedAwakeWindow);
        Assert.True(evaluation.WidenedForDaylightSavingTransition);
    }

    [Fact]
    public void The_same_window_on_an_ordinary_day_is_not_widened()
    {
        // The contrast that shows the widening is specific to the transition: one week later the same
        // window covers exactly 02:00 to 02:30 local and nothing more.
        var zone = DaylightSavingZone();
        var schedule = new Schedule(
            [new TimeWindow(new TimeOnly(2, 0), new TimeOnly(2, 30), DayOfWeekSet.Of(DayOfWeek.Sunday))]);

        var inside = schedule.EvaluateAt(Utc(2026, 3, 15, 9), zone);
        Assert.True(inside.IsInGuaranteedAwakeWindow);
        Assert.False(inside.WidenedForDaylightSavingTransition);

        Assert.False(schedule.EvaluateAt(Utc(2026, 3, 15, 10), zone).IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void Both_passes_through_a_repeated_hour_are_inside_the_window()
    {
        // 2026-11-01 01:00 to 02:00 local happens twice. GetUtcOffset resolves an ambiguous local time
        // to standard time, so resolving the boundary the obvious way would leave the first pass
        // outside the window and drop protection for an hour.
        var zone = DaylightSavingZone();
        var schedule = new Schedule(
            [new TimeWindow(new TimeOnly(1, 0), new TimeOnly(1, 30), DayOfWeekSet.Of(DayOfWeek.Sunday))]);

        Assert.True(zone.IsAmbiguousTime(new DateTime(2026, 11, 1, 1, 15, 0)));

        var firstPass = schedule.EvaluateAt(Utc(2026, 11, 1, 8, 15), zone);
        var secondPass = schedule.EvaluateAt(Utc(2026, 11, 1, 9, 15), zone);

        Assert.True(firstPass.IsInGuaranteedAwakeWindow);
        Assert.True(firstPass.WidenedForDaylightSavingTransition);
        Assert.True(secondPass.IsInGuaranteedAwakeWindow);
        Assert.True(secondPass.WidenedForDaylightSavingTransition);

        // Still bounded: after the latest possible end of the window it stops holding.
        Assert.False(schedule.EvaluateAt(Utc(2026, 11, 1, 9, 45), zone).IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void A_time_zone_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => Workdays().EvaluateAt(Utc(2026, 7, 29, 2), null!));
    }

    [Fact]
    public void The_window_list_is_validated()
    {
        Assert.Throws<ArgumentNullException>(() => new Schedule(null!));
        Assert.Throws<ArgumentException>(() => new Schedule([null!]));
    }
}

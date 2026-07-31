using PowerLease.Domain;
using PowerLease.Persistence.Configuration;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class ConfigSchedulesTests
{
    private static readonly TimeZoneInfo Plus8 = TimeZoneInfo.CreateCustomTimeZone(
        "PowerLease Test +08", TimeSpan.FromHours(8), "PowerLease Test +08", "PLT8");

    [Fact]
    public void A_configured_window_evaluates_the_way_it_reads()
    {
        // The end-to-end check that the text in the file becomes the behaviour the user expects.
        var schedule = ConfigSchedules.ToSchedule(
        [
            new ScheduleWindowOptions
            {
                Start = "09:00",
                End = "18:00",
                Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]
            }
        ]);

        // 2026-07-29 is a Wednesday; 02:00 UTC is 10:00 local in +08:00.
        Assert.True(schedule.EvaluateAt(new DateTimeOffset(2026, 7, 29, 2, 0, 0, TimeSpan.Zero), Plus8)
            .IsInGuaranteedAwakeWindow);
        Assert.False(schedule.EvaluateAt(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero), Plus8)
            .IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void A_window_running_past_midnight_keeps_that_meaning()
    {
        var schedule = ConfigSchedules.ToSchedule(
            [new ScheduleWindowOptions { Start = "22:00", End = "06:00", Days = ["Friday"] }]);

        var window = Assert.Single(schedule.GuaranteedAwakeWindows);
        Assert.True(window.CrossesMidnight);

        // 2026-07-31 is a Friday; 19:00 UTC is Saturday 03:00 local, still inside Friday's window.
        Assert.True(schedule.EvaluateAt(new DateTimeOffset(2026, 7, 31, 19, 0, 0, TimeSpan.Zero), Plus8)
            .IsInGuaranteedAwakeWindow);
    }

    [Fact]
    public void Day_names_are_accepted_in_any_case()
    {
        var schedule = ConfigSchedules.ToSchedule(
            [new ScheduleWindowOptions { Start = "09:00", End = "10:00", Days = ["monday", "TUESDAY"] }]);

        var days = Assert.Single(schedule.GuaranteedAwakeWindows).Days;
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday], days.Days);
    }

    [Theory]
    [InlineData("25:00", "10:00", "start")]
    [InlineData("9:00", "10:00", "start")]
    [InlineData("09:00", "not a time", "end")]
    public void A_time_that_is_not_HH_mm_is_reported_against_its_own_field(string start, string end, string field)
    {
        var problem = ConfigSchedules.TryConvert(
            new ScheduleWindowOptions { Start = start, End = end, Days = ["Monday"] }, "schedules[0]", out var window);

        Assert.NotNull(problem);
        Assert.Equal($"schedules[0].{field}", problem.Path);
        Assert.Null(window);
    }

    [Fact]
    public void A_zero_length_window_is_reported_rather_than_guessed_at()
    {
        var problem = ConfigSchedules.TryConvert(
            new ScheduleWindowOptions { Start = "09:00", End = "09:00", Days = ["Monday"] }, "schedules[0]", out _);

        Assert.NotNull(problem);
        Assert.Equal("schedules[0]", problem.Path);
        Assert.Contains("non-zero length", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_window_with_no_days_is_reported()
    {
        var problem = ConfigSchedules.TryConvert(
            new ScheduleWindowOptions { Start = "09:00", End = "10:00", Days = [] }, "schedules[0]", out _);

        Assert.NotNull(problem);
        Assert.Equal("schedules[0].days", problem.Path);
    }

    [Fact]
    public void An_unknown_day_name_is_reported_with_its_index()
    {
        var problem = ConfigSchedules.TryConvert(
            new ScheduleWindowOptions { Start = "09:00", End = "10:00", Days = ["Monday", "Someday"] },
            "schedules[0]",
            out _);

        Assert.NotNull(problem);
        Assert.Equal("schedules[0].days[1]", problem.Path);
    }

    [Fact]
    public void A_numeric_day_is_refused_rather_than_read_as_an_index()
    {
        // "3" happens to parse as Wednesday, which is far more likely to be a mistake than an
        // intention, and a schedule silently covering the wrong day is hard to notice.
        var problem = ConfigSchedules.TryConvert(
            new ScheduleWindowOptions { Start = "09:00", End = "10:00", Days = ["3"] }, "schedules[0]", out _);

        Assert.NotNull(problem);
        Assert.Equal("schedules[0].days[0]", problem.Path);
    }

    [Fact]
    public void An_empty_schedule_list_produces_an_empty_schedule()
    {
        Assert.Empty(ConfigSchedules.ToSchedule([]).GuaranteedAwakeWindows);
    }

    [Fact]
    public void Converting_an_unvalidated_window_fails_loudly()
    {
        // Reaching here means validation was skipped, which is a defect rather than bad input.
        var error = Assert.Throws<InvalidOperationException>(() => ConfigSchedules.ToSchedule(
            [new ScheduleWindowOptions { Start = "09:00", End = "09:00", Days = ["Monday"] }]));

        Assert.Contains("validation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigSchedules.ToSchedule(null!));
        Assert.Throws<ArgumentNullException>(() => ConfigSchedules.TryConvert(null!, "schedules[0]", out _));
    }
}

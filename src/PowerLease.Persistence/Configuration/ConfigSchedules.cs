using System.Globalization;
using PowerLease.Domain;

namespace PowerLease.Persistence.Configuration;

/// <summary>
/// Turns the text form of a schedule in <c>config.json</c> into the domain type that evaluates it.
/// </summary>
public static class ConfigSchedules
{
    // HH, not hh: the field is documented as a 24-hour time, and hh is the 12-hour specifier, which
    // rejects every hour past noon.
    private const string TimeFormat = @"HH\:mm";

    /// <summary>
    /// Convert one window, reporting the first problem found instead of throwing.
    /// </summary>
    /// <returns>Null when the window is valid, otherwise what is wrong with it.</returns>
    public static ConfigProblem? TryConvert(ScheduleWindowOptions options, string path, out TimeWindow? window)
    {
        ArgumentNullException.ThrowIfNull(options);
        window = null;

        if (!TryParseTime(options.Start, out var start))
        {
            return new ConfigProblem($"{path}.start", $"'{options.Start}' is not a time of day in HH:mm form.");
        }

        if (!TryParseTime(options.End, out var end))
        {
            return new ConfigProblem($"{path}.end", $"'{options.End}' is not a time of day in HH:mm form.");
        }

        if (start == end)
        {
            return new ConfigProblem(
                path,
                $"A window must have a non-zero length; start and end are both '{options.Start}'. " +
                "Whether that means the whole day or none of it cannot be guessed, and the guess would " +
                "decide whether the machine stays awake.");
        }

        var days = DayOfWeekSet.Empty;
        for (var i = 0; i < options.Days.Count; i++)
        {
            if (!TryParseDay(options.Days[i], out var day))
            {
                return new ConfigProblem(
                    $"{path}.days[{i}]",
                    $"'{options.Days[i]}' is not a day name. Use Monday through Sunday.");
            }

            days = days.With(day);
        }

        if (days.IsEmpty)
        {
            return new ConfigProblem($"{path}.days", "A window must apply to at least one day.");
        }

        window = new TimeWindow(start, end, days);
        return null;
    }

    /// <summary>
    /// Convert every window. Only call this with a configuration that already validated; a problem
    /// here means validation was skipped, which is a defect rather than bad input.
    /// </summary>
    public static Schedule ToSchedule(IReadOnlyList<ScheduleWindowOptions> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var converted = new List<TimeWindow>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            var problem = TryConvert(windows[i], $"schedules[{i}]", out var window);
            if (problem is not null || window is null)
            {
                throw new InvalidOperationException(
                    $"Schedule {i} is not valid and should have been rejected by validation: {problem}");
            }

            converted.Add(window);
        }

        return new Schedule(converted);
    }

    private static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(value)
            && TimeOnly.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    private static bool TryParseDay(string? value, out DayOfWeek day)
    {
        day = default;

        // Digits are refused so that "3" cannot quietly mean Wednesday: a numeric day in a
        // configuration file is far more likely to be a mistake than an intention.
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsDigit(value[0])
            && Enum.TryParse(value, ignoreCase: true, out day)
            && Enum.IsDefined(day);
    }
}

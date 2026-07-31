namespace PowerLease.Domain;

/// <summary>
/// The windows during which the machine must be kept awake regardless of how idle it looks.
/// <para>
/// Windows are written in local time and evaluated against an instant, so a change of time zone
/// takes effect on the next evaluation. Only guaranteed-awake windows exist in this version; windows
/// that merely schedule a wake-up are part of the wake-timer work and are not modelled here.
/// </para>
/// </summary>
public sealed class Schedule
{
    public Schedule(IEnumerable<TimeWindow> guaranteedAwakeWindows)
    {
        ArgumentNullException.ThrowIfNull(guaranteedAwakeWindows);

        var windows = new List<TimeWindow>();
        foreach (var window in guaranteedAwakeWindows)
        {
            windows.Add(window ?? throw new ArgumentException(
                "A null window was supplied.", nameof(guaranteedAwakeWindows)));
        }

        GuaranteedAwakeWindows = windows;
    }

    public static Schedule Empty { get; } = new([]);

    public IReadOnlyList<TimeWindow> GuaranteedAwakeWindows { get; }

    /// <summary>
    /// Decide whether <paramref name="instant" /> falls inside a guaranteed-awake window in
    /// <paramref name="zone" />.
    /// </summary>
    public ScheduleEvaluation EvaluateAt(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (GuaranteedAwakeWindows.Count == 0)
        {
            return new ScheduleEvaluation([], widenedForDaylightSavingTransition: false);
        }

        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

        var active = new List<TimeWindow>();
        var widened = false;

        // Windows are anchored to the day they start on, so one that runs past midnight is found by
        // also looking at yesterday. Tomorrow is checked too because widening a start over a daylight
        // saving transition can pull a window back across midnight.
        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            var anchor = localDate.AddDays(dayOffset);

            foreach (var window in GuaranteedAwakeWindows)
            {
                if (!window.Days.Contains(anchor.DayOfWeek))
                {
                    continue;
                }

                var startBracket = Bracket(anchor.ToDateTime(window.Start), zone);
                var endDate = window.CrossesMidnight ? anchor.AddDays(1) : anchor;
                var endBracket = Bracket(endDate.ToDateTime(window.End), zone);

                // Earliest possible start, latest possible end: the window is widened, never narrowed.
                if (instant < startBracket.Earliest || instant >= endBracket.Latest)
                {
                    continue;
                }

                if (!active.Contains(window))
                {
                    active.Add(window);
                }

                widened |= startBracket.Shifted || endBracket.Shifted;
            }
        }

        return new ScheduleEvaluation(active, widened);
    }

    /// <summary>
    /// The range of instants a local wall-clock time could correspond to.
    /// <para>
    /// A window boundary is written as a local time, and twice a year a local time either does not
    /// exist or happens twice. Taking the earliest possible instant for a start and the latest for an
    /// end widens the window across the transition, which is the conservative direction: a
    /// guaranteed-awake window that silently fails to fire is a hole in exactly the protection the
    /// user asked for. Note that <see cref="TimeZoneInfo.GetUtcOffset(DateTime)" /> resolves an
    /// ambiguous local time to standard time, so without this the first of the two repeated hours
    /// would fall outside the window.
    /// </para>
    /// </summary>
    private static (DateTimeOffset Earliest, DateTimeOffset Latest, bool Shifted) Bracket(
        DateTime local,
        TimeZoneInfo zone)
    {
        if (zone.IsAmbiguousTime(local))
        {
            var earliest = DateTimeOffset.MaxValue;
            var latest = DateTimeOffset.MinValue;

            foreach (var offset in zone.GetAmbiguousTimeOffsets(local))
            {
                var candidate = new DateTimeOffset(local, offset);
                if (candidate < earliest)
                {
                    earliest = candidate;
                }

                if (candidate > latest)
                {
                    latest = candidate;
                }
            }

            return (earliest, latest, true);
        }

        if (zone.IsInvalidTime(local))
        {
            // The clock jumped over this local time. Bracket the jump with the offsets in effect on
            // either side of it; sampling a day away is safe because transitions are months apart.
            var before = new DateTimeOffset(local, zone.GetUtcOffset(local.AddDays(-1)));
            var after = new DateTimeOffset(local, zone.GetUtcOffset(local.AddDays(1)));

            return before <= after ? (before, after, true) : (after, before, true);
        }

        var exact = new DateTimeOffset(local, zone.GetUtcOffset(local));
        return (exact, exact, false);
    }
}

namespace PowerLease.Domain;

/// <summary>
/// A recurring local-time window, such as weekdays 09:00 to 18:00.
/// <para>
/// A window that ends before it starts runs past midnight and is anchored to the day it starts on,
/// so "Friday 22:00 to 06:00" belongs to Friday even though it ends on Saturday.
/// </para>
/// </summary>
public sealed record TimeWindow
{
    public TimeWindow(TimeOnly start, TimeOnly end, DayOfWeekSet days)
    {
        if (start == end)
        {
            // Whether this means "no time at all" or "the whole day" is a guess, and guessing here
            // decides whether the machine stays awake. Rejecting it makes the configuration loader
            // latch a fault instead, and a latched fault is itself a reason to stay awake.
            throw new ArgumentException(
                $"A time window must have a non-zero length; start and end are both {start}.",
                nameof(end));
        }

        if (days.IsEmpty)
        {
            throw new ArgumentException("A time window must apply to at least one day.", nameof(days));
        }

        Start = start;
        End = end;
        Days = days;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    /// <summary>The days the window starts on.</summary>
    public DayOfWeekSet Days { get; }

    public bool CrossesMidnight => End < Start;
}

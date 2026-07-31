namespace PowerLease.Domain;

/// <summary>Which guaranteed-awake windows cover a given instant.</summary>
public sealed class ScheduleEvaluation
{
    public ScheduleEvaluation(
        IReadOnlyList<TimeWindow> activeWindows,
        bool widenedForDaylightSavingTransition)
    {
        ArgumentNullException.ThrowIfNull(activeWindows);

        ActiveWindows = activeWindows;
        WidenedForDaylightSavingTransition = widenedForDaylightSavingTransition;
    }

    public IReadOnlyList<TimeWindow> ActiveWindows { get; }

    /// <summary>
    /// True when a matching window's boundary fell on a daylight saving transition and was widened.
    /// Surfaced by <c>powerlease status</c> so an unexpected hold has a visible explanation.
    /// </summary>
    public bool WidenedForDaylightSavingTransition { get; }

    /// <summary>
    /// True when the machine must be kept awake by the schedule. Derived from
    /// <see cref="ActiveWindows" /> so the two cannot disagree.
    /// </summary>
    public bool IsInGuaranteedAwakeWindow => ActiveWindows.Count > 0;
}

using PowerLease.Domain;

namespace PowerLease.Application.Inhibitors;

/// <summary>
/// Holds the machine awake during the windows the user asked for.
/// <para>
/// The time zone is read through a provider on every evaluation rather than captured once. A machine that
/// travels, or whose zone is corrected, must start honouring the new one on the next cycle -- and the
/// provider deliberately clears the runtime's cached zone data, because otherwise a process started before
/// the change would keep using the old one for as long as it lives.
/// </para>
/// </summary>
public sealed class ScheduleEvaluator
{
    public const string SourceId = "schedule";

    private readonly Schedule _schedule;
    private readonly ITimeZoneProvider _timeZones;

    public ScheduleEvaluator(Schedule schedule, ITimeZoneProvider timeZones)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(timeZones);

        _schedule = schedule;
        _timeZones = timeZones;
    }

    public InhibitorSourceReport Evaluate(DateTimeOffset nowUtc)
    {
        if (_schedule.GuaranteedAwakeWindows.Count == 0)
        {
            return InhibitorSourceReport.ConfirmedAbsent(SourceId);
        }

        TimeZoneInfo zone;
        try
        {
            zone = _timeZones.Current;
        }
        catch (TimeZoneNotFoundException error)
        {
            // A schedule that cannot be placed on a clock cannot be honoured, and the window the user asked
            // for may be running right now.
            return InhibitorSourceReport.Indeterminate(
                SourceId, $"The local time zone could not be determined: {error.Message}");
        }
        catch (InvalidTimeZoneException error)
        {
            return InhibitorSourceReport.Indeterminate(
                SourceId, $"The local time zone is not usable: {error.Message}");
        }

        var evaluation = _schedule.EvaluateAt(nowUtc, zone);

        if (!evaluation.IsInGuaranteedAwakeWindow)
        {
            return InhibitorSourceReport.ConfirmedAbsent(SourceId);
        }

        var reason = evaluation.WidenedForDaylightSavingTransition
            ? "Inside a guaranteed-awake window, widened across a daylight saving transition"
            : "Inside a guaranteed-awake window";

        var detail = string.Join(
            ", ",
            evaluation.ActiveWindows.Select(window => $"{window.Start:HH\\:mm}-{window.End:HH\\:mm} {window.Days}"));

        return InhibitorSourceReport.Observed(
            SourceId,
            new Inhibitor(InhibitorKind.ScheduleWindow, reason, nowUtc, detail));
    }
}

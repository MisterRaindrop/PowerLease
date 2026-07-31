namespace PowerLease.Application.Inhibitors;

/// <summary>Where a power figure came from.</summary>
public enum EnergySource
{
    /// <summary>Nothing to go on. No figure is produced at all.</summary>
    Unavailable,

    /// <summary>A hardware sensor reported it.</summary>
    Measured,

    /// <summary>Derived from a configured baseline. Never to be shown as a reading.</summary>
    Estimated
}

/// <summary>
/// Energy used over a period.
/// </summary>
/// <param name="IsEstimated">
/// Whether this was worked out rather than measured. Carried on the value itself so it cannot be presented
/// as a reading by a caller that forgot to check where it came from; a made-up number on a dashboard is
/// indistinguishable from a real one, and the user has no way to know they are being told a guess.
/// </param>
public sealed record EnergyEstimate(double? WattHours, EnergySource Source, bool IsEstimated, string? Detail = null)
{
    public static EnergyEstimate Unavailable(string detail) =>
        new(null, EnergySource.Unavailable, IsEstimated: false, detail);
}

/// <summary>
/// Works out how much energy a period used.
/// <para>
/// Deliberately modest. It reports what it can support and says nothing where it cannot: a measured figure
/// when a sensor gave one, an estimate from the configured idle baseline otherwise, and nothing at all when
/// there is neither. Recording only -- nothing in this version acts on energy.
/// </para>
/// </summary>
public sealed class EnergyEstimator
{
    private readonly double? _idleBaselineWatts;

    /// <param name="idleBaselineWatts">
    /// What the machine draws when idle, if the user supplied it. Without it no estimate is possible, and
    /// inventing a plausible-looking default would be worse than admitting that.
    /// </param>
    public EnergyEstimator(double? idleBaselineWatts)
    {
        _idleBaselineWatts = idleBaselineWatts is { } watts && watts >= 0 && !double.IsNaN(watts)
            ? watts
            : null;
    }

    /// <summary>
    /// Energy over <paramref name="duration" />.
    /// </summary>
    /// <param name="averageMeasuredWatts">
    /// The average a sensor reported, or null when none did.
    /// </param>
    public EnergyEstimate Estimate(TimeSpan duration, double? averageMeasuredWatts)
    {
        if (duration <= TimeSpan.Zero)
        {
            return EnergyEstimate.Unavailable("The period has no length.");
        }

        if (averageMeasuredWatts is { } measured && measured >= 0 && !double.IsNaN(measured))
        {
            return new EnergyEstimate(
                measured * duration.TotalHours,
                EnergySource.Measured,
                IsEstimated: false);
        }

        if (_idleBaselineWatts is { } baseline)
        {
            return new EnergyEstimate(
                baseline * duration.TotalHours,
                EnergySource.Estimated,
                IsEstimated: true,
                $"Derived from the configured idle baseline of {baseline:0.##} W, not measured.");
        }

        return EnergyEstimate.Unavailable(
            "No power sensor reported a figure and no idle baseline is configured.");
    }
}

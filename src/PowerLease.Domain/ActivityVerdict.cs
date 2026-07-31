namespace PowerLease.Domain;

/// <summary>What a metric's recent history says about whether the machine is busy.</summary>
public enum ActivityVerdict
{
    /// <summary>A measurement above the threshold was seen inside the retained history.</summary>
    Active,

    /// <summary>
    /// Not enough trustworthy history to establish quiet: no samples, stale samples, a gap large
    /// enough to have hidden activity, or not yet enough quiet time.
    /// </summary>
    InsufficientData,

    /// <summary>
    /// Every measurement has been at or below the threshold, without a blind gap, for the full
    /// required duration. The only verdict that permits releasing protection.
    /// </summary>
    QuietLongEnough
}

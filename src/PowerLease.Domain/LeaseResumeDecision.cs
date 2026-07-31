namespace PowerLease.Domain;

/// <summary>
/// How a lease's remaining time was re-established after a change of monotonic clock epoch.
/// Recorded so <c>powerlease status</c> and the audit trail can explain why a lease has the
/// remaining time it does.
/// </summary>
public enum LeaseResumeDecision
{
    /// <summary>A usable checkpoint was found and its remaining time was granted again.</summary>
    RemainingFromCheckpoint,

    /// <summary>
    /// No checkpoint was persisted, so the full original duration was granted. Erring long is the
    /// safe direction: the alternative is dropping protection the user asked for.
    /// </summary>
    FullDurationNoCheckpoint,

    /// <summary>
    /// A checkpoint existed but contradicted itself, so the full original duration was granted.
    /// </summary>
    FullDurationInconsistentCheckpoint
}

namespace PowerLease.Domain;

public interface IClock
{
    // Used for persistence and display.
    DateTimeOffset UtcNow { get; }

    // Used to measure durations; system-clock changes do not affect it.
    MonotonicStamp Now { get; }

    /// <summary>
    /// Begin a new monotonic epoch after the machine resumes from sleep.
    /// <para>
    /// Elapsed values captured before and after this call have different origins and must never be
    /// compared. Implementations must publish the new identifier and elapsed origin atomically.
    /// </para>
    /// </summary>
    void BeginNewEpoch();
}

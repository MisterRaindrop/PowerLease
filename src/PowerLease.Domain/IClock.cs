namespace PowerLease.Domain;

public interface IClock
{
    // Used for persistence and display.
    DateTimeOffset UtcNow { get; }

    // Used to measure durations; system-clock changes do not affect it.
    MonotonicStamp Now { get; }
}

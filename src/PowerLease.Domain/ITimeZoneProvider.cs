namespace PowerLease.Domain;

public interface ITimeZoneProvider
{
    // Implementations must reflect runtime time-zone changes and must not cache permanently.
    TimeZoneInfo Current { get; }
}

using PowerLease.Domain;

namespace PowerLease.Infrastructure.Windows;

public sealed class SystemTimeZoneProvider : ITimeZoneProvider
{
    public TimeZoneInfo Current
    {
        get
        {
            // TimeZoneInfo.Local is cached; clear it so runtime user time-zone changes are observed.
            TimeZoneInfo.ClearCachedData();
            return TimeZoneInfo.Local;
        }
    }
}

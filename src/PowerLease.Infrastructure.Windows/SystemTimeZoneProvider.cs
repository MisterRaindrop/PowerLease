using PowerLease.Domain;

namespace PowerLease.Infrastructure.Windows;

/// <summary>
/// The local time zone as Windows reports it.
/// <para>
/// The runtime caches <see cref="TimeZoneInfo.Local" /> for the life of the process, so a user changing the
/// zone would otherwise never be noticed. Clearing that cache is a process-wide side effect, so it happens only
/// when <see cref="Refresh" /> is called -- on the time-change event -- rather than on every read.
/// </para>
/// </summary>
public sealed class SystemTimeZoneProvider : ITimeZoneProvider
{
    private TimeZoneInfo? _cached;

    public TimeZoneInfo Current => _cached ??= TimeZoneInfo.Local;

    public void Refresh()
    {
        TimeZoneInfo.ClearCachedData();
        _cached = null;
    }
}

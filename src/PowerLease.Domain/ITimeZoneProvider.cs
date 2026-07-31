namespace PowerLease.Domain;

/// <summary>
/// The machine's local time zone, which schedules are written in.
/// <para>
/// Edge triggered, not polled. Reading is cheap and cached; <see cref="Refresh" /> is what discards the cache,
/// and the host calls it when it learns the zone or the clock changed -- the same event the kernel handles by
/// making every source report again. Discarding the cache on every read instead would mean a process-wide side
/// effect several times a minute, forcing unrelated code that wants local time to re-read zone data too.
/// </para>
/// </summary>
public interface ITimeZoneProvider
{
    TimeZoneInfo Current { get; }

    /// <summary>
    /// Forget the cached zone, so the next read reflects a change made since. Safe to call when nothing changed.
    /// </summary>
    void Refresh();
}

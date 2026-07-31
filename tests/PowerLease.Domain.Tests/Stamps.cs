using PowerLease.Domain;

namespace PowerLease.Domain.Tests;

/// <summary>
/// Monotonic stamps for tests. The epoch identifiers are fixed rather than generated so a failure
/// message names the same epoch every run.
/// </summary>
internal static class Stamps
{
    public static readonly Guid EpochA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid EpochB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static MonotonicStamp Seconds(double seconds, Guid? epoch = null) =>
        new(epoch ?? EpochA, TimeSpan.FromSeconds(seconds));

    public static MonotonicStamp Minutes(double minutes, Guid? epoch = null) =>
        new(epoch ?? EpochA, TimeSpan.FromMinutes(minutes));
}

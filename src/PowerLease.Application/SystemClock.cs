using System.Diagnostics;
using PowerLease.Domain;

namespace PowerLease.Application;

public sealed class SystemClock : IClock
{
    private readonly Guid _epochId = Guid.NewGuid();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public MonotonicStamp Now => new(_epochId, _stopwatch.Elapsed);
}

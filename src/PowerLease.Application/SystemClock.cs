using System.Diagnostics;
using PowerLease.Domain;

namespace PowerLease.Application;

public sealed class SystemClock : IClock
{
    private Epoch _epoch = Epoch.Start();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public MonotonicStamp Now
    {
        get
        {
            // The reference is the publication boundary. A reader uses one immutable epoch object, so it
            // cannot pair the identifier from one epoch with the stopwatch from another during a resume.
            var epoch = Volatile.Read(ref _epoch);
            return new MonotonicStamp(epoch.Id, epoch.Stopwatch.Elapsed);
        }
    }

    /// <inheritdoc />
    public void BeginNewEpoch()
    {
        var next = Epoch.Start();
        Volatile.Write(ref _epoch, next);
    }

    private sealed record Epoch(Guid Id, Stopwatch Stopwatch)
    {
        public static Epoch Start() => new(Guid.NewGuid(), Stopwatch.StartNew());
    }
}

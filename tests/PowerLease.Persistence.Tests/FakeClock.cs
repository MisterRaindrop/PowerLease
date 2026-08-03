using PowerLease.Domain;

namespace PowerLease.Persistence.Tests;

/// <summary>A clock the test moves by hand, so timestamps and backup file names are predictable.</summary>
internal sealed class FakeClock : IClock
{
    private static readonly Guid Epoch = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public FakeClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; set; }

    public TimeSpan Elapsed { get; set; }

    public Guid EpochId { get; set; } = Epoch;

    public MonotonicStamp Now => new(EpochId, Elapsed);

    public void Advance(TimeSpan amount)
    {
        UtcNow = UtcNow.Add(amount);
        Elapsed += amount;
    }

    public void BeginNewEpoch()
    {
        EpochId = Guid.NewGuid();
        Elapsed = TimeSpan.Zero;
    }
}

using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class MonotonicStampTests
{
    [Fact]
    public void Equality_includes_the_epoch_identity()
    {
        var elapsed = TimeSpan.FromSeconds(5);
        var epochId = Guid.NewGuid();
        var first = new MonotonicStamp(epochId, elapsed);
        var same = new MonotonicStamp(epochId, elapsed);
        var differentEpoch = new MonotonicStamp(Guid.NewGuid(), elapsed);

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentEpoch);
    }
}

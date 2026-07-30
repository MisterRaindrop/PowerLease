using PowerLease.Application;
using Xunit;

namespace PowerLease.Application.Tests;

public sealed class SystemClockTests
{
    [Fact]
    public void Now_uses_a_stable_epoch_and_non_decreasing_elapsed_time_per_instance()
    {
        var clock = new SystemClock();
        var first = clock.Now;
        var second = clock.Now;

        Assert.Equal(first.EpochId, second.EpochId);
        Assert.True(second.Elapsed >= first.Elapsed, "Elapsed time must not decrease within an epoch.");
    }

    [Fact]
    public void Separate_instances_have_different_epochs()
    {
        var first = new SystemClock();
        var second = new SystemClock();

        Assert.NotEqual(first.Now.EpochId, second.Now.EpochId);
    }

    [Fact]
    public void UtcNow_has_a_zero_offset()
    {
        var clock = new SystemClock();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }
}

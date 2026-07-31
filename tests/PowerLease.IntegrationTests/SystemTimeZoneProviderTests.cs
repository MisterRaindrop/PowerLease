using PowerLease.Infrastructure.Windows;
using Xunit;

namespace PowerLease.IntegrationTests;

public sealed class SystemTimeZoneProviderTests
{
    [Fact]
    public void Reading_twice_does_not_re_read_the_operating_system()
    {
        // The point of caching: a schedule is evaluated every few seconds, and clearing the runtime's zone cache
        // that often is a process-wide side effect that unrelated code pays for.
        var provider = new SystemTimeZoneProvider();

        Assert.Same(provider.Current, provider.Current);
    }

    [Fact]
    public void Refreshing_makes_the_next_read_go_back_to_the_operating_system()
    {
        // Whether the zone actually changed cannot be simulated here without changing a machine-wide setting, so
        // what is asserted is the mechanism: after a refresh the cached instance is no longer handed back, which
        // is what lets a real change be observed.
        var provider = new SystemTimeZoneProvider();
        var before = provider.Current;

        provider.Refresh();

        Assert.Equal(before.Id, provider.Current.Id);
        Assert.NotSame(before, provider.Current);
    }
}

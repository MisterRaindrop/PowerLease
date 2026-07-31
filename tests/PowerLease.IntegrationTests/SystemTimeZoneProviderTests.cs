using PowerLease.Infrastructure.Windows;
using Xunit;

namespace PowerLease.IntegrationTests;

public sealed class SystemTimeZoneProviderTests
{
    [Fact]
    public void Current_returns_the_same_time_zone_when_the_system_setting_is_unchanged()
    {
        var provider = new SystemTimeZoneProvider();

        var first = provider.Current;
        var second = provider.Current;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
    }
}

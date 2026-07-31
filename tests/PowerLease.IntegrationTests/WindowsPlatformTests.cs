using PowerLease.Infrastructure.Windows;
using Xunit;

namespace PowerLease.IntegrationTests;

public sealed class WindowsPlatformTests
{
    [Fact]
    public void Platform_is_a_supported_Windows_runner()
    {
        Assert.True(WindowsPlatform.IsSupported, "This integration test must run on a Windows runner.");

        var description = WindowsPlatform.Describe();

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.StartsWith("Windows ", description);
    }
}

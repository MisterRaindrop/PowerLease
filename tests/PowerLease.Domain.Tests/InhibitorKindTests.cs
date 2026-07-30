using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class InhibitorKindTests
{
    [Fact]
    public void Names_are_the_complete_inhibitor_kind_set()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(InhibitorKind.SshSession),
            nameof(InhibitorKind.ManualLease),
            nameof(InhibitorKind.CliLease),
            nameof(InhibitorKind.ProtectedProcess),
            nameof(InhibitorKind.LockFile),
            nameof(InhibitorKind.ScheduleWindow),
            nameof(InhibitorKind.SystemActivity),
            nameof(InhibitorKind.GracePeriod),
            nameof(InhibitorKind.Fault),
            nameof(InhibitorKind.ProducerUnhealthy)
        };
        var actual = Enum.GetNames<InhibitorKind>().ToHashSet(StringComparer.Ordinal);

        Assert.True(expected.SetEquals(actual), "Inhibitor kind names must exactly match the supported set.");
    }
}

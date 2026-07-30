using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class ProtectionStateTests
{
    /// <summary>
    /// The three states are load-bearing and distinct: <c>Released</c> means we deliberately let
    /// Windows sleep the machine, <c>Protected</c> means the inhibit request is held AND accepted,
    /// and <c>Unprotected</c> means we want to hold it but the system is ignoring us. Collapsing
    /// <c>Unprotected</c> into either of the others would hide the one failure mode this product
    /// exists to prevent, so pin the set.
    /// </summary>
    [Fact]
    public void Names_are_the_complete_protection_state_set()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ProtectionState.Released),
            nameof(ProtectionState.Protected),
            nameof(ProtectionState.Unprotected)
        };
        var actual = Enum.GetNames<ProtectionState>().ToHashSet(StringComparer.Ordinal);

        Assert.True(expected.SetEquals(actual), "Protection state names must exactly match the supported set.");
    }
}

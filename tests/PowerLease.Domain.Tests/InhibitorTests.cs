using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class InhibitorTests
{
    [Fact]
    public void Records_with_the_same_fields_are_equal_but_different_kinds_are_not()
    {
        var sinceUtc = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var first = new Inhibitor(InhibitorKind.SshSession, "Active SSH session", sinceUtc, "host-a");
        var same = new Inhibitor(InhibitorKind.SshSession, "Active SSH session", sinceUtc, "host-a");
        var differentKind = new Inhibitor(InhibitorKind.ManualLease, "Active SSH session", sinceUtc, "host-a");

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentKind);
    }

    [Fact]
    public void Detail_defaults_to_null()
    {
        var inhibitor = new Inhibitor(
            InhibitorKind.ManualLease,
            "Manual protection requested",
            new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));

        Assert.Null(inhibitor.Detail);
    }

    /// <summary>
    /// The reason is what `powerlease status` shows a human when it explains why the machine is
    /// being kept awake, so ToString must actually surface the fields rather than a type name.
    /// </summary>
    [Fact]
    public void ToString_surfaces_the_kind_and_reason()
    {
        var inhibitor = new Inhibitor(
            InhibitorKind.ProtectedProcess,
            "msbuild.exe is running",
            new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));

        var text = inhibitor.ToString();

        Assert.Contains(nameof(InhibitorKind.ProtectedProcess), text, StringComparison.Ordinal);
        Assert.Contains("msbuild.exe is running", text, StringComparison.Ordinal);
    }

    [Fact]
    public void With_expression_replaces_only_the_named_member()
    {
        var sinceUtc = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var original = new Inhibitor(InhibitorKind.LockFile, "Lock file present", sinceUtc, "keep-awake.lock");

        var withDetail = original with { Detail = "renamed.lock" };

        Assert.Equal("renamed.lock", withDetail.Detail);
        Assert.Equal(original.Kind, withDetail.Kind);
        Assert.Equal(original.Reason, withDetail.Reason);
        Assert.Equal(original.SinceUtc, withDetail.SinceUtc);
        Assert.NotEqual(original, withDetail);
    }

    [Fact]
    public void Deconstruction_yields_the_constructor_arguments()
    {
        var sinceUtc = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var inhibitor = new Inhibitor(InhibitorKind.GracePeriod, "Resume grace period", sinceUtc, "10m");

        var (kind, reason, since, detail) = inhibitor;

        Assert.Equal(InhibitorKind.GracePeriod, kind);
        Assert.Equal("Resume grace period", reason);
        Assert.Equal(sinceUtc, since);
        Assert.Equal("10m", detail);
    }

    [Fact]
    public void Equal_records_share_a_hash_code()
    {
        var sinceUtc = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var first = new Inhibitor(InhibitorKind.SystemActivity, "CPU busy", sinceUtc);
        var same = new Inhibitor(InhibitorKind.SystemActivity, "CPU busy", sinceUtc);

        Assert.Equal(first.GetHashCode(), same.GetHashCode());
    }
}

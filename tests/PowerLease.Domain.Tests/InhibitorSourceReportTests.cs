using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class InhibitorSourceReportTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static Inhibitor Ssh() => new(InhibitorKind.SshSession, "session from 10.0.0.5", Origin);

    [Fact]
    public void Observed_carries_the_inhibitors_and_is_determinate()
    {
        var report = InhibitorSourceReport.Observed("ssh", Ssh());

        Assert.True(report.IsDeterminate);
        Assert.Null(report.IndeterminateReason);
        Assert.Equal(InhibitorKind.SshSession, Assert.Single(report.Inhibitors).Kind);
    }

    [Fact]
    public void Observed_rejects_an_empty_list_so_claiming_absence_stays_deliberate()
    {
        // An empty Observed would mean the same thing as ConfirmedAbsent, which is the only report
        // that can release protection. Requiring the named factory keeps every such claim greppable.
        var error = Assert.Throws<ArgumentException>(() => InhibitorSourceReport.Observed("ssh"));

        Assert.Contains(nameof(InhibitorSourceReport.ConfirmedAbsent), error.Message);
    }

    [Fact]
    public void Observed_rejects_a_null_inhibitor()
    {
        Assert.Throws<ArgumentException>(() => InhibitorSourceReport.Observed("ssh", Ssh(), null!));
    }

    [Fact]
    public void ConfirmedAbsent_is_determinate_and_contributes_nothing()
    {
        var report = InhibitorSourceReport.ConfirmedAbsent("ssh");

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void Indeterminate_is_not_determinate_and_keeps_its_reason()
    {
        var report = InhibitorSourceReport.Indeterminate("ssh", "event log unreadable");

        Assert.False(report.IsDeterminate);
        Assert.Equal("event log unreadable", report.IndeterminateReason);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void Indeterminate_requires_a_reason_because_the_reason_becomes_user_visible()
    {
        Assert.Throws<ArgumentException>(() => InhibitorSourceReport.Indeterminate("ssh", string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Every_factory_requires_a_source_id(string? sourceId)
    {
        // ThrowsAny because a null argument raises ArgumentNullException and an empty one raises
        // ArgumentException; both are rejections and the test is about the rejection, not the type.
        Assert.ThrowsAny<ArgumentException>(() => InhibitorSourceReport.Observed(sourceId!, Ssh()));
        Assert.ThrowsAny<ArgumentException>(() => InhibitorSourceReport.ConfirmedAbsent(sourceId!));
        Assert.ThrowsAny<ArgumentException>(() => InhibitorSourceReport.Indeterminate(sourceId!, "reason"));
    }
}

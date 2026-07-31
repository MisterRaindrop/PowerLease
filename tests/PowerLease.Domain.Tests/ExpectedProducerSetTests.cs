using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class ExpectedProducerSetTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static ExpectedProducerSet Producers(params string[] ids) =>
        new(ids, TimeSpan.FromSeconds(60));

    [Fact]
    public void A_source_that_has_never_reported_is_unhealthy()
    {
        // Startup is when protection matters most, so an unknown source counts as gone rather than
        // as fine.
        var producers = Producers("ssh", "locks");

        Assert.Equal(["locks", "ssh"], producers.UnhealthySources(Stamps.Seconds(0)));
        Assert.False(producers.AllHealthy(Stamps.Seconds(0)));
    }

    [Fact]
    public void A_fresh_heartbeat_makes_a_source_healthy()
    {
        var producers = Producers("ssh");
        producers.Heartbeat("ssh", Stamps.Seconds(10));

        Assert.True(producers.AllHealthy(Stamps.Seconds(30)));
        Assert.Empty(producers.UnhealthySources(Stamps.Seconds(30)));
    }

    [Fact]
    public void A_heartbeat_exactly_at_the_age_limit_is_still_healthy()
    {
        var producers = Producers("ssh");
        producers.Heartbeat("ssh", Stamps.Seconds(0));

        Assert.True(producers.AllHealthy(Stamps.Seconds(60)));
        Assert.False(producers.AllHealthy(Stamps.Seconds(61)));
    }

    [Fact]
    public void A_stale_heartbeat_makes_the_source_unhealthy_again()
    {
        // The dangerous case: the source's last known state was probably "nothing happening", and
        // reusing it would release protection on evidence that has since expired.
        var producers = Producers("ssh");
        producers.Heartbeat("ssh", Stamps.Seconds(0));

        Assert.Equal(["ssh"], producers.UnhealthySources(Stamps.Seconds(120)));
    }

    [Fact]
    public void A_heartbeat_from_another_clock_epoch_is_unhealthy()
    {
        // A heartbeat from before the last resume says nothing about now, and its age cannot even be
        // computed against the current clock.
        var producers = Producers("ssh");
        producers.Heartbeat("ssh", Stamps.Seconds(0, Stamps.EpochA));

        Assert.Equal(["ssh"], producers.UnhealthySources(Stamps.Seconds(1, Stamps.EpochB)));
    }

    [Fact]
    public void A_heartbeat_from_the_future_is_unhealthy()
    {
        // A monotonic clock must never go backwards inside one epoch, so this reading cannot be trusted.
        var producers = Producers("ssh");
        producers.Heartbeat("ssh", Stamps.Seconds(100));

        Assert.Equal(["ssh"], producers.UnhealthySources(Stamps.Seconds(50)));
    }

    [Fact]
    public void A_heartbeat_from_an_unexpected_source_is_counted_and_otherwise_ignored()
    {
        // Not an error, because a producer registered under the wrong identifier must not be able to
        // bring down the kernel. The expected source stays unhealthy, so the machine keeps holding,
        // and the count makes the mistake visible instead of leaving an unexplained hold.
        var producers = Producers("ssh");
        producers.Heartbeat("shh", Stamps.Seconds(0));

        Assert.Equal(1, producers.UnexpectedHeartbeatCount);
        Assert.False(producers.AllHealthy(Stamps.Seconds(0)));
        Assert.Equal(["ssh"], producers.UnhealthySources(Stamps.Seconds(0)));
    }

    [Fact]
    public void Every_unhealthy_source_becomes_a_reason_to_stay_awake()
    {
        var producers = Producers("ssh", "locks");
        producers.Heartbeat("locks", Stamps.Seconds(0));

        var inhibitors = producers.ToInhibitors(Stamps.Seconds(10), Origin);

        var inhibitor = Assert.Single(inhibitors);
        Assert.Equal(InhibitorKind.ProducerUnhealthy, inhibitor.Kind);
        Assert.Equal("ssh", inhibitor.Detail);
        Assert.Equal(Origin, inhibitor.SinceUtc);
        Assert.Contains("ssh", inhibitor.Reason);
    }

    [Fact]
    public void A_healthy_set_contributes_no_inhibitors()
    {
        var producers = Producers("ssh");
        producers.Heartbeat("ssh", Stamps.Seconds(0));

        Assert.Empty(producers.ToInhibitors(Stamps.Seconds(10), Origin));
    }

    [Fact]
    public void Expecting_nothing_is_always_healthy()
    {
        var producers = Producers();

        Assert.True(producers.AllHealthy(Stamps.Seconds(0)));
        Assert.Empty(producers.ExpectedSourceIds);
    }

    [Fact]
    public void A_repeated_identifier_is_only_expected_once()
    {
        var producers = Producers("ssh", "ssh");

        Assert.Single(producers.ExpectedSourceIds);
    }

    [Fact]
    public void The_constructor_validates_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ExpectedProducerSet(null!, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpectedProducerSet(["ssh"], TimeSpan.Zero));
        Assert.ThrowsAny<ArgumentException>(() => new ExpectedProducerSet([""], TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_heartbeat_needs_a_source_id(string? blank)
    {
        Assert.ThrowsAny<ArgumentException>(() => Producers("ssh").Heartbeat(blank!, Stamps.Seconds(0)));
    }
}

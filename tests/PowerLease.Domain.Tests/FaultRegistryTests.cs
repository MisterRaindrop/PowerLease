using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class FaultRegistryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_persistent_fault_survives_any_number_of_healthy_observations()
    {
        // Corrupt configuration does not become correct because the next read happened to succeed.
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 2);
        registry.Report("config", FaultSeverity.Persistent, "config.json failed validation", Origin);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(registry.ReportHealthy("config"));
        }

        Assert.True(registry.HasActiveFaults);
        Assert.Single(registry.Active);
    }

    [Fact]
    public void A_transient_fault_clears_only_after_enough_consecutive_healthy_observations()
    {
        // One good reading is not enough: an intermittent failure alternates.
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 3);
        registry.Report("metrics", FaultSeverity.Transient, "counter read failed", Origin);

        Assert.False(registry.ReportHealthy("metrics"));
        Assert.False(registry.ReportHealthy("metrics"));
        Assert.True(registry.HasActiveFaults);

        Assert.True(registry.ReportHealthy("metrics"));
        Assert.False(registry.HasActiveFaults);
    }

    [Fact]
    public void A_new_failure_resets_the_healthy_count()
    {
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 2);
        registry.Report("metrics", FaultSeverity.Transient, "counter read failed", Origin);

        registry.ReportHealthy("metrics");
        registry.Report("metrics", FaultSeverity.Transient, "counter read failed again", Origin.AddSeconds(30));

        Assert.False(registry.ReportHealthy("metrics"));
        Assert.True(registry.HasActiveFaults);
    }

    [Fact]
    public void Severity_only_ever_rises()
    {
        // The persistent diagnosis is the one saying the damage will not heal on its own, so a later
        // transient report must not downgrade it back into something self-clearing.
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);
        registry.Report("migration", FaultSeverity.Transient, "locked", Origin);
        registry.Report("migration", FaultSeverity.Persistent, "migration 3 failed", Origin.AddSeconds(1));
        registry.Report("migration", FaultSeverity.Transient, "locked", Origin.AddSeconds(2));

        Assert.False(registry.ReportHealthy("migration"));
        Assert.Equal(FaultSeverity.Persistent, Assert.Single(registry.Active).Severity);
    }

    [Fact]
    public void Explicitly_clearing_removes_even_a_persistent_fault()
    {
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);
        registry.Report("config", FaultSeverity.Persistent, "config.json failed validation", Origin);

        Assert.True(registry.Clear("config"));
        Assert.False(registry.HasActiveFaults);
        Assert.False(registry.Clear("config"));
    }

    [Fact]
    public void Re_reporting_keeps_the_time_the_fault_was_first_seen()
    {
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);
        registry.Report("metrics", FaultSeverity.Transient, "first", Origin);
        registry.Report("metrics", FaultSeverity.Transient, "second", Origin.AddMinutes(5));

        var fault = Assert.Single(registry.Active);
        Assert.Equal(Origin, fault.FirstSeenUtc);
        Assert.Equal(Origin.AddMinutes(5), fault.LastSeenUtc);
        Assert.Equal("second", fault.Message);
    }

    [Fact]
    public void Every_active_fault_is_itself_a_reason_to_stay_awake()
    {
        // If the evidence used to decide it is safe to release cannot be trusted, the safe response is
        // to keep holding.
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);
        registry.Report("config", FaultSeverity.Persistent, "config.json failed validation", Origin);

        var inhibitor = Assert.Single(registry.ToInhibitors());

        Assert.Equal(InhibitorKind.Fault, inhibitor.Kind);
        Assert.Equal("config.json failed validation", inhibitor.Reason);
        Assert.Equal("config", inhibitor.Detail);
        Assert.Equal(Origin, inhibitor.SinceUtc);
    }

    [Fact]
    public void A_registry_with_no_faults_contributes_no_inhibitors()
    {
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);

        Assert.False(registry.HasActiveFaults);
        Assert.Empty(registry.ToInhibitors());
    }

    [Fact]
    public void Faults_are_listed_in_a_stable_order()
    {
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);
        registry.Report("zeta", FaultSeverity.Transient, "z", Origin);
        registry.Report("alpha", FaultSeverity.Transient, "a", Origin);
        registry.Report("mid", FaultSeverity.Transient, "m", Origin);

        Assert.Equal(["alpha", "mid", "zeta"], registry.Active.Select(f => f.Key));
    }

    [Fact]
    public void Reporting_healthy_for_an_unknown_key_does_nothing()
    {
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);

        Assert.False(registry.ReportHealthy("never-failed"));
        Assert.False(registry.HasActiveFaults);
    }

    [Fact]
    public void Clearing_a_transient_fault_requires_at_least_one_healthy_observation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FaultRegistry(0));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_fault_needs_a_key_and_a_message(string? blank)
    {
        var registry = new FaultRegistry(consecutiveHealthyToClearTransient: 1);

        Assert.ThrowsAny<ArgumentException>(
            () => registry.Report(blank!, FaultSeverity.Transient, "message", Origin));
        Assert.ThrowsAny<ArgumentException>(
            () => registry.Report("key", FaultSeverity.Transient, blank!, Origin));
        Assert.ThrowsAny<ArgumentException>(() => registry.ReportHealthy(blank!));
        Assert.ThrowsAny<ArgumentException>(() => registry.Clear(blank!));
    }
}

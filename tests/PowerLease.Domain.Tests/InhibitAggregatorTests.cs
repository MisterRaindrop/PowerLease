using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class InhibitAggregatorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly InhibitorKind[] AllKinds = Enum.GetValues<InhibitorKind>();

    private static Inhibitor Ssh(string detail = "10.0.0.5") =>
        new(InhibitorKind.SshSession, "SSH session", Origin, detail);

    [Fact]
    public void Protection_is_released_only_when_every_source_confirmed_absence()
    {
        var decision = InhibitAggregator.Aggregate(
            [InhibitorSourceReport.ConfirmedAbsent("ssh"), InhibitorSourceReport.ConfirmedAbsent("locks")],
            AllKinds,
            Origin);

        Assert.False(decision.ShouldHold);
        Assert.Empty(decision.Inhibitors);
    }

    [Fact]
    public void One_observed_inhibitor_among_confirmed_absences_still_holds()
    {
        var decision = InhibitAggregator.Aggregate(
            [
                InhibitorSourceReport.ConfirmedAbsent("locks"),
                InhibitorSourceReport.Observed("ssh", Ssh()),
                InhibitorSourceReport.ConfirmedAbsent("processes")
            ],
            AllKinds,
            Origin);

        Assert.True(decision.ShouldHold);
        Assert.Equal(InhibitorKind.SshSession, Assert.Single(decision.Inhibitors).Kind);
    }

    [Fact]
    public void An_indeterminate_source_holds_even_though_it_observed_nothing()
    {
        var decision = InhibitAggregator.Aggregate(
            [
                InhibitorSourceReport.ConfirmedAbsent("locks"),
                InhibitorSourceReport.Indeterminate("ssh", "event log unreadable")
            ],
            AllKinds,
            Origin);

        Assert.True(decision.ShouldHold);

        var synthesised = Assert.Single(decision.Inhibitors);
        Assert.Equal(InhibitorKind.ProducerUnhealthy, synthesised.Kind);
        Assert.Equal("event log unreadable", synthesised.Reason);
        Assert.Equal("ssh", synthesised.Detail);
        Assert.Equal(Origin, synthesised.SinceUtc);
    }

    [Fact]
    public void No_report_can_remove_another_sources_protection()
    {
        // The safety property the whole aggregation exists for. Enumerating every subset of a small
        // pool and then adding each remaining report checks it exhaustively rather than by example:
        // if a decision holds, no additional report may turn it into a release.
        InhibitorSourceReport[] pool =
        [
            InhibitorSourceReport.ConfirmedAbsent("locks"),
            InhibitorSourceReport.Observed("ssh", Ssh()),
            InhibitorSourceReport.Indeterminate("metrics", "sampler restarted"),
            InhibitorSourceReport.ConfirmedAbsent("processes")
        ];

        for (var subset = 0; subset < 1 << pool.Length; subset++)
        {
            var reports = new List<InhibitorSourceReport>();
            for (var i = 0; i < pool.Length; i++)
            {
                if ((subset & (1 << i)) != 0)
                {
                    reports.Add(pool[i]);
                }
            }

            var holdsBefore = InhibitAggregator.Aggregate(reports, AllKinds, Origin).ShouldHold;
            if (!holdsBefore)
            {
                continue;
            }

            for (var i = 0; i < pool.Length; i++)
            {
                if ((subset & (1 << i)) != 0)
                {
                    continue;
                }

                var extended = new List<InhibitorSourceReport>(reports) { pool[i] };
                Assert.True(
                    InhibitAggregator.Aggregate(extended, AllKinds, Origin).ShouldHold,
                    $"Adding report {i} to subset {subset} released protection.");
            }
        }
    }

    [Fact]
    public void Two_sources_reporting_the_same_kind_are_both_listed()
    {
        // Two SSH sessions are two reasons to stay awake, and status has to be able to show both.
        var decision = InhibitAggregator.Aggregate(
            [
                InhibitorSourceReport.Observed("ssh-a", Ssh("10.0.0.5")),
                InhibitorSourceReport.Observed("ssh-b", Ssh("10.0.0.6"))
            ],
            AllKinds,
            Origin);

        Assert.Equal(2, decision.Inhibitors.Count);
        Assert.Equal(["10.0.0.5", "10.0.0.6"], decision.Inhibitors.Select(i => i.Detail));
    }

    [Fact]
    public void Kinds_nobody_evaluates_are_reported_but_do_not_hold()
    {
        // A rule the user switched off must not pin the machine awake forever; it is a coverage gap
        // to display, not a reason to hold.
        var decision = InhibitAggregator.Aggregate(
            [InhibitorSourceReport.ConfirmedAbsent("ssh")],
            [InhibitorKind.SshSession],
            Origin);

        Assert.False(decision.ShouldHold);
        Assert.DoesNotContain(InhibitorKind.SshSession, decision.UncoveredKinds);
        Assert.Contains(InhibitorKind.LockFile, decision.UncoveredKinds);
        Assert.Equal(AllKinds.Length - 1, decision.UncoveredKinds.Count);
    }

    [Fact]
    public void Nothing_covered_reports_every_kind_as_uncovered()
    {
        var decision = InhibitAggregator.Aggregate([], [], Origin);

        Assert.False(decision.ShouldHold);
        Assert.Equal(AllKinds, decision.UncoveredKinds);
    }

    [Fact]
    public void The_inhibitor_order_does_not_depend_on_the_order_the_reports_arrived()
    {
        // Status output that reshuffles between polls reads like something changed when nothing did.
        var later = Origin.AddMinutes(5);
        var reports = new List<InhibitorSourceReport>
        {
            InhibitorSourceReport.Observed("locks", new Inhibitor(InhibitorKind.LockFile, "build.lock", later)),
            InhibitorSourceReport.Observed("ssh", Ssh("10.0.0.6")),
            InhibitorSourceReport.Observed("ssh2", Ssh("10.0.0.5")),
            InhibitorSourceReport.Indeterminate("metrics", "sampler restarted")
        };

        var forwards = InhibitAggregator.Aggregate(reports, AllKinds, Origin).Inhibitors;
        reports.Reverse();
        var backwards = InhibitAggregator.Aggregate(reports, AllKinds, Origin).Inhibitors;

        Assert.Equal(forwards, backwards);

        // Ordered by kind first, so the enum order drives the listing.
        Assert.Equal(
            [InhibitorKind.SshSession, InhibitorKind.SshSession, InhibitorKind.LockFile, InhibitorKind.ProducerUnhealthy],
            forwards.Select(i => i.Kind));

        // Then by the remaining fields, which is what makes two SSH inhibitors stably ordered.
        Assert.Equal("10.0.0.5", forwards[0].Detail);
        Assert.Equal("10.0.0.6", forwards[1].Detail);
    }

    [Fact]
    public void Inhibitors_of_the_same_kind_are_ordered_by_when_they_started()
    {
        // The oldest SSH session listed first. The details are chosen so that ordering by detail would
        // give the opposite answer, which is what pins the time as the higher-priority key.
        var decision = InhibitAggregator.Aggregate(
            [
                InhibitorSourceReport.Observed(
                    "ssh-new", new Inhibitor(InhibitorKind.SshSession, "SSH session", Origin.AddMinutes(5), "aaa")),
                InhibitorSourceReport.Observed(
                    "ssh-old", new Inhibitor(InhibitorKind.SshSession, "SSH session", Origin, "zzz"))
            ],
            AllKinds,
            Origin);

        Assert.Equal(["zzz", "aaa"], decision.Inhibitors.Select(i => i.Detail));
    }

    [Fact]
    public void Mutating_the_array_a_source_reported_does_not_change_a_later_decision()
    {
        var inhibitors = new[] { Ssh() };
        var report = InhibitorSourceReport.Observed("ssh", inhibitors);

        inhibitors[0] = new Inhibitor(InhibitorKind.LockFile, "tampered", Origin);

        var decision = InhibitAggregator.Aggregate([report], AllKinds, Origin);
        Assert.Equal(InhibitorKind.SshSession, Assert.Single(decision.Inhibitors).Kind);
    }

    [Fact]
    public void A_null_report_is_rejected_rather_than_skipped()
    {
        // Skipping it would silently drop a source, which reads as "that source confirmed absence".
        Assert.Throws<ArgumentException>(() =>
            InhibitAggregator.Aggregate([InhibitorSourceReport.ConfirmedAbsent("ssh"), null!], AllKinds, Origin));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => InhibitAggregator.Aggregate(null!, AllKinds, Origin));
        Assert.Throws<ArgumentNullException>(() => InhibitAggregator.Aggregate([], null!, Origin));
    }

    [Fact]
    public void A_decision_listing_reasons_can_never_also_say_it_is_safe_to_release()
    {
        var decision = new InhibitDecision([Ssh()], []);
        Assert.True(decision.ShouldHold);

        Assert.Throws<ArgumentNullException>(() => new InhibitDecision(null!, []));
        Assert.Throws<ArgumentNullException>(() => new InhibitDecision([], null!));
    }
}

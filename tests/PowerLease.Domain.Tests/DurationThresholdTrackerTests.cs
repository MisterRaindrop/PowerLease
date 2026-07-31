using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class DurationThresholdTrackerTests
{
    private const double Threshold = 10;

    private static DurationThresholdTracker Tracker(double requiredQuietMinutes = 5, double maxGapSeconds = 60) =>
        new(Threshold, TimeSpan.FromMinutes(requiredQuietMinutes), TimeSpan.FromSeconds(maxGapSeconds));

    /// <summary>Feed one sample every <paramref name="everySeconds" /> from 0 up to and including the end.</summary>
    private static void Feed(DurationThresholdTracker tracker, double untilSeconds, double value, double everySeconds = 30)
    {
        for (var at = 0.0; at <= untilSeconds; at += everySeconds)
        {
            tracker.Observe(Stamps.Seconds(at), value);
        }
    }

    [Fact]
    public void With_no_samples_nothing_can_be_established_and_the_metric_inhibits()
    {
        var assessment = Tracker().Assess(Stamps.Minutes(0));

        Assert.Equal(ActivityVerdict.InsufficientData, assessment.Verdict);
        Assert.True(assessment.Inhibits);
    }

    [Fact]
    public void A_single_measurement_above_the_threshold_inhibits_immediately()
    {
        // Asymmetric on purpose: one busy sample is enough to keep the machine awake, while releasing
        // needs continuous evidence across the whole required duration.
        var tracker = Tracker();
        tracker.Observe(Stamps.Minutes(0), Threshold + 1);

        var assessment = tracker.Assess(Stamps.Minutes(0));

        Assert.Equal(ActivityVerdict.Active, assessment.Verdict);
        Assert.True(assessment.Inhibits);
    }

    [Fact]
    public void Continuous_quiet_for_the_full_required_duration_stops_inhibiting()
    {
        var tracker = Tracker(requiredQuietMinutes: 5);
        Feed(tracker, untilSeconds: 360, value: 0);

        var assessment = tracker.Assess(Stamps.Minutes(6));

        Assert.Equal(ActivityVerdict.QuietLongEnough, assessment.Verdict);
        Assert.False(assessment.Inhibits);
        Assert.Equal(TimeSpan.FromMinutes(6), assessment.QuietFor);
    }

    [Fact]
    public void Quiet_that_has_not_lasted_long_enough_still_inhibits_and_reports_progress()
    {
        var tracker = Tracker(requiredQuietMinutes: 5);
        Feed(tracker, untilSeconds: 120, value: 0);

        var assessment = tracker.Assess(Stamps.Minutes(2));

        Assert.Equal(ActivityVerdict.InsufficientData, assessment.Verdict);
        Assert.True(assessment.Inhibits);
        Assert.Equal(TimeSpan.FromMinutes(2), assessment.QuietFor);
    }

    [Fact]
    public void A_source_that_stopped_reporting_cannot_establish_quiet()
    {
        // The metric went quiet in the wrong sense. Anything could have happened since the last
        // sample, so nothing can be concluded from it.
        var tracker = Tracker(requiredQuietMinutes: 5);
        Feed(tracker, untilSeconds: 360, value: 0);

        var assessment = tracker.Assess(Stamps.Minutes(10));

        Assert.Equal(ActivityVerdict.InsufficientData, assessment.Verdict);
        Assert.True(assessment.Inhibits);
    }

    [Fact]
    public void A_gap_wide_enough_to_have_hidden_activity_cannot_establish_quiet()
    {
        // Two quiet samples three minutes apart do not prove the three minutes between them were
        // quiet. Without this check, a sampler that stalls would look like proof of idleness.
        var tracker = Tracker(requiredQuietMinutes: 3, maxGapSeconds: 60);
        tracker.Observe(Stamps.Seconds(0), 0);
        tracker.Observe(Stamps.Seconds(180), 0);

        var assessment = tracker.Assess(Stamps.Seconds(180));

        Assert.Equal(ActivityVerdict.InsufficientData, assessment.Verdict);
        Assert.True(assessment.Inhibits);
    }

    [Fact]
    public void The_same_span_covered_without_a_gap_does_establish_quiet()
    {
        // Same three minutes as the previous test, same values; only the intermediate samples differ.
        var tracker = Tracker(requiredQuietMinutes: 3, maxGapSeconds: 60);
        Feed(tracker, untilSeconds: 180, value: 0, everySeconds: 60);

        var assessment = tracker.Assess(Stamps.Seconds(180));

        Assert.Equal(ActivityVerdict.QuietLongEnough, assessment.Verdict);
        Assert.False(assessment.Inhibits);
    }

    [Fact]
    public void Activity_older_than_the_required_quiet_duration_no_longer_inhibits()
    {
        var tracker = Tracker(requiredQuietMinutes: 3, maxGapSeconds: 60);
        tracker.Observe(Stamps.Seconds(0), Threshold + 40);
        for (var at = 60.0; at <= 240.0; at += 60.0)
        {
            tracker.Observe(Stamps.Seconds(at), 0);
        }

        var assessment = tracker.Assess(Stamps.Seconds(240));

        Assert.Equal(ActivityVerdict.QuietLongEnough, assessment.Verdict);
        Assert.Equal(TimeSpan.FromMinutes(3), assessment.QuietFor);
    }

    [Fact]
    public void Activity_inside_the_required_window_inhibits_even_with_quiet_since()
    {
        var tracker = Tracker(requiredQuietMinutes: 5, maxGapSeconds: 60);
        tracker.Observe(Stamps.Seconds(0), Threshold + 40);
        for (var at = 60.0; at <= 180.0; at += 60.0)
        {
            tracker.Observe(Stamps.Seconds(at), 0);
        }

        var assessment = tracker.Assess(Stamps.Seconds(180));

        Assert.Equal(ActivityVerdict.Active, assessment.Verdict);
        Assert.True(assessment.Inhibits);
        Assert.Equal(TimeSpan.FromMinutes(2), assessment.QuietFor);
    }

    [Fact]
    public void A_change_of_clock_epoch_discards_the_history_and_inhibits_again()
    {
        // What a resume looks like: samples from before the machine slept say nothing about now.
        var tracker = Tracker(requiredQuietMinutes: 5);
        Feed(tracker, untilSeconds: 360, value: 0);
        Assert.Equal(ActivityVerdict.QuietLongEnough, tracker.Assess(Stamps.Minutes(6)).Verdict);

        tracker.Observe(Stamps.Minutes(0, Stamps.EpochB), 0);
        var assessment = tracker.Assess(Stamps.Minutes(0, Stamps.EpochB));

        Assert.Equal(ActivityVerdict.InsufficientData, assessment.Verdict);
        Assert.True(assessment.Inhibits);
        Assert.Equal(1, tracker.DiscontinuityCount);
    }

    [Fact]
    public void A_measurement_exactly_at_the_threshold_counts_as_quiet()
    {
        var tracker = Tracker(requiredQuietMinutes: 3, maxGapSeconds: 60);
        Feed(tracker, untilSeconds: 180, value: Threshold, everySeconds: 60);

        Assert.Equal(ActivityVerdict.QuietLongEnough, tracker.Assess(Stamps.Seconds(180)).Verdict);
    }

    [Fact]
    public void A_sample_exactly_as_old_as_the_allowed_gap_is_still_fresh()
    {
        var tracker = Tracker(requiredQuietMinutes: 3, maxGapSeconds: 60);
        Feed(tracker, untilSeconds: 180, value: 0, everySeconds: 60);

        Assert.Equal(ActivityVerdict.QuietLongEnough, tracker.Assess(Stamps.Seconds(240)).Verdict);
    }

    [Fact]
    public void A_not_a_number_threshold_is_rejected_because_it_would_classify_everything_as_quiet()
    {
        // NaN compares false against everything, so every sample would read as below the threshold and
        // protection would be released. This must not be allowed to fail open.
        Assert.Throws<ArgumentException>(
            () => new DurationThresholdTracker(double.NaN, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_not_a_number_sample_is_rejected_for_the_same_reason()
    {
        var tracker = Tracker();

        Assert.Throws<ArgumentException>(() => tracker.Observe(Stamps.Minutes(0), double.NaN));
    }

    [Fact]
    public void The_required_duration_and_the_allowed_gap_must_both_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DurationThresholdTracker(Threshold, TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DurationThresholdTracker(Threshold, TimeSpan.FromMinutes(1), TimeSpan.Zero));
    }

    [Fact]
    public void The_retained_sample_count_is_reported()
    {
        var tracker = Tracker(requiredQuietMinutes: 5);
        Feed(tracker, untilSeconds: 120, value: 0, everySeconds: 60);

        Assert.Equal(3, tracker.SampleCount);
    }
}

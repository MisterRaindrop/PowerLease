using PowerLease.Domain;
using PowerLease.Persistence.History;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class AggregationTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Noon = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static MinuteAggregator Minutes(HistoryFixture fixture) => new(fixture.Store, Interval);

    [Fact]
    public void Measurements_are_grouped_into_the_minute_they_belong_to()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(0), cpu: 10);
        fixture.AddSample(Noon.AddSeconds(30), cpu: 20);
        fixture.AddSample(Noon.AddSeconds(70), cpu: 90);

        var result = Minutes(fixture).Run(Noon.AddMinutes(2));

        Assert.Equal(2, result.BucketsWritten);
        var buckets = fixture.Store.ReadMinuteAggregates(Noon, Noon.AddMinutes(5));
        Assert.Equal([Noon, Noon.AddMinutes(1)], buckets.Select(bucket => bucket.BucketStartUtc));
        Assert.Equal(15, buckets[0].CpuAverage);
        Assert.Equal(10, buckets[0].CpuMinimum);
        Assert.Equal(20, buckets[0].CpuMaximum);
        Assert.Equal(90, buckets[1].CpuAverage);
    }

    [Fact]
    public void The_minute_still_in_progress_is_left_alone()
    {
        // Aggregating it would produce a bucket holding part of a minute and then move past it, and the
        // measurements still to arrive would have nowhere to go.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(30));
        fixture.AddSample(Noon.AddSeconds(90));

        var result = Minutes(fixture).Run(Noon.AddSeconds(105));

        Assert.Equal(1, result.BucketsWritten);
        Assert.Equal(Noon.AddMinutes(1), result.CompletedThroughUtc);
        Assert.Equal(Noon, Assert.Single(fixture.Store.ReadMinuteAggregates(Noon, Noon.AddMinutes(5))).BucketStartUtc);
    }

    [Fact]
    public void Running_again_over_the_same_period_produces_the_same_buckets_not_more()
    {
        // What makes a crash between aggregating and recording progress safe: the work is simply redone.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(10), cpu: 40);
        Minutes(fixture).Run(Noon.AddMinutes(2));

        using (var reset = fixture.Store.BeginTransaction())
        {
            reset.SetWatermark(MinuteAggregator.WatermarkName, Noon);
            reset.Commit();
        }

        var second = Minutes(fixture).Run(Noon.AddMinutes(2));

        Assert.Equal(1, second.BucketsWritten);
        Assert.Equal(1, fixture.Store.CountMinuteAggregates());
        Assert.Equal(40, Assert.Single(fixture.Store.ReadMinuteAggregates(Noon, Noon.AddMinutes(5))).CpuAverage);
    }

    [Fact]
    public void Running_with_nothing_new_finished_does_nothing()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(10));
        Minutes(fixture).Run(Noon.AddMinutes(2));

        var second = Minutes(fixture).Run(Noon.AddMinutes(2));

        Assert.Equal(0, second.BucketsWritten);
        Assert.Equal(Noon.AddMinutes(2), second.CompletedThroughUtc);
    }

    [Fact]
    public void A_clock_that_moved_backwards_does_not_move_progress_backwards()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(10));
        Minutes(fixture).Run(Noon.AddMinutes(5));

        var backwards = Minutes(fixture).Run(Noon.AddMinutes(1));

        Assert.Equal(0, backwards.BucketsWritten);
        Assert.Equal(Noon.AddMinutes(5), fixture.Store.GetWatermark(MinuteAggregator.WatermarkName));
    }

    [Fact]
    public void A_minute_with_no_measurements_produces_no_bucket()
    {
        // The absence of a row is how "the machine was not running then" is recorded. A row of zeroes
        // would read as a perfectly idle machine.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(10));
        fixture.AddSample(Noon.AddMinutes(3).AddSeconds(10));

        Minutes(fixture).Run(Noon.AddMinutes(5));

        Assert.Equal(
            [Noon, Noon.AddMinutes(3)],
            fixture.Store.ReadMinuteAggregates(Noon, Noon.AddMinutes(5)).Select(bucket => bucket.BucketStartUtc));
    }

    [Fact]
    public void Time_in_each_state_comes_from_the_sampling_interval()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(0), state: ProtectionState.Protected);
        fixture.AddSample(Noon.AddSeconds(10), state: ProtectionState.Protected);
        fixture.AddSample(Noon.AddSeconds(20), state: ProtectionState.Protected);
        fixture.AddSample(Noon.AddSeconds(30), state: ProtectionState.Released);
        fixture.AddSample(Noon.AddSeconds(40), state: ProtectionState.Unprotected);
        fixture.AddSample(Noon.AddSeconds(50), state: ProtectionState.Unprotected);

        Minutes(fixture).Run(Noon.AddMinutes(2));

        var bucket = Assert.Single(fixture.Store.ReadMinuteAggregates(Noon, Noon.AddMinutes(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), bucket.ProtectedFor);
        Assert.Equal(TimeSpan.FromSeconds(10), bucket.ReleasedFor);
        Assert.Equal(TimeSpan.FromSeconds(20), bucket.UnprotectedFor);
    }

    [Fact]
    public void Completeness_says_how_much_of_the_minute_was_actually_measured()
    {
        // A bucket built from two measurements must not read like one built from six.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(0));
        fixture.AddSample(Noon.AddSeconds(10));

        Minutes(fixture).Run(Noon.AddMinutes(2));

        var bucket = Assert.Single(fixture.Store.ReadMinuteAggregates(Noon, Noon.AddMinutes(5)));
        Assert.Equal(2, bucket.SampleCount);
        Assert.Equal(6, bucket.ExpectedSampleCount);
        Assert.Equal(1.0 / 3, bucket.Completeness, 6);
    }

    [Fact]
    public void The_highest_number_of_sessions_seen_is_kept()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(0), sshSessions: 1);
        fixture.AddSample(Noon.AddSeconds(10), sshSessions: 3);
        fixture.AddSample(Noon.AddSeconds(20), sshSessions: 0);

        Minutes(fixture).Run(Noon.AddMinutes(2));

        Assert.Equal(3, Assert.Single(fixture.Store.ReadMinuteAggregates(Noon, Noon.AddMinutes(5))).SshSessionMaximum);
    }

    [Fact]
    public void An_hour_is_only_summarised_once_every_minute_in_it_has_been()
    {
        // The bound comes from how far minute aggregation has got, not from the clock. Summarising from
        // minutes that are still missing would produce an hour that looks complete and is not.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon.AddSeconds(10), cpu: 10);
        fixture.AddSample(Noon.AddMinutes(30), cpu: 20);

        Minutes(fixture).Run(Noon.AddMinutes(31));
        Assert.Equal(0, new HourAggregator(fixture.Store).Run().BucketsWritten);

        // Once minute aggregation has passed the end of the hour, the hour can be summarised.
        fixture.AddSample(Noon.AddHours(1).AddSeconds(10), cpu: 30);
        Minutes(fixture).Run(Noon.AddHours(1).AddMinutes(1));

        var hours = new HourAggregator(fixture.Store).Run();
        Assert.Equal(1, hours.BucketsWritten);
        Assert.Equal(Noon, Assert.Single(fixture.Store.ReadHourAggregates(Noon, Noon.AddHours(5))).BucketStartUtc);
    }

    [Fact]
    public void An_hour_average_is_weighted_by_how_many_measurements_each_minute_held()
    {
        // A minute built from one measurement must not count as much as a minute built from six. The
        // unweighted mean of these two would be 55; the weighted one is about 22.9.
        using var fixture = new HistoryFixture();
        fixture.AddMinuteBucket(Noon, sampleCount: 6, cpuAverage: 10);
        fixture.AddMinuteBucket(Noon.AddMinutes(1), sampleCount: 1, cpuAverage: 100);

        using (var progress = fixture.Store.BeginTransaction())
        {
            progress.SetWatermark(MinuteAggregator.WatermarkName, Noon.AddHours(1));
            progress.Commit();
        }

        new HourAggregator(fixture.Store).Run();

        var hour = Assert.Single(fixture.Store.ReadHourAggregates(Noon, Noon.AddHours(2)));
        Assert.Equal(160.0 / 7, hour.CpuAverage!.Value, 6);
        Assert.Equal(7, hour.SampleCount);
    }

    [Fact]
    public void A_metric_missing_from_some_minutes_does_not_drag_the_hour_average_down()
    {
        // The weight has to exclude minutes where the metric itself was absent. Counting them would put
        // twelve in the divisor for six measurements' worth of readings and halve the average.
        using var fixture = new HistoryFixture();
        fixture.AddMinuteBucket(Noon, sampleCount: 6, cpuAverage: 10);
        fixture.AddMinuteBucket(Noon.AddMinutes(1), sampleCount: 6, cpuAverage: null);

        using (var progress = fixture.Store.BeginTransaction())
        {
            progress.SetWatermark(MinuteAggregator.WatermarkName, Noon.AddHours(1));
            progress.Commit();
        }

        new HourAggregator(fixture.Store).Run();

        Assert.Equal(10, Assert.Single(fixture.Store.ReadHourAggregates(Noon, Noon.AddHours(2))).CpuAverage);
    }

    [Fact]
    public void An_hour_with_no_readings_at_all_reports_no_average_rather_than_zero()
    {
        using var fixture = new HistoryFixture();
        fixture.AddMinuteBucket(Noon, sampleCount: 6, cpuAverage: null);

        using (var progress = fixture.Store.BeginTransaction())
        {
            progress.SetWatermark(MinuteAggregator.WatermarkName, Noon.AddHours(1));
            progress.Commit();
        }

        new HourAggregator(fixture.Store).Run();

        Assert.Null(Assert.Single(fixture.Store.ReadHourAggregates(Noon, Noon.AddHours(2))).CpuAverage);
    }

    [Fact]
    public void Hour_aggregation_does_nothing_before_minute_aggregation_has_ever_run()
    {
        using var fixture = new HistoryFixture();
        fixture.AddMinuteBucket(Noon, sampleCount: 6, cpuAverage: 10);

        var result = new HourAggregator(fixture.Store).Run();

        Assert.Equal(0, result.BucketsWritten);
        Assert.Null(result.CompletedThroughUtc);
    }

    [Fact]
    public void Aggregating_with_no_measurements_at_all_is_harmless()
    {
        using var fixture = new HistoryFixture();

        Assert.Equal(0, Minutes(fixture).Run(Noon).BucketsWritten);
        Assert.Equal(0, new HourAggregator(fixture.Store).Run().BucketsWritten);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        using var fixture = new HistoryFixture();

        Assert.Throws<ArgumentNullException>(() => new MinuteAggregator(null!, Interval));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinuteAggregator(fixture.Store, TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new HourAggregator(null!));
    }
}

using PowerLease.Persistence.Configuration;
using PowerLease.Persistence.History;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class RetentionPolicyTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly RetentionOptions ShortLived =
        new() { RawHours = 1, MinuteDays = 1, EventDays = 1 };

    [Fact]
    public void Measurements_are_not_deleted_before_they_have_been_aggregated()
    {
        // The rule that makes retention safe. These measurements are far past the age limit, but nothing
        // has summarised them yet, so deleting them would destroy the only copy of that history.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon);

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddDays(10));

        Assert.Equal(0, result.RawSamplesDeleted);
        Assert.Equal(1, fixture.Store.CountRawSamples());
    }

    [Fact]
    public void Aggregated_measurements_past_the_age_limit_are_deleted()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon);
        new MinuteAggregator(fixture.Store, TimeSpan.FromSeconds(10)).Run(Noon.AddMinutes(2));

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddDays(10));

        Assert.Equal(1, result.RawSamplesDeleted);
        Assert.Equal(0, fixture.Store.CountRawSamples());

        // The summary built from them survives, which is the point of aggregating first.
        Assert.Equal(1, fixture.Store.CountMinuteAggregates());
    }

    [Fact]
    public void Aggregated_measurements_inside_the_age_limit_are_kept()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon);
        new MinuteAggregator(fixture.Store, TimeSpan.FromSeconds(10)).Run(Noon.AddMinutes(2));

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddMinutes(30));

        Assert.Equal(0, result.RawSamplesDeleted);
        Assert.Equal(1, fixture.Store.CountRawSamples());
    }

    [Fact]
    public void The_cutoff_never_runs_past_what_aggregation_has_finished()
    {
        // Age would allow deleting everything before 11:00 the next day; aggregation has only reached
        // 12:01, so that is where the cutoff sits.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon);
        new MinuteAggregator(fixture.Store, TimeSpan.FromSeconds(10)).Run(Noon.AddMinutes(2));

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddDays(1));

        Assert.Equal(Noon.AddMinutes(2), result.RawCutoffUtc);
    }

    [Fact]
    public void Minute_buckets_are_not_deleted_before_the_hour_covering_them_exists()
    {
        using var fixture = new HistoryFixture();
        fixture.AddMinuteBucket(Noon, sampleCount: 6, cpuAverage: 10);

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddDays(10));

        Assert.Equal(0, result.MinuteAggregatesDeleted);
        Assert.Equal(1, fixture.Store.CountMinuteAggregates());
    }

    [Fact]
    public void Minute_buckets_past_the_age_limit_go_once_the_hour_has_been_summarised()
    {
        using var fixture = new HistoryFixture();
        fixture.AddMinuteBucket(Noon, sampleCount: 6, cpuAverage: 10);

        using (var progress = fixture.Store.BeginTransaction())
        {
            progress.SetWatermark(MinuteAggregator.WatermarkName, Noon.AddHours(1));
            progress.Commit();
        }

        new HourAggregator(fixture.Store).Run();
        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddDays(10));

        Assert.Equal(1, result.MinuteAggregatesDeleted);
        Assert.Equal(0, fixture.Store.CountMinuteAggregates());

        // Hour buckets are the long-term record and are never deleted.
        Assert.Single(fixture.Store.ReadHourAggregates(Noon, Noon.AddHours(2)));
    }

    [Fact]
    public void Nothing_is_deleted_at_all_before_aggregation_has_ever_run()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        fixture.AddSample(Noon);
        fixture.AddMinuteBucket(Noon, sampleCount: 6, cpuAverage: 10);

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddYears(1));

        Assert.Equal(0, result.RawSamplesDeleted);
        Assert.Equal(0, result.MinuteAggregatesDeleted);
        Assert.Equal(DateTimeOffset.MinValue, result.RawCutoffUtc);
        Assert.Equal(DateTimeOffset.MinValue, result.MinuteCutoffUtc);
    }

    [Fact]
    public void Events_age_out_on_their_own_without_waiting_for_aggregation()
    {
        // Nothing is derived from an event, so there is no summary to protect.
        using var fixture = new HistoryFixture();
        fixture.Store.RecordInhibitEvent(new InhibitEvent
        {
            OccurredAtUtc = Noon,
            Change = InhibitChange.Established,
            ProtectionState = Domain.ProtectionState.Protected,
            Revision = 1
        });

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddDays(10));

        Assert.Equal(1, result.EventsDeleted);
        Assert.Equal(0, fixture.Store.CountInhibitEvents());
    }

    [Fact]
    public void Events_inside_the_age_limit_are_kept()
    {
        using var fixture = new HistoryFixture();
        fixture.Store.RecordInhibitEvent(new InhibitEvent
        {
            OccurredAtUtc = Noon,
            Change = InhibitChange.Established,
            ProtectionState = Domain.ProtectionState.Protected,
            Revision = 1
        });

        var result = new RetentionPolicy(fixture.Store, ShortLived).Apply(Noon.AddHours(2));

        Assert.Equal(0, result.EventsDeleted);
        Assert.Equal(1, fixture.Store.CountInhibitEvents());
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        using var fixture = new HistoryFixture();

        Assert.Throws<ArgumentNullException>(() => new RetentionPolicy(null!, ShortLived));
        Assert.Throws<ArgumentNullException>(() => new RetentionPolicy(fixture.Store, null!));
    }
}

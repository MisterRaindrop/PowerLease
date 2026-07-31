using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class SlidingWindowTests
{
    [Fact]
    public void Samples_that_fall_outside_the_window_are_dropped()
    {
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));

        window.Add(Stamps.Minutes(0), 1);
        window.Add(Stamps.Minutes(3), 2);
        window.Add(Stamps.Minutes(6), 3);

        Assert.Equal([2, 3], window.Samples.Select(s => s.Value));
    }

    [Fact]
    public void Pruning_drops_stale_samples_even_though_no_new_sample_arrived()
    {
        // A source that stops reporting must not keep looking current, so callers prune at their own
        // cadence rather than relying on the next Add.
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));
        window.Add(Stamps.Minutes(0), 1);

        window.Prune(Stamps.Minutes(10));

        Assert.Equal(0, window.Count);
        Assert.Null(window.EpochId);
    }

    [Fact]
    public void A_sample_from_a_new_epoch_discards_the_window_and_records_a_discontinuity()
    {
        // Elapsed values from two epochs count from different origins, so keeping both would let a
        // duration be computed that looks plausible and is wrong.
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));
        window.Add(Stamps.Minutes(0), 1);
        window.Add(Stamps.Minutes(1), 2);

        window.Add(Stamps.Minutes(0, Stamps.EpochB), 3);

        Assert.Equal([3], window.Samples.Select(s => s.Value));
        Assert.Equal(Stamps.EpochB, window.EpochId);
        Assert.Equal(1, window.DiscontinuityCount);
    }

    [Fact]
    public void Pruning_against_a_foreign_epoch_discards_everything()
    {
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));
        window.Add(Stamps.Minutes(0), 1);

        window.Prune(Stamps.Minutes(1, Stamps.EpochB));

        Assert.Equal(0, window.Count);
        Assert.Equal(1, window.DiscontinuityCount);
    }

    [Fact]
    public void An_out_of_order_sample_discards_the_window()
    {
        // A monotonic clock cannot go backwards, so this means a bug or a misbehaving producer.
        // Discarding leaves the caller with too little history, which makes it hold rather than guess.
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));
        window.Add(Stamps.Minutes(2), 1);

        window.Add(Stamps.Minutes(1), 2);

        Assert.Equal([2], window.Samples.Select(s => s.Value));
        Assert.Equal(1, window.DiscontinuityCount);
    }

    [Fact]
    public void Clearing_deliberately_is_not_a_discontinuity()
    {
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));
        window.Add(Stamps.Minutes(0), 1);

        window.Clear();

        Assert.Equal(0, window.Count);
        Assert.Equal(0, window.DiscontinuityCount);
    }

    [Fact]
    public void The_oldest_and_newest_samples_are_exposed()
    {
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));

        Assert.Null(window.Oldest);
        Assert.Null(window.Newest);

        window.Add(Stamps.Minutes(0), 1);
        window.Add(Stamps.Minutes(1), 2);

        Assert.Equal(1, window.Oldest!.Value.Value);
        Assert.Equal(2, window.Newest!.Value.Value);
        Assert.Equal(Stamps.Minutes(1), window.Newest!.Value.At);
    }

    [Fact]
    public void A_sample_exactly_at_the_window_edge_is_kept()
    {
        var window = new SlidingWindow<int>(TimeSpan.FromMinutes(5));

        window.Add(Stamps.Minutes(0), 1);
        window.Add(Stamps.Minutes(5), 2);

        Assert.Equal([1, 2], window.Samples.Select(s => s.Value));
    }

    [Fact]
    public void The_window_length_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindow<int>(TimeSpan.Zero));
    }
}

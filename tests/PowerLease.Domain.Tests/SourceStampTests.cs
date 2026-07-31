using PowerLease.Domain;
using Xunit;

namespace PowerLease.Domain.Tests;

public sealed class SourceStampTests
{
    private static SourceStamp Stamp(
        long sequence,
        long generation = 1,
        string sourceId = "ssh",
        Guid? epoch = null,
        long configGeneration = 1) =>
        new(sourceId, generation, sequence, Stamps.Seconds(sequence, epoch), configGeneration);

    [Fact]
    public void The_clock_epoch_comes_from_the_observation_so_the_two_cannot_disagree()
    {
        var stamp = Stamp(1, epoch: Stamps.EpochB);

        Assert.Equal(Stamps.EpochB, stamp.ClockEpochId);
        Assert.Equal(stamp.ObservedAt.EpochId, stamp.ClockEpochId);
    }

    [Fact]
    public void A_higher_sequence_from_the_same_run_supersedes()
    {
        Assert.True(Stamp(2).Supersedes(Stamp(1)));
    }

    [Fact]
    public void A_duplicate_does_not_supersede()
    {
        Assert.False(Stamp(1).Supersedes(Stamp(1)));
    }

    [Fact]
    public void An_out_of_order_arrival_does_not_supersede()
    {
        Assert.False(Stamp(1).Supersedes(Stamp(2)));
    }

    [Fact]
    public void Stamps_from_different_sources_are_not_comparable()
    {
        var ssh = Stamp(1, sourceId: "ssh");
        var locks = Stamp(2, sourceId: "locks");

        Assert.False(locks.IsComparableTo(ssh));
        Assert.False(locks.Supersedes(ssh));
    }

    [Fact]
    public void Stamps_from_different_runs_of_the_same_source_are_not_comparable()
    {
        // A restarted producer begins its sequence again, so a higher number from the new run says
        // nothing about the old one.
        var before = Stamp(9, generation: 1);
        var afterRestart = Stamp(1, generation: 2);

        Assert.False(afterRestart.IsComparableTo(before));
        Assert.False(afterRestart.Supersedes(before));
        Assert.False(before.Supersedes(afterRestart));
    }

    [Fact]
    public void Stamps_from_different_clock_epochs_are_not_comparable()
    {
        var before = Stamp(1, epoch: Stamps.EpochA);
        var afterResume = Stamp(2, epoch: Stamps.EpochB);

        Assert.False(afterResume.IsComparableTo(before));
        Assert.False(afterResume.Supersedes(before));
    }

    [Fact]
    public void Comparability_ignores_the_configuration_generation()
    {
        // Reloaded configuration does not restart the source's sequence, so it must not break
        // ordering; deciding what to do with a stale configuration generation is the kernel's job.
        var older = Stamp(1, configGeneration: 1);
        var newer = Stamp(2, configGeneration: 2);

        Assert.True(newer.IsComparableTo(older));
        Assert.True(newer.Supersedes(older));

        // The generation is still carried, because the kernel needs it to reject a result computed
        // under configuration that has since been replaced.
        Assert.Equal(1, older.ConfigGeneration);
        Assert.Equal(2, newer.ConfigGeneration);
    }
}

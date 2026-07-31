using Microsoft.Data.Sqlite;
using PowerLease.Domain;
using PowerLease.Persistence.History;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class SqliteHistoryStoreTests
{
    private static RawSample Sample(DateTimeOffset at, double cpu) => new()
    {
        PowerSessionId = HistoryFixture.SessionId,
        SampledAtUtc = at,
        CpuPercent = cpu,
        State = ProtectionState.Protected,
        SshSessionCount = 0
    };

    [Fact]
    public void A_lease_survives_the_round_trip_with_the_fields_needed_to_restore_it()
    {
        using var fixture = new HistoryFixture();
        var lease = fixture.Lease();

        fixture.Store.UpsertLease(lease);
        var loaded = Assert.Single(fixture.Store.LoadLeases());

        Assert.Equal(lease.Id, loaded.Id);
        Assert.Equal(lease.Source, loaded.Source);
        Assert.Equal(lease.Reason, loaded.Reason);
        Assert.Equal(lease.OwnerUser, loaded.OwnerUser);
        Assert.Equal(lease.RemoteIp, loaded.RemoteIp);
        Assert.Equal(lease.ProcessId, loaded.ProcessId);
        Assert.Equal(lease.ProcessName, loaded.ProcessName);
        Assert.Equal(lease.StartedAtUtc, loaded.StartedAtUtc);
        Assert.Equal(lease.ExpiresAtUtc, loaded.ExpiresAtUtc);
        Assert.Equal(lease.LastRenewedAtUtc, loaded.LastRenewedAtUtc);
        Assert.Equal(lease.AutoRenew, loaded.AutoRenew);
        Assert.Equal(lease.Status, loaded.Status);
        Assert.Equal(lease.EpochId, loaded.EpochId);
        Assert.Equal(lease.OriginalDuration, loaded.OriginalDuration);
        Assert.Equal(lease.LastRenewDuration, loaded.LastRenewDuration);
        Assert.Equal(lease.RemainingAtCheckpoint, loaded.RemainingAtCheckpoint);
        Assert.Equal(lease.CheckpointUtc, loaded.CheckpointUtc);
    }

    [Fact]
    public void A_persisted_lease_resumes_with_the_time_it_had_left()
    {
        // The point of storing the checkpoint at all: after a restart the lease continues with the
        // remaining time it was measured to have, not with a guess.
        using var fixture = new HistoryFixture();
        fixture.Store.UpsertLease(fixture.Lease());

        var loaded = Assert.Single(fixture.Store.LoadLeases());
        var resumed = LeaseDeadline.Resume(
            loaded.TryGetCheckpoint(),
            loaded.OriginalDuration,
            new MonotonicStamp(Guid.Parse("99999999-9999-9999-9999-999999999999"), TimeSpan.Zero));

        Assert.Equal(LeaseResumeDecision.RemainingFromCheckpoint, resumed.Decision);
        Assert.Equal(
            TimeSpan.FromMinutes(150),
            resumed.Deadline.RemainingAt(
                new MonotonicStamp(Guid.Parse("99999999-9999-9999-9999-999999999999"), TimeSpan.Zero)));
    }

    [Fact]
    public void Writing_a_lease_again_replaces_it_rather_than_adding_another()
    {
        using var fixture = new HistoryFixture();
        fixture.Store.UpsertLease(fixture.Lease());

        fixture.Store.UpsertLease(fixture.Lease() with
        {
            Status = LeaseStatus.Released,
            EndReason = "released by liu",
            RemainingAtCheckpoint = TimeSpan.FromMinutes(30)
        });

        var loaded = Assert.Single(fixture.Store.LoadLeases());
        Assert.Equal(LeaseStatus.Released, loaded.Status);
        Assert.Equal("released by liu", loaded.EndReason);
        Assert.Equal(TimeSpan.FromMinutes(30), loaded.RemainingAtCheckpoint);
    }

    [Fact]
    public void Leases_can_be_asked_for_by_status()
    {
        using var fixture = new HistoryFixture();
        fixture.Store.UpsertLease(fixture.Lease("active-1"));
        fixture.Store.UpsertLease(fixture.Lease("ended-1", LeaseStatus.Expired));

        Assert.Equal("active-1", Assert.Single(fixture.Store.LoadLeases(LeaseStatus.Active)).Id);
        Assert.Equal("ended-1", Assert.Single(fixture.Store.LoadLeases(LeaseStatus.Expired)).Id);
        Assert.Equal(2, fixture.Store.LoadLeases().Count);
    }

    [Fact]
    public void A_measurement_cannot_be_attributed_to_a_run_of_the_machine_that_does_not_exist()
    {
        // Without the foreign key, orphaned measurements would still aggregate and report history
        // belonging to no run of the machine.
        using var fixture = new HistoryFixture();

        Assert.Throws<SqliteException>(() => fixture.AddSample(fixture.Clock.UtcNow));

        fixture.StartSession();
        fixture.AddSample(fixture.Clock.UtcNow);
        Assert.Equal(1, fixture.Store.CountRawSamples());
    }

    [Fact]
    public void A_measurement_survives_the_round_trip_including_what_was_wrong_with_it()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        var at = fixture.Clock.UtcNow;

        fixture.Store.InsertRawSample(new RawSample
        {
            PowerSessionId = HistoryFixture.SessionId,
            SampledAtUtc = at,
            CpuPercent = 12.5,
            MemoryPercent = null,
            DiskReadBytesPerSecond = 2048,
            DiskWriteBytesPerSecond = 1024,
            NetworkRxBytesPerSecond = 512,
            NetworkTxBytesPerSecond = 256,
            State = ProtectionState.Unprotected,
            SshSessionCount = 2,
            QualityFlags = SampleQuality.MemoryUnavailable | SampleQuality.Estimated
        });

        var loaded = Assert.Single(fixture.Store.ReadRawSamples(at, at.AddSeconds(1)));
        Assert.Equal(12.5, loaded.CpuPercent);
        Assert.Null(loaded.MemoryPercent);
        Assert.Equal(2048, loaded.DiskReadBytesPerSecond);
        Assert.Equal(ProtectionState.Unprotected, loaded.State);
        Assert.Equal(2, loaded.SshSessionCount);
        Assert.Equal(SampleQuality.MemoryUnavailable | SampleQuality.Estimated, loaded.QualityFlags);
    }

    [Fact]
    public void A_batch_of_measurements_is_written_in_one_transaction()
    {
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        var at = fixture.Clock.UtcNow;

        fixture.Store.InsertRawSamples(
        [
            Sample(at, 10),
            Sample(at.AddSeconds(10), 20),
            Sample(at.AddSeconds(20), 30)
        ]);

        var loaded = fixture.Store.ReadRawSamples(at, at.AddMinutes(1));
        Assert.Equal([10.0, 20.0, 30.0], loaded.Select(sample => sample.CpuPercent));
    }

    [Fact]
    public void A_batch_containing_a_bad_measurement_writes_none_of_it()
    {
        // The batch is one transaction, so a half-written run of measurements cannot be aggregated as if
        // it were complete.
        using var fixture = new HistoryFixture();
        fixture.StartSession();
        var at = fixture.Clock.UtcNow;

        Assert.Throws<SqliteException>(() => fixture.Store.InsertRawSamples(
        [
            Sample(at, 10),
            Sample(at.AddSeconds(10), 20) with { PowerSessionId = "no-such-session" }
        ]));

        Assert.Equal(0, fixture.Store.CountRawSamples());
    }

    [Fact]
    public void An_inhibit_change_records_why_the_machine_was_held()
    {
        using var fixture = new HistoryFixture();

        fixture.Store.RecordInhibitEvent(new InhibitEvent
        {
            OccurredAtUtc = fixture.Clock.UtcNow,
            Change = InhibitChange.Established,
            ProtectionState = ProtectionState.Protected,
            InhibitorKinds = [InhibitorKind.SshSession, InhibitorKind.ProtectedProcess],
            Reason = "SSH session and a running build",
            Revision = 7
        });

        Assert.Equal(1, fixture.Store.CountInhibitEvents());
    }

    [Fact]
    public void Nothing_is_written_when_a_transaction_is_not_committed()
    {
        using var fixture = new HistoryFixture();

        using (var transaction = fixture.Store.BeginTransaction())
        {
            transaction.UpsertLease(fixture.Lease());
        }

        Assert.Empty(fixture.Store.LoadLeases());
    }

    [Fact]
    public void A_log_position_and_the_lease_that_log_entry_called_for_land_together()
    {
        // The pairing that must be atomic. Advancing the position and then crashing would drop the
        // login permanently: the log is never re-read, no lease is created, and the machine sleeps
        // while the user is connected.
        using var fixture = new HistoryFixture();

        using (var abandoned = fixture.Store.BeginTransaction())
        {
            abandoned.TryMarkProcessed("openssh", "4242", fixture.Clock.UtcNow);
            abandoned.SetBookmark("openssh", "offset-100", fixture.Clock.UtcNow);
            abandoned.UpsertLease(fixture.Lease());
        }

        Assert.False(fixture.Store.IsProcessed("openssh", "4242"));
        Assert.Null(fixture.Store.GetBookmark("openssh"));
        Assert.Empty(fixture.Store.LoadLeases());

        using (var committed = fixture.Store.BeginTransaction())
        {
            committed.TryMarkProcessed("openssh", "4242", fixture.Clock.UtcNow);
            committed.SetBookmark("openssh", "offset-100", fixture.Clock.UtcNow);
            committed.UpsertLease(fixture.Lease());
            committed.Commit();
        }

        Assert.True(fixture.Store.IsProcessed("openssh", "4242"));
        Assert.Equal("offset-100", fixture.Store.GetBookmark("openssh"));
        Assert.Single(fixture.Store.LoadLeases());
    }

    [Fact]
    public void A_log_record_already_acted_on_is_not_acted_on_again()
    {
        using var fixture = new HistoryFixture();

        using (var first = fixture.Store.BeginTransaction())
        {
            Assert.True(first.TryMarkProcessed("openssh", "4242", fixture.Clock.UtcNow));
            first.Commit();
        }

        using var second = fixture.Store.BeginTransaction();
        Assert.False(second.TryMarkProcessed("openssh", "4242", fixture.Clock.UtcNow));
    }

    [Fact]
    public void A_retried_command_returns_the_first_answer_instead_of_acting_again()
    {
        // A client whose reply was lost retries with the same identifier. Acting again would give it a
        // second lease.
        using var fixture = new HistoryFixture();

        using (var first = fixture.Store.BeginTransaction())
        {
            var outcome = first.RecordCommand("S-1-5-21-1", "req-1", "hash-a", """{"leaseId":"lease-1"}""", fixture.Clock.UtcNow);
            Assert.Equal(CommandRecordOutcome.Recorded, outcome.Outcome);
            first.Commit();
        }

        using var retry = fixture.Store.BeginTransaction();
        var repeated = retry.RecordCommand("S-1-5-21-1", "req-1", "hash-a", null, fixture.Clock.UtcNow);

        Assert.Equal(CommandRecordOutcome.AlreadyCompleted, repeated.Outcome);
        Assert.Equal("""{"leaseId":"lease-1"}""", repeated.ExistingResultJson);
    }

    [Fact]
    public void The_same_request_identifier_carrying_different_content_is_refused()
    {
        // Answering from the earlier result would tell the caller a request succeeded that was never
        // looked at.
        using var fixture = new HistoryFixture();

        using (var first = fixture.Store.BeginTransaction())
        {
            first.RecordCommand("S-1-5-21-1", "req-1", "hash-a", "{}", fixture.Clock.UtcNow);
            first.Commit();
        }

        using var conflicting = fixture.Store.BeginTransaction();
        var outcome = conflicting.RecordCommand("S-1-5-21-1", "req-1", "hash-b", null, fixture.Clock.UtcNow);

        Assert.Equal(CommandRecordOutcome.PayloadConflict, outcome.Outcome);
        Assert.Null(outcome.ExistingResultJson);
    }

    [Fact]
    public void Two_callers_may_use_the_same_request_identifier()
    {
        using var fixture = new HistoryFixture();

        using var transaction = fixture.Store.BeginTransaction();
        Assert.Equal(
            CommandRecordOutcome.Recorded,
            transaction.RecordCommand("S-1-5-21-1", "req-1", "hash-a", null, fixture.Clock.UtcNow).Outcome);
        Assert.Equal(
            CommandRecordOutcome.Recorded,
            transaction.RecordCommand("S-1-5-21-2", "req-1", "hash-a", null, fixture.Clock.UtcNow).Outcome);
    }

    [Fact]
    public void A_run_of_the_machine_is_recorded_and_can_be_closed_off()
    {
        using var fixture = new HistoryFixture();
        var startedAt = fixture.Clock.UtcNow;
        fixture.StartSession();

        var open = fixture.Store.LoadPowerSession(HistoryFixture.SessionId);
        Assert.NotNull(open);
        Assert.Equal("boot-1", open.BootId);
        Assert.Equal(startedAt, open.StartedAtUtc);
        Assert.Equal("Boot", open.StartReason);
        Assert.Null(open.EndedAtUtc);

        fixture.Store.EndPowerSession(HistoryFixture.SessionId, startedAt.AddHours(2), "Shutdown");

        var closed = fixture.Store.LoadPowerSession(HistoryFixture.SessionId);
        Assert.NotNull(closed);
        Assert.Equal(startedAt.AddHours(2), closed.EndedAtUtc);
        Assert.Equal("Shutdown", closed.EndReason);

        // Closing it must not disturb when it started.
        Assert.Equal(startedAt, closed.StartedAtUtc);
    }

    [Fact]
    public void An_unknown_run_of_the_machine_reads_back_as_absent()
    {
        using var fixture = new HistoryFixture();

        Assert.Null(fixture.Store.LoadPowerSession("never-existed"));
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        Assert.Throws<ArgumentNullException>(() => new SqliteHistoryStore(null!));

        using var fixture = new HistoryFixture();
        Assert.Throws<ArgumentNullException>(() => fixture.Store.UpsertLease(null!));
        Assert.Throws<ArgumentNullException>(() => fixture.Store.InsertRawSamples(null!));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Store.GetWatermark(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Store.GetBookmark(string.Empty));
    }
}

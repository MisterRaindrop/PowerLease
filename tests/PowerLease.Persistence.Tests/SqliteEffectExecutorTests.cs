using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Persistence.History;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class SqliteEffectExecutorTests
{
    private static readonly CallerSnapshot Liu = new()
    {
        Sid = "S-1-5-21-1",
        AccountName = "liu"
    };

    [Fact]
    public async Task A_created_lease_is_readable_after_the_effect_completes()
    {
        using var fixture = new HistoryFixture();
        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);
        var execution = executor.ExecuteAsync(LeaseEffect(fixture.Lease()), TestContext.Current.CancellationToken);

        Assert.True(execution.IsCompletedSuccessfully);
        var completion = await execution;
        var loaded = Assert.Single(fixture.Store.LoadLeases().Leases);

        Assert.Equal(EffectOutcome.Succeeded, completion.Outcome);
        Assert.Equal("lease-1", loaded.Id);
        Assert.Equal("""{"leaseId":"lease-1"}""", completion.ResultJson);
    }

    [Fact]
    public async Task A_retried_request_returns_the_first_result_without_writing_a_second_lease()
    {
        // The database result is authoritative after a reply is lost. The retry's provisional lease
        // must not replace or sit beside the one created by the first attempt.
        using var fixture = new HistoryFixture();
        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);
        var first = await executor.ExecuteAsync(
            LeaseEffect(fixture.Lease("lease-1")),
            TestContext.Current.CancellationToken);

        var retry = await executor.ExecuteAsync(
            LeaseEffect(fixture.Lease("lease-2")) with { EffectId = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(EffectOutcome.Succeeded, first.Outcome);
        Assert.Equal(EffectOutcome.AlreadyDone, retry.Outcome);
        Assert.Equal("""{"leaseId":"lease-1"}""", retry.ResultJson);
        Assert.Equal("lease-1", Assert.Single(fixture.Store.LoadLeases().Leases).Id);
    }

    [Fact]
    public async Task A_request_identifier_with_different_content_is_refused_without_writing_a_lease()
    {
        // Seed only the completed command so this test can prove the conflicting effect does not apply
        // its lease write.
        using var fixture = new HistoryFixture();
        using (var seed = fixture.Store.BeginTransaction())
        {
            seed.RecordCommand(
                Liu.Sid,
                "request-1",
                "hash-a",
                """{"leaseId":"earlier-lease"}""",
                fixture.Clock.UtcNow);
            seed.Commit();
        }

        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);
        var completion = await executor.ExecuteAsync(
            LeaseEffect(fixture.Lease()) with { PayloadHash = "hash-b" },
            TestContext.Current.CancellationToken);

        Assert.Equal(EffectOutcome.Conflict, completion.Outcome);
        Assert.Empty(fixture.Store.LoadLeases().Leases);
        Assert.Equal(1, SqliteTestHelpers.CountIn(fixture.DatabasePath, "idempotent_commands"));
    }

    [Fact]
    public async Task A_release_makes_the_lease_ending_durable()
    {
        using var fixture = new HistoryFixture();
        fixture.Store.UpsertLease(fixture.Lease());
        var endedAt = fixture.Clock.UtcNow.AddHours(1);
        var released = fixture.Lease(status: LeaseStatus.Released) with
        {
            EndedAtUtc = endedAt,
            EndReason = "released by liu"
        };
        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);

        var completion = await executor.ExecuteAsync(
            LeaseEffect(released, EffectKind.PersistLeaseRelease),
            TestContext.Current.CancellationToken);
        var loaded = Assert.Single(fixture.Store.LoadLeases().Leases);

        Assert.Equal(EffectOutcome.Succeeded, completion.Outcome);
        Assert.Equal(LeaseStatus.Released, loaded.Status);
        Assert.Equal(endedAt, loaded.EndedAtUtc);
        Assert.Equal("released by liu", loaded.EndReason);
    }

    [Fact]
    public async Task An_expired_lease_is_written_without_inventing_a_command()
    {
        // Natural expiry has no caller request to deduplicate, so manufacturing an identifier would
        // turn an internal lifecycle write into a client command that never existed.
        using var fixture = new HistoryFixture();
        var expired = fixture.Lease(status: LeaseStatus.Expired) with
        {
            EndedAtUtc = fixture.Clock.UtcNow,
            EndReason = "the lease ran out"
        };
        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);

        var completion = await executor.ExecuteAsync(
            LeaseEffect(expired) with { RequestId = null, Caller = null, PayloadHash = null },
            TestContext.Current.CancellationToken);

        Assert.Equal(EffectOutcome.Succeeded, completion.Outcome);
        Assert.Equal(LeaseStatus.Expired, Assert.Single(fixture.Store.LoadLeases().Leases).Status);
        Assert.Equal(0, SqliteTestHelpers.CountIn(fixture.DatabasePath, "idempotent_commands"));
    }

    [Fact]
    public async Task An_unprotected_change_is_recorded_as_rejected()
    {
        using var fixture = new HistoryFixture();
        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);
        var effect = new KernelEffect
        {
            EffectId = 7,
            Kind = EffectKind.RecordInhibitChange,
            ProtectionState = ProtectionState.Unprotected,
            InhibitorKinds = [InhibitorKind.SshSession, InhibitorKind.ProtectedProcess],
            Reason = "Windows refused the power request",
            Revision = 42
        };

        var completion = await executor.ExecuteAsync(effect, TestContext.Current.CancellationToken);

        Assert.Equal(EffectOutcome.Succeeded, completion.Outcome);
        Assert.Equal(1, fixture.Store.CountInhibitEvents());
        using var connection = SqliteTestHelpers.OpenRaw(fixture.DatabasePath);
        Assert.Equal("Rejected", SqliteTestHelpers.Text(connection, "SELECT change FROM inhibit_events;"));
        Assert.Equal(
            "Unprotected",
            SqliteTestHelpers.Text(connection, "SELECT protection_state FROM inhibit_events;"));
        Assert.Equal(
            "SshSession,ProtectedProcess",
            SqliteTestHelpers.Text(connection, "SELECT inhibitor_kinds FROM inhibit_events;"));
        Assert.Equal(
            "Windows refused the power request",
            SqliteTestHelpers.Text(connection, "SELECT reason FROM inhibit_events;"));
        Assert.Equal(
            "2026-07-31T12:00:00.0000000Z",
            SqliteTestHelpers.Text(connection, "SELECT occurred_at_utc FROM inhibit_events;"));
        Assert.Equal(42, SqliteTestHelpers.Scalar(connection, "SELECT revision FROM inhibit_events;"));
    }

    [Fact]
    public async Task A_failed_lease_write_rolls_back_its_command_record()
    {
        // RecordCommand runs first. If the later lease write fails, atomicity requires the command row
        // to disappear too, or a retry would be answered with a success that never happened.
        using var fixture = new HistoryFixture();
        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);
        var invalidLease = fixture.Lease() with { Id = null! };

        var completion = await executor.ExecuteAsync(
            LeaseEffect(invalidLease),
            TestContext.Current.CancellationToken);

        Assert.Equal(EffectOutcome.Failed, completion.Outcome);
        Assert.NotNull(completion.Error);
        Assert.Empty(fixture.Store.LoadLeases().Leases);
        Assert.Equal(0, SqliteTestHelpers.CountIn(fixture.DatabasePath, "idempotent_commands"));
    }

    [Fact]
    public async Task Missing_effect_requirements_are_reported_without_writing_anything()
    {
        using var fixture = new HistoryFixture();
        var executor = new SqliteEffectExecutor(fixture.Store, fixture.Clock);

        var missingLease = await executor.ExecuteAsync(
            LeaseEffect(fixture.Lease()) with { Lease = null },
            TestContext.Current.CancellationToken);
        var missingCaller = await executor.ExecuteAsync(
            LeaseEffect(fixture.Lease()) with { EffectId = 2, Caller = null },
            TestContext.Current.CancellationToken);
        var missingHash = await executor.ExecuteAsync(
            LeaseEffect(fixture.Lease()) with { EffectId = 3, PayloadHash = null },
            TestContext.Current.CancellationToken);

        Assert.Equal(EffectOutcome.Failed, missingLease.Outcome);
        Assert.Contains("Lease", Assert.IsType<string>(missingLease.Error));
        Assert.Equal(EffectOutcome.Failed, missingCaller.Outcome);
        Assert.Contains("Caller", Assert.IsType<string>(missingCaller.Error));
        Assert.Equal(EffectOutcome.Failed, missingHash.Outcome);
        Assert.Contains("PayloadHash", Assert.IsType<string>(missingHash.Error));
        Assert.Empty(fixture.Store.LoadLeases().Leases);
        Assert.Equal(0, SqliteTestHelpers.CountIn(fixture.DatabasePath, "idempotent_commands"));
    }

    private static KernelEffect LeaseEffect(KeepAwakeLease lease, EffectKind kind = EffectKind.PersistLease) => new()
    {
        EffectId = 1,
        Kind = kind,
        RequestId = "request-1",
        Caller = Liu,
        PayloadHash = "hash-a",
        Lease = lease
    };
}

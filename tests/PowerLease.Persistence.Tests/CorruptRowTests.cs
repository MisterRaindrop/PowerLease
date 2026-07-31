using PowerLease.Persistence.History;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class CorruptRowTests
{
    [Fact]
    public void One_unreadable_lease_row_does_not_stop_the_others_from_loading()
    {
        // What a downgrade looks like after a future version adds a lease source: a row this build cannot map.
        // If mapping it throws, nothing can load any lease, the service fails to start, and the machine is
        // unprotected for as long as it keeps restarting.
        using var fixture = new HistoryFixture();
        fixture.Store.UpsertLease(fixture.Lease("good-1"));

        using (var raw = SqliteTestHelpers.OpenRaw(fixture.DatabasePath))
        {
            SqliteTestHelpers.Execute(raw, """
                INSERT INTO keep_awake_leases (id, source, started_at_utc, auto_renew, status, epoch_id,
                    original_duration_seconds)
                VALUES ('bad-1', 'SomethingFromTheFuture', '2026-07-31T12:00:00.0000000Z', 0, 'Active',
                    '11111111-1111-1111-1111-111111111111', 3600);
                """);
        }

        var result = fixture.Store.LoadLeases();

        // The readable lease still loads, so one bad record cannot stop the service starting.
        Assert.Equal("good-1", Assert.Single(result.Leases).Id);

        // And the skip is reported, because a lease quietly dropped is protection quietly lost. The caller
        // latches a fault for this, which is itself a reason to keep the machine awake.
        Assert.True(result.HasUnreadableRows);
        Assert.Contains("bad-1", Assert.Single(result.UnreadableRows), StringComparison.Ordinal);
    }
}

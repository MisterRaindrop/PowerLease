using Microsoft.Data.Sqlite;
using PowerLease.Domain;
using PowerLease.Persistence.History;
using PowerLease.Persistence.Sqlite;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class SqliteReliabilityTests
{
    [Fact]
    public void A_reader_never_observes_half_of_the_single_writers_transaction()
    {
        // The service has one writer connection. A second connection represents a CLI/history reader that
        // began a snapshot while that writer was changing the state. WAL must let the writer commit without
        // disturbing the reader's old snapshot, and the next snapshot must contain the whole transaction.
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        var factory = new SqliteConnectionFactory(root.Paths.DatabasePath);
        using var writer = new SqliteHistoryStore(factory);
        using var reader = factory.Open();
        using (var readerTransaction = reader.BeginTransaction(deferred: true))
        {
            Assert.Equal(0, CountLeases(reader, readerTransaction));

            using (var write = writer.BeginTransaction())
            {
                write.UpsertLease(Lease("first"));
                Assert.Equal(0, CountLeases(reader, readerTransaction));

                write.UpsertLease(Lease("second"));
                Assert.Equal(0, CountLeases(reader, readerTransaction));
                write.Commit();
            }

            Assert.Equal(0, CountLeases(reader, readerTransaction));
        }

        Assert.Equal(2, CountLeases(reader, transaction: null));
    }

    [Fact]
    public void A_crash_image_recovers_committed_wal_data_and_an_intact_schema()
    {
        // Copy the database and WAL while the owning connection is still alive. That is the on-disk image an
        // abrupt process death leaves behind; a clean close would checkpoint it and stop exercising recovery.
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        var factory = new SqliteConnectionFactory(root.Paths.DatabasePath);
        var crashDirectory = Path.Combine(root.Path, "crash-image");
        Directory.CreateDirectory(crashDirectory);
        var crashDatabase = Path.Combine(crashDirectory, "history.db");

        using (var live = factory.Open())
        {
            SqliteTestHelpers.Execute(live, """
                INSERT INTO alerts (raised_at_utc, category, severity, message)
                VALUES ('2026-08-03T00:00:00.0000000Z', 'Recovery', 'Warning', 'committed before crash');
                """);

            var mainFileOnly = Path.Combine(root.Path, "main-file-only.db");
            File.Copy(root.Paths.DatabasePath, mainFileOnly);
            Assert.Equal(0, SqliteTestHelpers.CountIn(mainFileOnly, "alerts"));

            var sourceWal = root.Paths.DatabasePath + "-wal";
            Assert.True(File.Exists(sourceWal));
            File.Copy(root.Paths.DatabasePath, crashDatabase);
            File.Copy(sourceWal, crashDatabase + "-wal");
        }

        using var recovered = new SqliteConnectionFactory(crashDatabase).Open();
        Assert.Equal("ok", SqliteTestHelpers.Text(recovered, "PRAGMA integrity_check;"));
        Assert.Equal(2, SqliteTestHelpers.Scalar(recovered, "SELECT MAX(version) FROM schema_versions;"));
        Assert.Equal(
            "committed before crash",
            SqliteTestHelpers.Text(recovered, "SELECT message FROM alerts;"));
        Assert.Contains("keep_awake_leases", SqliteTestHelpers.TableNames(recovered));
    }

    private static long CountLeases(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM keep_awake_leases;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static KeepAwakeLease Lease(string id) => new()
    {
        Id = id,
        Source = LeaseSource.Cli,
        OwnerSid = "S-1-5-21-1000",
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        ExpiresAtUtc = DateTimeOffset.UnixEpoch.AddHours(1),
        Status = LeaseStatus.Active,
        EpochId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
        OriginalDuration = TimeSpan.FromHours(1)
    };

    private static FakeClock Clock() => new(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));
}

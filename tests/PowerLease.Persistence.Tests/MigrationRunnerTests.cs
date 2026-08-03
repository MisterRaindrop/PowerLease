using Microsoft.Data.Sqlite;
using PowerLease.Persistence.Sqlite;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class MigrationRunnerTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static FakeClock Clock() => new(Origin);

    [Fact]
    public void A_new_database_gets_the_whole_schema()
    {
        // The table list is asserted exactly. A table quietly disappearing from the migration would
        // otherwise only show up when something tried to write to it.
        using var root = new TempRoot();

        var result = new MigrationRunner(root.Paths, Clock()).Run();

        Assert.True(result.IsNewDatabase);
        Assert.Equal(0, result.FromVersion);
        Assert.Equal(2, result.ToVersion);
        Assert.Equal([1, 2], result.AppliedVersions);
        Assert.Null(result.BackupPath);

        using var connection = SqliteTestHelpers.OpenRaw(root.Paths.DatabasePath);
        Assert.Equal(
            [
                "aggregate_watermarks",
                "alerts",
                "audit_events",
                "event_bookmarks",
                "hour_aggregates",
                "idempotent_commands",
                "inhibit_events",
                "keep_awake_leases",
                "minute_aggregates",
                "power_events",
                "power_sessions",
                "processed_event",
                "raw_samples",
                "schema_versions",
                "ssh_sessions",
                "wake_events"
            ],
            SqliteTestHelpers.TableNames(connection));
    }

    [Fact]
    public void The_applied_version_is_recorded_with_the_time_it_was_applied()
    {
        using var root = new TempRoot();

        new MigrationRunner(root.Paths, Clock()).Run();

        using var connection = SqliteTestHelpers.OpenRaw(root.Paths.DatabasePath);
        Assert.Equal(2, SqliteTestHelpers.Scalar(connection, "SELECT MAX(version) FROM schema_versions;"));
        Assert.Equal(
            Timestamps.ToText(Origin),
            SqliteTestHelpers.Text(connection, "SELECT applied_at_utc FROM schema_versions;"));
    }

    [Fact]
    public void Running_again_does_nothing()
    {
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        var second = new MigrationRunner(root.Paths, Clock()).Run();

        Assert.False(second.SchemaChanged);
        Assert.Equal(2, second.FromVersion);
        Assert.Equal(2, second.ToVersion);
        Assert.Null(second.BackupPath);
        Assert.False(Directory.Exists(root.Paths.BackupsDirectory));
    }

    [Fact]
    public void Version_two_adds_owner_sid_to_a_real_version_one_database_without_losing_its_lease()
    {
        using var root = new TempRoot();
        var version1Only = new[] { new Migration(1, "Initial schema", SqliteSchema.Version1) };
        new MigrationRunner(root.Paths, Clock(), version1Only).Run();

        using (var version1 = SqliteTestHelpers.OpenRaw(root.Paths.DatabasePath))
        {
            Assert.Equal(
                0,
                SqliteTestHelpers.Scalar(
                    version1,
                    "SELECT COUNT(*) FROM pragma_table_info('keep_awake_leases') WHERE name = 'owner_sid';"));
            SqliteTestHelpers.Execute(version1, """
                INSERT INTO keep_awake_leases (
                    id, source, reason, owner_user, started_at_utc, auto_renew, status, epoch_id,
                    original_duration_seconds, remaining_at_checkpoint_seconds, checkpoint_utc)
                VALUES (
                    'lease-v1', 'Cli', 'survive migration', 'liu', '2026-07-31T12:00:00.0000000Z',
                    0, 'Active', '33333333-3333-3333-3333-333333333333', 10800, 7200,
                    '2026-07-31T12:00:00.0000000Z');
                """);
        }

        var result = new MigrationRunner(root.Paths, Clock()).Run();

        Assert.Equal(1, result.FromVersion);
        Assert.Equal(2, result.ToVersion);
        Assert.Equal([2], result.AppliedVersions);

        using var migrated = SqliteTestHelpers.OpenRaw(root.Paths.DatabasePath);
        Assert.Equal("lease-v1", SqliteTestHelpers.Text(migrated, "SELECT id FROM keep_awake_leases;"));
        Assert.Equal(
            1,
            SqliteTestHelpers.Scalar(
                migrated,
                "SELECT COUNT(*) FROM pragma_table_info('keep_awake_leases') WHERE name = 'owner_sid';"));

        using var owner = migrated.CreateCommand();
        owner.CommandText = "SELECT owner_sid FROM keep_awake_leases WHERE id = 'lease-v1';";
        Assert.Equal(DBNull.Value, owner.ExecuteScalar());
    }

    [Fact]
    public void An_existing_database_is_copied_before_it_is_upgraded()
    {
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        var result = new MigrationRunner(root.Paths, Clock(), WithThirdVersion()).Run();

        Assert.Equal(2, result.FromVersion);
        Assert.Equal(3, result.ToVersion);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Contains("powerlease-v2-", Path.GetFileName(result.BackupPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_backups_taken_in_the_same_second_do_not_overwrite_each_other()
    {
        // The copy exists so a failed upgrade can be undone; silently replacing the previous one would
        // defeat the point. Two attempts at the same upgrade on a stopped clock both copy version 2, so
        // both want the same file name -- which is the only way to reach the collision at all.
        using var root = new TempRoot();
        var clock = Clock();
        new MigrationRunner(root.Paths, clock).Run();

        var first = Assert.Throws<MigrationException>(
            () => new MigrationRunner(root.Paths, clock, WithBrokenThirdVersion()).Run());
        var second = Assert.Throws<MigrationException>(
            () => new MigrationRunner(root.Paths, clock, WithBrokenThirdVersion()).Run());

        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.True(File.Exists(first.BackupPath));
        Assert.True(File.Exists(second.BackupPath));
        Assert.EndsWith("-2.db", second.BackupPath!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multi_step_upgrade_applies_every_pending_version()
    {
        using var root = new TempRoot();
        var clock = Clock();
        new MigrationRunner(root.Paths, clock).Run();

        var result = new MigrationRunner(root.Paths, clock, WithFourthVersion()).Run();

        Assert.Equal([3, 4], result.AppliedVersions);
        Assert.Equal(4, result.ToVersion);

        using var connection = SqliteTestHelpers.OpenRaw(root.Paths.DatabasePath);
        Assert.Contains("third_version", SqliteTestHelpers.TableNames(connection));
        Assert.Contains("fourth_version", SqliteTestHelpers.TableNames(connection));
    }

    [Fact]
    public void The_backup_keeps_data_that_is_still_only_in_the_write_ahead_log()
    {
        // The reason the SQLite backup mechanism is used instead of copying the file. Under write-ahead
        // logging, recent commits live in a separate -wal file until a checkpoint moves them, so a file
        // copy produces a database missing the newest data. The negative control below is what makes
        // this test mean anything: it proves the row really is outside the main database file.
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        var factory = new SqliteConnectionFactory(root.Paths.DatabasePath);

        // Held open for the whole test: closing the last connection checkpoints the log, which would
        // move the row into the main file and quietly make the negative control pass for the wrong
        // reason.
        using var connection = factory.Open();
        SqliteTestHelpers.Execute(connection, """
            INSERT INTO alerts (raised_at_utc, category, severity, message)
            VALUES ('2026-07-31T12:00:00.0000000Z', 'Config', 'Warning', 'only in the write-ahead log');
            """);

        var fileCopy = Path.Combine(root.Path, "file-copy.db");
        File.Copy(root.Paths.DatabasePath, fileCopy);
        Assert.Equal(0, SqliteTestHelpers.CountIn(fileCopy, "alerts"));

        var backup = Path.Combine(root.Path, "backup.db");
        DatabaseBackup.CopyTo(connection, backup);

        var restoredPath = Path.Combine(root.Path, "restored.db");
        File.Copy(backup, restoredPath);
        using var restored = new SqliteConnectionFactory(restoredPath).Open();
        Assert.Equal(1, SqliteTestHelpers.Scalar(restored, "SELECT COUNT(*) FROM alerts;"));
        Assert.Equal(
            "only in the write-ahead log",
            SqliteTestHelpers.Text(restored, "SELECT message FROM alerts;"));
        Assert.Equal(2, SqliteTestHelpers.Scalar(restored, "SELECT MAX(version) FROM schema_versions;"));
    }

    [Fact]
    public void A_failing_migration_leaves_the_database_exactly_as_it_was()
    {
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        using (var seed = new SqliteConnectionFactory(root.Paths.DatabasePath).Open())
        {
            SqliteTestHelpers.Execute(seed, """
                INSERT INTO alerts (raised_at_utc, category, severity, message)
                VALUES ('2026-07-31T12:00:00.0000000Z', 'Config', 'Warning', 'written before the upgrade');
                """);
        }

        var error = Assert.Throws<MigrationException>(
            () => new MigrationRunner(root.Paths, Clock(), WithBrokenThirdVersion()).Run());

        Assert.Equal(3, error.Version);
        Assert.NotNull(error.BackupPath);
        Assert.True(File.Exists(error.BackupPath));

        using var connection = SqliteTestHelpers.OpenRaw(root.Paths.DatabasePath);

        // Still at version 2, and the statement that ran before the failing one was rolled back.
        Assert.Equal(2, SqliteTestHelpers.Scalar(connection, "SELECT MAX(version) FROM schema_versions;"));
        Assert.DoesNotContain("half_applied", SqliteTestHelpers.TableNames(connection));
        Assert.Equal(1, SqliteTestHelpers.Scalar(connection, "SELECT COUNT(*) FROM alerts;"));
    }

    [Fact]
    public void The_copy_taken_before_a_failed_migration_can_be_read_back()
    {
        // A backup nobody has ever opened is a guess, not a safety net.
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        using (var seed = new SqliteConnectionFactory(root.Paths.DatabasePath).Open())
        {
            SqliteTestHelpers.Execute(seed, """
                INSERT INTO alerts (raised_at_utc, category, severity, message)
                VALUES ('2026-07-31T12:00:00.0000000Z', 'Config', 'Warning', 'written before the upgrade');
                """);
        }

        var error = Assert.Throws<MigrationException>(
            () => new MigrationRunner(root.Paths, Clock(), WithBrokenThirdVersion()).Run());

        using var backup = SqliteTestHelpers.OpenRaw(error.BackupPath!);
        Assert.Equal(2, SqliteTestHelpers.Scalar(backup, "SELECT MAX(version) FROM schema_versions;"));
        Assert.Equal(
            "written before the upgrade",
            SqliteTestHelpers.Text(backup, "SELECT message FROM alerts;"));
    }

    [Fact]
    public void A_database_written_by_a_newer_build_is_refused()
    {
        // Reading it with the older schema in mind would misinterpret columns rather than fail.
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock(), WithThirdVersion()).Run();

        var error = Assert.Throws<MigrationException>(() => new MigrationRunner(root.Paths, Clock()).Run());

        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_database_uses_write_ahead_logging()
    {
        using var root = new TempRoot();
        new MigrationRunner(root.Paths, Clock()).Run();

        using var connection = new SqliteConnectionFactory(root.Paths.DatabasePath).Open();
        Assert.Equal("wal", SqliteTestHelpers.Text(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void The_data_directory_is_created()
    {
        using var root = new TempRoot();
        Assert.False(Directory.Exists(root.Path));

        new MigrationRunner(root.Paths, Clock()).Run();

        Assert.True(File.Exists(root.Paths.DatabasePath));
    }

    [Fact]
    public void The_shipped_migrations_are_ordered_and_start_at_one()
    {
        var versions = MigrationRunner.DefaultMigrations.Select(migration => migration.Version).ToArray();

        Assert.Equal(versions.OrderBy(version => version), versions);
        Assert.Equal(1, versions[0]);
        Assert.Equal(versions.Distinct().Count(), versions.Length);
    }

    [Fact]
    public void A_migration_failure_says_what_failed_and_where_the_copy_is()
    {
        var inner = new InvalidOperationException("the statement was rejected");

        var withBackup = new MigrationException(3, "/tmp/backup.db", inner);
        Assert.Equal(3, withBackup.Version);
        Assert.Contains("/tmp/backup.db", withBackup.Message, StringComparison.Ordinal);
        Assert.Contains("left unchanged", withBackup.Message, StringComparison.Ordinal);
        Assert.Same(inner, withBackup.InnerException);

        var withoutBackup = new MigrationException(1, null, inner);
        Assert.DoesNotContain("copy", withoutBackup.Message, StringComparison.Ordinal);
        Assert.Null(withoutBackup.BackupPath);

        Assert.Equal("plain", new MigrationException("plain").Message);
        Assert.Same(inner, new MigrationException("wrapped", inner).InnerException);
        Assert.NotEmpty(new MigrationException().Message);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        using var root = new TempRoot();

        Assert.Throws<ArgumentNullException>(() => new MigrationRunner(null!, Clock()));
        Assert.Throws<ArgumentNullException>(() => new MigrationRunner(root.Paths, null!));
        Assert.Throws<ArgumentNullException>(() => new MigrationRunner(root.Paths, Clock(), null!));
        Assert.Throws<ArgumentException>(() => new MigrationRunner(root.Paths, Clock(), []));
    }

    private static IReadOnlyList<Migration> WithThirdVersion() =>
    [
        .. MigrationRunner.DefaultMigrations,
        new Migration(3, "Test upgrade", "CREATE TABLE third_version (x INTEGER);")
    ];

    private static IReadOnlyList<Migration> WithFourthVersion() =>
    [
        .. WithThirdVersion(),
        new Migration(4, "Test upgrade", "CREATE TABLE fourth_version (x INTEGER);")
    ];

    /// <summary>
    /// A migration whose first statement succeeds and whose second does not, so a rollback has
    /// something visible to undo.
    /// </summary>
    private static IReadOnlyList<Migration> WithBrokenThirdVersion() =>
    [
        .. MigrationRunner.DefaultMigrations,
        new Migration(
            3,
            "Test failure",
            """
            CREATE TABLE half_applied (x INTEGER);
            CREATE TABLE this_will_not_parse (;
            """)
    ];
}

public sealed class SchemaConstraintTests
{
    private static SqliteConnection Migrated(TempRoot root)
    {
        new MigrationRunner(root.Paths, new FakeClock(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero))).Run();
        return new SqliteConnectionFactory(root.Paths.DatabasePath).Open();
    }

    [Theory]
    [InlineData("Sleeping")]
    [InlineData("Hibernating")]
    public void A_sleep_transition_cannot_be_recorded(string state)
    {
        // This version holds the machine awake and never puts it to sleep, so it can never be the cause
        // of one of these transitions. Enforcing it in the schema means the claim cannot quietly stop
        // being true: adding such a row requires a migration that says so out loud.
        using var root = new TempRoot();
        using var connection = Migrated(root);

        Assert.Throws<SqliteException>(() => SqliteTestHelpers.Execute(connection, $"""
            INSERT INTO power_events (occurred_at_utc, event_type, to_state, automatic)
            VALUES ('2026-07-31T12:00:00.0000000Z', 'Transition', '{state}', 0);
            """));
    }

    [Fact]
    public void An_automatic_power_action_cannot_be_recorded()
    {
        using var root = new TempRoot();
        using var connection = Migrated(root);

        Assert.Throws<SqliteException>(() => SqliteTestHelpers.Execute(connection, """
            INSERT INTO power_events (occurred_at_utc, event_type, automatic)
            VALUES ('2026-07-31T12:00:00.0000000Z', 'Transition', 1);
            """));
    }

    [Fact]
    public void An_ordinary_power_event_is_still_accepted()
    {
        // The control for the two tests above: the constraints reject what they are meant to reject and
        // nothing else.
        using var root = new TempRoot();
        using var connection = Migrated(root);

        SqliteTestHelpers.Execute(connection, """
            INSERT INTO power_events (occurred_at_utc, event_type, from_state, to_state, automatic, requested_by)
            VALUES ('2026-07-31T12:00:00.0000000Z', 'Resumed', 'Released', 'Protected', 0, 'service');
            """);

        Assert.Equal(1, SqliteTestHelpers.Scalar(connection, "SELECT COUNT(*) FROM power_events;"));
    }

    [Fact]
    public void An_inhibit_event_must_name_a_protection_state_the_product_has()
    {
        using var root = new TempRoot();
        using var connection = Migrated(root);

        Assert.Throws<SqliteException>(() => SqliteTestHelpers.Execute(connection, """
            INSERT INTO inhibit_events (occurred_at_utc, change, protection_state, revision)
            VALUES ('2026-07-31T12:00:00.0000000Z', 'Established', 'Sleeping', 1);
            """));

        SqliteTestHelpers.Execute(connection, """
            INSERT INTO inhibit_events (occurred_at_utc, change, protection_state, inhibitor_kinds, revision)
            VALUES ('2026-07-31T12:00:00.0000000Z', 'Established', 'Protected', 'SshSession', 1);
            """);

        Assert.Equal(1, SqliteTestHelpers.Scalar(connection, "SELECT COUNT(*) FROM inhibit_events;"));
    }

    [Fact]
    public void One_external_record_can_only_be_processed_once()
    {
        // What stops a replayed log from creating a second lease for one login.
        using var root = new TempRoot();
        using var connection = Migrated(root);

        SqliteTestHelpers.Execute(connection, """
            INSERT INTO processed_event (channel, record_id, processed_at_utc)
            VALUES ('openssh', '4242', '2026-07-31T12:00:00.0000000Z');
            """);

        Assert.Throws<SqliteException>(() => SqliteTestHelpers.Execute(connection, """
            INSERT INTO processed_event (channel, record_id, processed_at_utc)
            VALUES ('openssh', '4242', '2026-07-31T12:00:05.0000000Z');
            """));
    }

    [Fact]
    public void One_request_identifier_can_only_be_carried_out_once_per_caller()
    {
        using var root = new TempRoot();
        using var connection = Migrated(root);

        SqliteTestHelpers.Execute(connection, """
            INSERT INTO idempotent_commands (caller_sid, request_id, payload_hash, completed_at_utc)
            VALUES ('S-1-5-21-1', 'req-1', 'abc', '2026-07-31T12:00:00.0000000Z');
            """);

        Assert.Throws<SqliteException>(() => SqliteTestHelpers.Execute(connection, """
            INSERT INTO idempotent_commands (caller_sid, request_id, payload_hash, completed_at_utc)
            VALUES ('S-1-5-21-1', 'req-1', 'def', '2026-07-31T12:00:01.0000000Z');
            """));

        // A different caller reusing the identifier is a different command.
        SqliteTestHelpers.Execute(connection, """
            INSERT INTO idempotent_commands (caller_sid, request_id, payload_hash, completed_at_utc)
            VALUES ('S-1-5-21-2', 'req-1', 'abc', '2026-07-31T12:00:02.0000000Z');
            """);

        Assert.Equal(2, SqliteTestHelpers.Scalar(connection, "SELECT COUNT(*) FROM idempotent_commands;"));
    }

    [Fact]
    public void A_time_bucket_can_only_exist_once()
    {
        // The unique bucket key aggregation relies on to be safely repeatable.
        using var root = new TempRoot();
        using var connection = Migrated(root);

        SqliteTestHelpers.Execute(connection, """
            INSERT INTO minute_aggregates (bucket_start_utc, sample_count, expected_sample_count, completeness)
            VALUES ('2026-07-31T12:00:00.0000000Z', 6, 6, 1.0);
            """);

        Assert.Throws<SqliteException>(() => SqliteTestHelpers.Execute(connection, """
            INSERT INTO minute_aggregates (bucket_start_utc, sample_count, expected_sample_count, completeness)
            VALUES ('2026-07-31T12:00:00.0000000Z', 6, 6, 1.0);
            """));
    }
}

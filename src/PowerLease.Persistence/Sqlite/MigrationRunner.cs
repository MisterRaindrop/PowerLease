using System.Globalization;
using Microsoft.Data.Sqlite;
using PowerLease.Domain;

namespace PowerLease.Persistence.Sqlite;

/// <summary>
/// Brings the database up to the schema this build expects.
/// <para>
/// Each migration runs inside its own transaction, so a failure leaves the database at the last
/// version that applied completely and <c>schema_versions</c> says accurately which one that is. A
/// database that already had a schema is copied first, using SQLite's backup mechanism so the copy
/// includes commits still sitting in the write-ahead log.
/// </para>
/// </summary>
public sealed class MigrationRunner
{
    public MigrationRunner(PowerLeasePaths paths, IClock clock)
        : this(paths, clock, DefaultMigrations)
    {
    }

    /// <param name="migrations">
    /// The migrations to apply. Overridable so that upgrade and failure paths can be exercised
    /// without waiting for the product to have a second schema version.
    /// </param>
    public MigrationRunner(PowerLeasePaths paths, IClock clock, IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(migrations);

        if (migrations.Count == 0)
        {
            throw new ArgumentException("At least one migration is required.", nameof(migrations));
        }

        Paths = paths;
        Clock = clock;
        Migrations = [.. migrations.OrderBy(migration => migration.Version)];
        Connections = new SqliteConnectionFactory(paths.DatabasePath);
    }

    public static IReadOnlyList<Migration> DefaultMigrations { get; } =
    [
        new Migration(1, "Initial schema", SqliteSchema.Version1),
        new Migration(2, "Persist lease owner security identifiers", SqliteSchema.Version2)
    ];

    public int TargetVersion => Migrations[^1].Version;

    private PowerLeasePaths Paths { get; }

    private IClock Clock { get; }

    private IReadOnlyList<Migration> Migrations { get; }

    private SqliteConnectionFactory Connections { get; }

    /// <exception cref="MigrationException">
    /// Thrown when a migration fails, or when the database was written by a newer build. In both cases
    /// the caller must latch a persistent fault: the schema is not what the code expects, so nothing
    /// it reads can be trusted, and a latched fault is itself a reason to keep the machine awake.
    /// </exception>
    public MigrationResult Run()
    {
        var directory = Path.GetDirectoryName(Paths.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Connections.Open();

        var current = ReadVersion(connection);
        if (current > TargetVersion)
        {
            throw new MigrationException(
                $"The database is at schema version {current} but this build understands only " +
                $"{TargetVersion}. It was written by a newer version of PowerLease.");
        }

        var pending = Migrations.Where(migration => migration.Version > current).ToArray();
        if (pending.Length == 0)
        {
            return new MigrationResult(current, current, backupPath: null, []);
        }

        // Nothing to protect in a database that has no schema yet.
        var backupPath = current > 0 ? CreateBackup(connection, current) : null;

        var applied = new List<int>();
        foreach (var migration in pending)
        {
            Apply(connection, migration, backupPath);
            applied.Add(migration.Version);
        }

        return new MigrationResult(current, applied[^1], backupPath, applied);
    }

    private void Apply(SqliteConnection connection, Migration migration, string? backupPath)
    {
        using var transaction = connection.BeginTransaction();

        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO schema_versions (version, applied_at_utc, description)
                    VALUES ($version, $appliedAt, $description);
                    """;
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$appliedAt", Timestamps.ToText(Clock.UtcNow));
                record.Parameters.AddWithValue("$description", migration.Description);
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (SqliteException error)
        {
            transaction.Rollback();
            throw new MigrationException(migration.Version, backupPath, error);
        }
    }

    private string CreateBackup(SqliteConnection connection, int fromVersion)
    {
        Directory.CreateDirectory(Paths.BackupsDirectory);

        var stamp = Clock.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(Paths.BackupsDirectory, $"powerlease-v{fromVersion}-{stamp}.db");

        // Two runs within the same second must not silently overwrite each other's copy: the backup
        // exists precisely so a failed upgrade can be undone.
        var attempt = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(Paths.BackupsDirectory, $"powerlease-v{fromVersion}-{stamp}-{attempt}.db");
            attempt++;
        }

        DatabaseBackup.CopyTo(connection, path);
        return path;
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_versions';";
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                return 0;
            }
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_versions;";
        return Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

namespace PowerLease.Persistence.Sqlite;

/// <summary>
/// A migration could not be applied. The database is unchanged, because migrations run inside a
/// transaction, and <see cref="BackupPath" /> names the copy taken beforehand when there was
/// anything to copy.
/// </summary>
public sealed class MigrationException : Exception
{
    public MigrationException(int version, string? backupPath, Exception innerException)
        : base(BuildMessage(version, backupPath), innerException)
    {
        Version = version;
        BackupPath = backupPath;
    }

    public MigrationException()
    {
    }

    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The migration that failed, or zero when the failure was not in a specific migration.</summary>
    public int Version { get; }

    /// <summary>Where the database was copied to before the attempt, when a copy was taken.</summary>
    public string? BackupPath { get; }

    private static string BuildMessage(int version, string? backupPath) =>
        backupPath is null
            ? $"Migration {version} failed. The database has been left unchanged."
            : $"Migration {version} failed. The database has been left unchanged and a copy taken " +
              $"before the attempt is at {backupPath}.";
}

using Microsoft.Data.Sqlite;

namespace PowerLease.Persistence.Sqlite;

/// <summary>
/// Copies the database using SQLite's own backup mechanism.
/// <para>
/// Copying the database file is not an alternative. Under write-ahead logging, recent commits live in
/// a separate <c>-wal</c> file until a checkpoint moves them, so a file copy silently produces a
/// database missing the newest data -- including, potentially, the lease checkpoints written moments
/// before an upgrade. The backup mechanism reads through the connection and writes one consistent
/// file containing everything committed.
/// </para>
/// </summary>
public static class DatabaseBackup
{
    /// <summary>
    /// Write a consistent copy of the database behind <paramref name="source" /> to
    /// <paramref name="destinationPath" />, replacing whatever is there.
    /// </summary>
    public static void CopyTo(SqliteConnection source, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Pooling = false
        }.ToString();

        using var destination = new SqliteConnection(connectionString);
        destination.Open();
        source.BackupDatabase(destination);
    }
}

namespace PowerLease.Persistence.Sqlite;

/// <summary>What a migration run did.</summary>
public sealed class MigrationResult
{
    public MigrationResult(int fromVersion, int toVersion, string? backupPath, IReadOnlyList<int> appliedVersions)
    {
        ArgumentNullException.ThrowIfNull(appliedVersions);

        FromVersion = fromVersion;
        ToVersion = toVersion;
        BackupPath = backupPath;
        AppliedVersions = appliedVersions;
    }

    /// <summary>The version found on disk. Zero when there was no schema yet.</summary>
    public int FromVersion { get; }

    public int ToVersion { get; }

    /// <summary>
    /// Where the database was copied to before migrating, or null. No copy is taken for a database
    /// that had no schema, because there was nothing in it to lose.
    /// </summary>
    public string? BackupPath { get; }

    public IReadOnlyList<int> AppliedVersions { get; }

    public bool SchemaChanged => AppliedVersions.Count > 0;

    /// <summary>True when this run created the schema from nothing.</summary>
    public bool IsNewDatabase => FromVersion == 0;
}

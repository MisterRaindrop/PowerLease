namespace PowerLease.Persistence.Sqlite;

/// <summary>One schema version and the statements that produce it.</summary>
public sealed record Migration(int Version, string Description, string Sql);

using Microsoft.Data.Sqlite;

namespace PowerLease.Persistence.Sqlite;

/// <summary>
/// Opens connections to the history database with the settings the product depends on.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,

            // The service is the only writer and keeps one connection for its lifetime, so a pool
            // buys nothing. It costs something: a pooled connection outlives the using block that
            // appears to close it, which on Windows keeps the file locked after the code that owned
            // it has finished.
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>Open a connection and apply the pragmas.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        try
        {
            // Write-ahead logging lets readers work while the service writes, which is what allows the
            // CLI to query history through the service without ever blocking a write.
            Execute(connection, "PRAGMA journal_mode = WAL;");

            // NORMAL rather than FULL. Under write-ahead logging NORMAL cannot corrupt the database;
            // the worst case is losing the most recent commits after a power cut. For the one record
            // where that matters, a lease checkpoint, losing the newest value makes the lease resume
            // with its full original duration, which holds the machine awake for longer. The failure
            // mode of the cheaper setting therefore points in the safe direction.
            Execute(connection, "PRAGMA synchronous = NORMAL;");

            Execute(connection, "PRAGMA busy_timeout = 5000;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

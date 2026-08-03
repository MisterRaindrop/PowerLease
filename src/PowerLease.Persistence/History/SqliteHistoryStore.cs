using System.Globalization;
using Microsoft.Data.Sqlite;
using PowerLease.Domain;
using PowerLease.Persistence.Sqlite;

namespace PowerLease.Persistence.History;

/// <summary>
/// The history database.
/// <para>
/// One instance owns one connection and is the only thing that writes. That is not an optimisation:
/// the service is the single writer by design, and everything else reads through it, which is why
/// write-ahead logging is enough to keep queries from ever blocking a write.
/// </para>
/// <para>
/// Single writes get their own transaction. Where several writes have to land together, use
/// <see cref="BeginTransaction" /> and say so explicitly.
/// </para>
/// </summary>
public sealed class SqliteHistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteHistoryStore(SqliteConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connection = connections.Open();
    }

    public HistoryTransaction BeginTransaction() => new(_connection);

    public void Dispose() => _connection.Dispose();

    public void StartPowerSession(PowerSession session) => InOwnTransaction(tx => tx.StartPowerSession(session));

    public void EndPowerSession(string sessionId, DateTimeOffset endedAtUtc, string? endReason) =>
        InOwnTransaction(tx => tx.EndPowerSession(sessionId, endedAtUtc, endReason));

    public void InsertRawSample(RawSample sample) => InOwnTransaction(tx => tx.InsertRawSample(sample));

    public void InsertRawSamples(IEnumerable<RawSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        InOwnTransaction(tx =>
        {
            foreach (var sample in samples)
            {
                tx.InsertRawSample(sample);
            }
        });
    }

    public void RecordInhibitEvent(InhibitEvent inhibitEvent) =>
        InOwnTransaction(tx => tx.RecordInhibitEvent(inhibitEvent));

    public void UpsertLease(KeepAwakeLease lease) => InOwnTransaction(tx => tx.UpsertLease(lease));

    /// <summary>
    /// Read the leases.
    /// <para>
    /// A row that cannot be mapped is skipped rather than allowed to throw. This runs on the startup path, and
    /// a single unreadable record must not make every lease unloadable: that would leave the service unable to
    /// start and the machine unprotected for as long as it kept restarting. It is reachable without any
    /// corruption -- a downgrade after a later version adds a lease source leaves rows this build cannot map,
    /// and the schema version is unchanged so the migration check does not catch it.
    /// </para>
    /// <para>
    /// Skipping alone would not be safe, because a skipped lease is protection quietly lost. The identifiers are
    /// returned alongside so the caller can latch a fault, which holds the machine awake.
    /// </para>
    /// </summary>
    public LeaseLoadResult LoadLeases(LeaseStatus? status = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, reason, owner_user, remote_ip, process_id, process_name,
                   started_at_utc, expires_at_utc, last_renewed_at_utc, auto_renew, status,
                   ended_at_utc, end_reason, epoch_id, original_duration_seconds,
                   last_renew_duration_seconds, remaining_at_checkpoint_seconds, checkpoint_utc,
                   owner_sid
            FROM keep_awake_leases
            WHERE $status IS NULL OR status = $status
            ORDER BY started_at_utc, id;
            """;
        command.Parameters.AddWithValue("$status", status?.ToString() ?? (object)DBNull.Value);

        var leases = new List<KeepAwakeLease>();
        var unreadable = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0) ? "(no identifier)" : reader.GetString(0);

            try
            {
                leases.Add(MapLease(reader));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException)
            {
                unreadable.Add($"{id}: {error.Message}");
            }
        }

        return new LeaseLoadResult(leases, unreadable);
    }

    private static KeepAwakeLease MapLease(SqliteDataReader reader)
    {
        return new KeepAwakeLease
        {
            Id = reader.GetString(0),
            Source = Enum.Parse<LeaseSource>(reader.GetString(1)),
            Reason = TextOrNull(reader, 2),
            OwnerUser = TextOrNull(reader, 3),
            RemoteIp = TextOrNull(reader, 4),
            ProcessId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            ProcessName = TextOrNull(reader, 6),
            StartedAtUtc = Timestamps.Parse(reader.GetString(7)),
            ExpiresAtUtc = Timestamps.ParseOrNull(TextOrNull(reader, 8)),
            LastRenewedAtUtc = Timestamps.ParseOrNull(TextOrNull(reader, 9)),
            AutoRenew = reader.GetInt32(10) != 0,
            Status = Enum.Parse<LeaseStatus>(reader.GetString(11)),
            EndedAtUtc = Timestamps.ParseOrNull(TextOrNull(reader, 12)),
            EndReason = TextOrNull(reader, 13),
            EpochId = Guid.Parse(reader.GetString(14)),
            OriginalDuration = TimeSpan.FromSeconds(reader.GetDouble(15)),
            LastRenewDuration = SecondsOrNull(reader, 16),
            RemainingAtCheckpoint = SecondsOrNull(reader, 17),
            CheckpointUtc = Timestamps.ParseOrNull(TextOrNull(reader, 18)),
            OwnerSid = TextOrNull(reader, 19)
        };
    }

    public PowerSession? LoadPowerSession(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, boot_id, started_at_utc, ended_at_utc, start_reason, end_reason,
                   energy_wh, energy_source, is_estimated
            FROM power_sessions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PowerSession
        {
            Id = reader.GetString(0),
            BootId = TextOrNull(reader, 1),
            StartedAtUtc = Timestamps.Parse(reader.GetString(2)),
            EndedAtUtc = Timestamps.ParseOrNull(TextOrNull(reader, 3)),
            StartReason = TextOrNull(reader, 4),
            EndReason = TextOrNull(reader, 5),
            EnergyWh = DoubleOrNull(reader, 6),
            EnergySource = TextOrNull(reader, 7),
            IsEstimated = reader.GetInt32(8) != 0
        };
    }

    public IReadOnlyList<RawSample> ReadRawSamples(DateTimeOffset fromInclusiveUtc, DateTimeOffset toExclusiveUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT power_session_id, sampled_at_utc, cpu_percent, memory_percent,
                   disk_read_bps, disk_write_bps, network_rx_bps, network_tx_bps,
                   state, ssh_session_count, quality_flags
            FROM raw_samples
            WHERE sampled_at_utc >= $from AND sampled_at_utc < $to
            ORDER BY sampled_at_utc, id;
            """;
        command.Parameters.AddWithValue("$from", Timestamps.ToText(fromInclusiveUtc));
        command.Parameters.AddWithValue("$to", Timestamps.ToText(toExclusiveUtc));

        var samples = new List<RawSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new RawSample
            {
                PowerSessionId = reader.GetString(0),
                SampledAtUtc = Timestamps.Parse(reader.GetString(1)),
                CpuPercent = DoubleOrNull(reader, 2),
                MemoryPercent = DoubleOrNull(reader, 3),
                DiskReadBytesPerSecond = DoubleOrNull(reader, 4),
                DiskWriteBytesPerSecond = DoubleOrNull(reader, 5),
                NetworkRxBytesPerSecond = DoubleOrNull(reader, 6),
                NetworkTxBytesPerSecond = DoubleOrNull(reader, 7),
                State = Enum.Parse<ProtectionState>(reader.GetString(8)),
                SshSessionCount = reader.GetInt32(9),
                QualityFlags = (SampleQuality)reader.GetInt32(10)
            });
        }

        return samples;
    }

    public IReadOnlyList<MetricAggregate> ReadMinuteAggregates(
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toExclusiveUtc) =>
        ReadAggregates("minute_aggregates", fromInclusiveUtc, toExclusiveUtc);

    public IReadOnlyList<MetricAggregate> ReadHourAggregates(
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toExclusiveUtc) =>
        ReadAggregates("hour_aggregates", fromInclusiveUtc, toExclusiveUtc);

    /// <summary>
    /// Group the measurements in a period into minute buckets.
    /// <para>
    /// The grouping and the arithmetic are done by the database, in one query, so a service that has
    /// been down does not pay for the minutes it was absent.
    /// </para>
    /// </summary>
    /// <param name="sampleInterval">
    /// How far apart measurements are expected. Used to turn a count of measurements in a state into a
    /// duration, and to say how many measurements a full bucket should hold.
    /// </param>
    public IReadOnlyList<MetricAggregate> RollUpRawSamplesByMinute(
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toExclusiveUtc,
        TimeSpan sampleInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleInterval, TimeSpan.Zero);

        var expected = Math.Max(1, (int)Math.Round(TimeSpan.FromMinutes(1) / sampleInterval));

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT substr(sampled_at_utc, 1, {TimeBuckets.MinuteKeyLength}) AS bucket,
                   COUNT(*),
                   AVG(cpu_percent), MIN(cpu_percent), MAX(cpu_percent),
                   AVG(memory_percent),
                   AVG(disk_read_bps), MAX(disk_read_bps),
                   AVG(disk_write_bps), MAX(disk_write_bps),
                   AVG(network_rx_bps), MAX(network_rx_bps),
                   AVG(network_tx_bps), MAX(network_tx_bps),
                   MAX(ssh_session_count),
                   SUM(CASE WHEN state = 'Protected' THEN 1 ELSE 0 END) * $interval,
                   SUM(CASE WHEN state = 'Released' THEN 1 ELSE 0 END) * $interval,
                   SUM(CASE WHEN state = 'Unprotected' THEN 1 ELSE 0 END) * $interval
            FROM raw_samples
            WHERE sampled_at_utc >= $from AND sampled_at_utc < $to
            GROUP BY bucket
            ORDER BY bucket;
            """;
        command.Parameters.AddWithValue("$from", Timestamps.ToText(fromInclusiveUtc));
        command.Parameters.AddWithValue("$to", Timestamps.ToText(toExclusiveUtc));
        command.Parameters.AddWithValue("$interval", sampleInterval.TotalSeconds);

        var buckets = new List<MetricAggregate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buckets.Add(new MetricAggregate
            {
                BucketStartUtc = TimeBuckets.ParseMinuteKey(reader.GetString(0)),
                SampleCount = reader.GetInt32(1),
                ExpectedSampleCount = expected,
                CpuAverage = DoubleOrNull(reader, 2),
                CpuMinimum = DoubleOrNull(reader, 3),
                CpuMaximum = DoubleOrNull(reader, 4),
                MemoryAverage = DoubleOrNull(reader, 5),
                DiskReadAverage = DoubleOrNull(reader, 6),
                DiskReadMaximum = DoubleOrNull(reader, 7),
                DiskWriteAverage = DoubleOrNull(reader, 8),
                DiskWriteMaximum = DoubleOrNull(reader, 9),
                NetworkRxAverage = DoubleOrNull(reader, 10),
                NetworkRxMaximum = DoubleOrNull(reader, 11),
                NetworkTxAverage = DoubleOrNull(reader, 12),
                NetworkTxMaximum = DoubleOrNull(reader, 13),
                SshSessionMaximum = reader.GetInt32(14),
                ProtectedFor = TimeSpan.FromSeconds(reader.GetDouble(15)),
                ReleasedFor = TimeSpan.FromSeconds(reader.GetDouble(16)),
                UnprotectedFor = TimeSpan.FromSeconds(reader.GetDouble(17))
            });
        }

        return buckets;
    }

    /// <summary>
    /// Group minute buckets into hour buckets.
    /// <para>
    /// Averages are weighted by how many measurements each minute held, so an hour containing one minute
    /// built from a single measurement and fifty-nine built from six is not dragged towards that one
    /// minute. The weight excludes minutes where the metric itself was missing, which otherwise inflates
    /// the divisor and pulls every average down towards zero.
    /// </para>
    /// </summary>
    public IReadOnlyList<MetricAggregate> RollUpMinuteAggregatesByHour(
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toExclusiveUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT substr(bucket_start_utc, 1, {TimeBuckets.HourKeyLength}) AS bucket,
                   SUM(sample_count),
                   SUM(expected_sample_count),
                   {Weighted("cpu_avg")}, MIN(cpu_min), MAX(cpu_max),
                   {Weighted("memory_avg")},
                   {Weighted("disk_read_avg")}, MAX(disk_read_max),
                   {Weighted("disk_write_avg")}, MAX(disk_write_max),
                   {Weighted("network_rx_avg")}, MAX(network_rx_max),
                   {Weighted("network_tx_avg")}, MAX(network_tx_max),
                   MAX(ssh_session_max),
                   SUM(protected_seconds), SUM(released_seconds), SUM(unprotected_seconds)
            FROM minute_aggregates
            WHERE bucket_start_utc >= $from AND bucket_start_utc < $to
            GROUP BY bucket
            ORDER BY bucket;
            """;
        command.Parameters.AddWithValue("$from", Timestamps.ToText(fromInclusiveUtc));
        command.Parameters.AddWithValue("$to", Timestamps.ToText(toExclusiveUtc));

        var buckets = new List<MetricAggregate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buckets.Add(new MetricAggregate
            {
                BucketStartUtc = TimeBuckets.ParseHourKey(reader.GetString(0)),
                SampleCount = reader.GetInt32(1),
                ExpectedSampleCount = reader.GetInt32(2),
                CpuAverage = DoubleOrNull(reader, 3),
                CpuMinimum = DoubleOrNull(reader, 4),
                CpuMaximum = DoubleOrNull(reader, 5),
                MemoryAverage = DoubleOrNull(reader, 6),
                DiskReadAverage = DoubleOrNull(reader, 7),
                DiskReadMaximum = DoubleOrNull(reader, 8),
                DiskWriteAverage = DoubleOrNull(reader, 9),
                DiskWriteMaximum = DoubleOrNull(reader, 10),
                NetworkRxAverage = DoubleOrNull(reader, 11),
                NetworkRxMaximum = DoubleOrNull(reader, 12),
                NetworkTxAverage = DoubleOrNull(reader, 13),
                NetworkTxMaximum = DoubleOrNull(reader, 14),
                SshSessionMaximum = reader.GetInt32(15),
                ProtectedFor = TimeSpan.FromSeconds(reader.GetDouble(16)),
                ReleasedFor = TimeSpan.FromSeconds(reader.GetDouble(17)),
                UnprotectedFor = TimeSpan.FromSeconds(reader.GetDouble(18))
            });
        }

        return buckets;
    }

    private static string Weighted(string column) =>
        $"SUM({column} * sample_count) / " +
        $"NULLIF(SUM(CASE WHEN {column} IS NOT NULL THEN sample_count ELSE 0 END), 0)";

    public DateTimeOffset? GetWatermark(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT completed_through_utc FROM aggregate_watermarks WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);

        return Timestamps.ParseOrNull(command.ExecuteScalar() as string);
    }

    public string? GetBookmark(string channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT bookmark FROM event_bookmarks WHERE channel = $channel;";
        command.Parameters.AddWithValue("$channel", channel);

        return command.ExecuteScalar() as string;
    }

    public bool IsProcessed(string channel, string recordId)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentException.ThrowIfNullOrEmpty(recordId);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM processed_event WHERE channel = $channel AND record_id = $recordId;
            """;
        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$recordId", recordId);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public DateTimeOffset? EarliestRawSampleUtc() => EarliestOf("SELECT MIN(sampled_at_utc) FROM raw_samples;");

    public DateTimeOffset? EarliestMinuteBucketUtc() =>
        EarliestOf("SELECT MIN(bucket_start_utc) FROM minute_aggregates;");

    public long CountRawSamples() => Count("SELECT COUNT(*) FROM raw_samples;");

    public long CountMinuteAggregates() => Count("SELECT COUNT(*) FROM minute_aggregates;");

    public long CountInhibitEvents() => Count("SELECT COUNT(*) FROM inhibit_events;");

    // Returns the concrete list because the analyser objects to an interface on a private helper whose
    // only callers immediately hand it out as IReadOnlyList.
    private List<MetricAggregate> ReadAggregates(
        string table,
        DateTimeOffset fromInclusiveUtc,
        DateTimeOffset toExclusiveUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT bucket_start_utc, sample_count, expected_sample_count,
                   cpu_avg, cpu_min, cpu_max, memory_avg,
                   disk_read_avg, disk_read_max, disk_write_avg, disk_write_max,
                   network_rx_avg, network_rx_max, network_tx_avg, network_tx_max,
                   protected_seconds, released_seconds, unprotected_seconds, ssh_session_max
            FROM {table}
            WHERE bucket_start_utc >= $from AND bucket_start_utc < $to
            ORDER BY bucket_start_utc;
            """;
        command.Parameters.AddWithValue("$from", Timestamps.ToText(fromInclusiveUtc));
        command.Parameters.AddWithValue("$to", Timestamps.ToText(toExclusiveUtc));

        var aggregates = new List<MetricAggregate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            aggregates.Add(new MetricAggregate
            {
                BucketStartUtc = Timestamps.Parse(reader.GetString(0)),
                SampleCount = reader.GetInt32(1),
                ExpectedSampleCount = reader.GetInt32(2),
                CpuAverage = DoubleOrNull(reader, 3),
                CpuMinimum = DoubleOrNull(reader, 4),
                CpuMaximum = DoubleOrNull(reader, 5),
                MemoryAverage = DoubleOrNull(reader, 6),
                DiskReadAverage = DoubleOrNull(reader, 7),
                DiskReadMaximum = DoubleOrNull(reader, 8),
                DiskWriteAverage = DoubleOrNull(reader, 9),
                DiskWriteMaximum = DoubleOrNull(reader, 10),
                NetworkRxAverage = DoubleOrNull(reader, 11),
                NetworkRxMaximum = DoubleOrNull(reader, 12),
                NetworkTxAverage = DoubleOrNull(reader, 13),
                NetworkTxMaximum = DoubleOrNull(reader, 14),
                ProtectedFor = TimeSpan.FromSeconds(reader.GetDouble(15)),
                ReleasedFor = TimeSpan.FromSeconds(reader.GetDouble(16)),
                UnprotectedFor = TimeSpan.FromSeconds(reader.GetDouble(17)),
                SshSessionMaximum = reader.GetInt32(18)
            });
        }

        return aggregates;
    }

    private DateTimeOffset? EarliestOf(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Timestamps.ParseOrNull(command.ExecuteScalar() as string);
    }

    private long Count(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void InOwnTransaction(Action<HistoryTransaction> write)
    {
        using var transaction = BeginTransaction();
        write(transaction);
        transaction.Commit();
    }

    private static string? TextOrNull(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static double? DoubleOrNull(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetDouble(index);

    private static TimeSpan? SecondsOrNull(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : TimeSpan.FromSeconds(reader.GetDouble(index));
}

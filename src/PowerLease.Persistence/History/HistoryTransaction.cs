using System.Globalization;
using Microsoft.Data.Sqlite;
using PowerLease.Domain;
using PowerLease.Persistence.Sqlite;

namespace PowerLease.Persistence.History;

/// <summary>
/// A set of writes that either all land or none do.
/// <para>
/// This exists so that the pairings that must be atomic can be written down as such. Advancing a log
/// bookmark and creating the lease that log entry called for is the important one: doing the first and
/// crashing before the second would drop the login permanently, and the machine would go to sleep
/// while the user was connected.
/// </para>
/// <para>
/// Nothing is written until <see cref="Commit" /> is called. Disposing without committing rolls
/// everything back.
/// </para>
/// </summary>
public sealed class HistoryTransaction : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    internal HistoryTransaction(SqliteConnection connection)
    {
        _connection = connection;
        _transaction = connection.BeginTransaction();
    }

    public void Commit() => _transaction.Commit();

    public void Dispose()
    {
        // No explicit rollback. Disposing an uncommitted transaction already rolls it back, while calling
        // Rollback() throws if the transaction has completed or the connection has since been closed -- and an
        // exception thrown from Dispose replaces whatever real exception was unwinding through the using block.
        _transaction.Dispose();
    }

    public void StartPowerSession(PowerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var command = Command("""
            INSERT INTO power_sessions (
                id, boot_id, started_at_utc, ended_at_utc, start_reason, end_reason,
                energy_wh, energy_source, is_estimated)
            VALUES (
                $id, $bootId, $startedAt, $endedAt, $startReason, $endReason,
                $energyWh, $energySource, $isEstimated);
            """);

        command.Parameters.AddWithValue("$id", session.Id);
        AddNullable(command, "$bootId", session.BootId);
        command.Parameters.AddWithValue("$startedAt", Timestamps.ToText(session.StartedAtUtc));
        AddNullable(command, "$endedAt", Timestamps.ToTextOrNull(session.EndedAtUtc));
        AddNullable(command, "$startReason", session.StartReason);
        AddNullable(command, "$endReason", session.EndReason);
        AddNullable(command, "$energyWh", session.EnergyWh);
        AddNullable(command, "$energySource", session.EnergySource);
        command.Parameters.AddWithValue("$isEstimated", session.IsEstimated ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void EndPowerSession(string sessionId, DateTimeOffset endedAtUtc, string? endReason)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        using var command = Command("""
            UPDATE power_sessions
            SET ended_at_utc = $endedAt, end_reason = $endReason
            WHERE id = $id;
            """);

        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$endedAt", Timestamps.ToText(endedAtUtc));
        AddNullable(command, "$endReason", endReason);
        command.ExecuteNonQuery();
    }

    public void InsertRawSample(RawSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        using var command = Command("""
            INSERT INTO raw_samples (
                power_session_id, sampled_at_utc, cpu_percent, memory_percent,
                disk_read_bps, disk_write_bps, network_rx_bps, network_tx_bps,
                state, ssh_session_count, quality_flags)
            VALUES (
                $sessionId, $sampledAt, $cpu, $memory,
                $diskRead, $diskWrite, $networkRx, $networkTx,
                $state, $sshCount, $quality);
            """);

        command.Parameters.AddWithValue("$sessionId", sample.PowerSessionId);
        command.Parameters.AddWithValue("$sampledAt", Timestamps.ToText(sample.SampledAtUtc));
        AddNullable(command, "$cpu", sample.CpuPercent);
        AddNullable(command, "$memory", sample.MemoryPercent);
        AddNullable(command, "$diskRead", sample.DiskReadBytesPerSecond);
        AddNullable(command, "$diskWrite", sample.DiskWriteBytesPerSecond);
        AddNullable(command, "$networkRx", sample.NetworkRxBytesPerSecond);
        AddNullable(command, "$networkTx", sample.NetworkTxBytesPerSecond);
        command.Parameters.AddWithValue("$state", sample.State.ToString());
        command.Parameters.AddWithValue("$sshCount", sample.SshSessionCount);
        command.Parameters.AddWithValue("$quality", (int)sample.QualityFlags);
        command.ExecuteNonQuery();
    }

    public void RecordInhibitEvent(InhibitEvent inhibitEvent)
    {
        ArgumentNullException.ThrowIfNull(inhibitEvent);

        using var command = Command("""
            INSERT INTO inhibit_events (
                occurred_at_utc, change, protection_state, inhibitor_kinds, reason, revision)
            VALUES ($occurredAt, $change, $state, $kinds, $reason, $revision);
            """);

        command.Parameters.AddWithValue("$occurredAt", Timestamps.ToText(inhibitEvent.OccurredAtUtc));
        command.Parameters.AddWithValue("$change", inhibitEvent.Change.ToString());
        command.Parameters.AddWithValue("$state", inhibitEvent.ProtectionState.ToString());
        AddNullable(
            command,
            "$kinds",
            inhibitEvent.InhibitorKinds.Count == 0 ? null : string.Join(",", inhibitEvent.InhibitorKinds));
        AddNullable(command, "$reason", inhibitEvent.Reason);
        command.Parameters.AddWithValue("$revision", inhibitEvent.Revision);
        command.ExecuteNonQuery();
    }

    /// <summary>Create or update a lease, keyed on its identifier.</summary>
    public void UpsertLease(KeepAwakeLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        using var command = Command("""
            INSERT INTO keep_awake_leases (
                id, source, reason, owner_user, remote_ip, process_id, process_name,
                started_at_utc, expires_at_utc, last_renewed_at_utc, auto_renew, status,
                ended_at_utc, end_reason, epoch_id, original_duration_seconds,
                last_renew_duration_seconds, remaining_at_checkpoint_seconds, checkpoint_utc)
            VALUES (
                $id, $source, $reason, $ownerUser, $remoteIp, $processId, $processName,
                $startedAt, $expiresAt, $lastRenewedAt, $autoRenew, $status,
                $endedAt, $endReason, $epochId, $originalDuration,
                $lastRenewDuration, $remainingAtCheckpoint, $checkpointUtc)
            ON CONFLICT (id) DO UPDATE SET
                source = excluded.source,
                reason = excluded.reason,
                owner_user = excluded.owner_user,
                remote_ip = excluded.remote_ip,
                process_id = excluded.process_id,
                process_name = excluded.process_name,
                started_at_utc = excluded.started_at_utc,
                expires_at_utc = excluded.expires_at_utc,
                last_renewed_at_utc = excluded.last_renewed_at_utc,
                auto_renew = excluded.auto_renew,
                status = excluded.status,
                ended_at_utc = excluded.ended_at_utc,
                end_reason = excluded.end_reason,
                epoch_id = excluded.epoch_id,
                original_duration_seconds = excluded.original_duration_seconds,
                last_renew_duration_seconds = excluded.last_renew_duration_seconds,
                remaining_at_checkpoint_seconds = excluded.remaining_at_checkpoint_seconds,
                checkpoint_utc = excluded.checkpoint_utc;
            """);

        command.Parameters.AddWithValue("$id", lease.Id);
        command.Parameters.AddWithValue("$source", lease.Source.ToString());
        AddNullable(command, "$reason", lease.Reason);
        AddNullable(command, "$ownerUser", lease.OwnerUser);
        AddNullable(command, "$remoteIp", lease.RemoteIp);
        AddNullable(command, "$processId", lease.ProcessId);
        AddNullable(command, "$processName", lease.ProcessName);
        command.Parameters.AddWithValue("$startedAt", Timestamps.ToText(lease.StartedAtUtc));
        AddNullable(command, "$expiresAt", Timestamps.ToTextOrNull(lease.ExpiresAtUtc));
        AddNullable(command, "$lastRenewedAt", Timestamps.ToTextOrNull(lease.LastRenewedAtUtc));
        command.Parameters.AddWithValue("$autoRenew", lease.AutoRenew ? 1 : 0);
        command.Parameters.AddWithValue("$status", lease.Status.ToString());
        AddNullable(command, "$endedAt", Timestamps.ToTextOrNull(lease.EndedAtUtc));
        AddNullable(command, "$endReason", lease.EndReason);
        command.Parameters.AddWithValue("$epochId", lease.EpochId.ToString());
        command.Parameters.AddWithValue("$originalDuration", lease.OriginalDuration.TotalSeconds);
        AddNullable(command, "$lastRenewDuration", lease.LastRenewDuration?.TotalSeconds);
        AddNullable(command, "$remainingAtCheckpoint", lease.RemainingAtCheckpoint?.TotalSeconds);
        AddNullable(command, "$checkpointUtc", Timestamps.ToTextOrNull(lease.CheckpointUtc));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Note that an external log record has been acted on.
    /// </summary>
    /// <returns>
    /// False when it had already been recorded, which means the effects must not be applied again.
    /// </returns>
    public bool TryMarkProcessed(string channel, string recordId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentException.ThrowIfNullOrEmpty(recordId);

        using var command = Command("""
            INSERT INTO processed_event (channel, record_id, processed_at_utc)
            VALUES ($channel, $recordId, $processedAt)
            ON CONFLICT (channel, record_id) DO NOTHING;
            """);

        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$recordId", recordId);
        command.Parameters.AddWithValue("$processedAt", Timestamps.ToText(nowUtc));
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Move a log's read position. Must be committed together with the effects of everything read up to
    /// it, or a crash in between loses those records for good.
    /// </summary>
    public void SetBookmark(string channel, string bookmark, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(bookmark);

        using var command = Command("""
            INSERT INTO event_bookmarks (channel, bookmark, updated_at_utc)
            VALUES ($channel, $bookmark, $updatedAt)
            ON CONFLICT (channel) DO UPDATE SET
                bookmark = excluded.bookmark,
                updated_at_utc = excluded.updated_at_utc;
            """);

        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$bookmark", bookmark);
        command.Parameters.AddWithValue("$updatedAt", Timestamps.ToText(nowUtc));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Claim a command identifier before acting on it, in the same transaction as the action.
    /// </summary>
    public CommandRecord RecordCommand(
        string callerSid,
        string requestId,
        string payloadHash,
        string? resultJson,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(callerSid);
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentException.ThrowIfNullOrEmpty(payloadHash);

        using (var existing = Command("""
            SELECT payload_hash, result_json FROM idempotent_commands
            WHERE caller_sid = $sid AND request_id = $requestId;
            """))
        {
            existing.Parameters.AddWithValue("$sid", callerSid);
            existing.Parameters.AddWithValue("$requestId", requestId);

            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var storedHash = reader.GetString(0);
                var storedResult = reader.IsDBNull(1) ? null : reader.GetString(1);

                return string.Equals(storedHash, payloadHash, StringComparison.Ordinal)
                    ? new CommandRecord(CommandRecordOutcome.AlreadyCompleted, storedResult)
                    : new CommandRecord(CommandRecordOutcome.PayloadConflict, null);
            }
        }

        using var insert = Command("""
            INSERT INTO idempotent_commands (caller_sid, request_id, payload_hash, result_json, completed_at_utc)
            VALUES ($sid, $requestId, $payloadHash, $resultJson, $completedAt);
            """);

        insert.Parameters.AddWithValue("$sid", callerSid);
        insert.Parameters.AddWithValue("$requestId", requestId);
        insert.Parameters.AddWithValue("$payloadHash", payloadHash);
        AddNullable(insert, "$resultJson", resultJson);
        insert.Parameters.AddWithValue("$completedAt", Timestamps.ToText(nowUtc));
        insert.ExecuteNonQuery();

        return new CommandRecord(CommandRecordOutcome.Recorded, null);
    }

    public void UpsertMinuteAggregate(MetricAggregate aggregate) => UpsertAggregate("minute_aggregates", aggregate);

    public void UpsertHourAggregate(MetricAggregate aggregate) => UpsertAggregate("hour_aggregates", aggregate);

    /// <summary>
    /// Record how far an aggregation level has been completed. Source rows are only ever deleted up to
    /// this point.
    /// </summary>
    public void SetWatermark(string name, DateTimeOffset completedThroughUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        using var command = Command("""
            INSERT INTO aggregate_watermarks (name, completed_through_utc)
            VALUES ($name, $through)
            ON CONFLICT (name) DO UPDATE SET completed_through_utc = excluded.completed_through_utc;
            """);

        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$through", Timestamps.ToText(completedThroughUtc));
        command.ExecuteNonQuery();
    }

    public int DeleteRawSamplesBefore(DateTimeOffset cutoffUtc) =>
        DeleteBefore("DELETE FROM raw_samples WHERE sampled_at_utc < $cutoff;", cutoffUtc);

    public int DeleteMinuteAggregatesBefore(DateTimeOffset cutoffUtc) =>
        DeleteBefore("DELETE FROM minute_aggregates WHERE bucket_start_utc < $cutoff;", cutoffUtc);

    public int DeleteInhibitEventsBefore(DateTimeOffset cutoffUtc) =>
        DeleteBefore("DELETE FROM inhibit_events WHERE occurred_at_utc < $cutoff;", cutoffUtc);

    public int DeletePowerEventsBefore(DateTimeOffset cutoffUtc) =>
        DeleteBefore("DELETE FROM power_events WHERE occurred_at_utc < $cutoff;", cutoffUtc);

    public int DeleteAuditEventsBefore(DateTimeOffset cutoffUtc) =>
        DeleteBefore("DELETE FROM audit_events WHERE occurred_at_utc < $cutoff;", cutoffUtc);

    private void UpsertAggregate(string table, MetricAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        // Keyed on the bucket, so running aggregation again over a period it has already covered
        // replaces the row rather than adding a second one.
        using var command = Command($"""
            INSERT INTO {table} (
                bucket_start_utc, sample_count, expected_sample_count, completeness,
                cpu_avg, cpu_min, cpu_max, memory_avg,
                disk_read_avg, disk_read_max, disk_write_avg, disk_write_max,
                network_rx_avg, network_rx_max, network_tx_avg, network_tx_max,
                protected_seconds, released_seconds, unprotected_seconds, ssh_session_max)
            VALUES (
                $bucket, $sampleCount, $expected, $completeness,
                $cpuAvg, $cpuMin, $cpuMax, $memoryAvg,
                $diskReadAvg, $diskReadMax, $diskWriteAvg, $diskWriteMax,
                $networkRxAvg, $networkRxMax, $networkTxAvg, $networkTxMax,
                $protectedSeconds, $releasedSeconds, $unprotectedSeconds, $sshMax)
            ON CONFLICT (bucket_start_utc) DO UPDATE SET
                sample_count = excluded.sample_count,
                expected_sample_count = excluded.expected_sample_count,
                completeness = excluded.completeness,
                cpu_avg = excluded.cpu_avg,
                cpu_min = excluded.cpu_min,
                cpu_max = excluded.cpu_max,
                memory_avg = excluded.memory_avg,
                disk_read_avg = excluded.disk_read_avg,
                disk_read_max = excluded.disk_read_max,
                disk_write_avg = excluded.disk_write_avg,
                disk_write_max = excluded.disk_write_max,
                network_rx_avg = excluded.network_rx_avg,
                network_rx_max = excluded.network_rx_max,
                network_tx_avg = excluded.network_tx_avg,
                network_tx_max = excluded.network_tx_max,
                protected_seconds = excluded.protected_seconds,
                released_seconds = excluded.released_seconds,
                unprotected_seconds = excluded.unprotected_seconds,
                ssh_session_max = excluded.ssh_session_max;
            """);

        command.Parameters.AddWithValue("$bucket", Timestamps.ToText(aggregate.BucketStartUtc));
        command.Parameters.AddWithValue("$sampleCount", aggregate.SampleCount);
        command.Parameters.AddWithValue("$expected", aggregate.ExpectedSampleCount);
        command.Parameters.AddWithValue("$completeness", aggregate.Completeness);
        AddNullable(command, "$cpuAvg", aggregate.CpuAverage);
        AddNullable(command, "$cpuMin", aggregate.CpuMinimum);
        AddNullable(command, "$cpuMax", aggregate.CpuMaximum);
        AddNullable(command, "$memoryAvg", aggregate.MemoryAverage);
        AddNullable(command, "$diskReadAvg", aggregate.DiskReadAverage);
        AddNullable(command, "$diskReadMax", aggregate.DiskReadMaximum);
        AddNullable(command, "$diskWriteAvg", aggregate.DiskWriteAverage);
        AddNullable(command, "$diskWriteMax", aggregate.DiskWriteMaximum);
        AddNullable(command, "$networkRxAvg", aggregate.NetworkRxAverage);
        AddNullable(command, "$networkRxMax", aggregate.NetworkRxMaximum);
        AddNullable(command, "$networkTxAvg", aggregate.NetworkTxAverage);
        AddNullable(command, "$networkTxMax", aggregate.NetworkTxMaximum);
        command.Parameters.AddWithValue("$protectedSeconds", aggregate.ProtectedFor.TotalSeconds);
        command.Parameters.AddWithValue("$releasedSeconds", aggregate.ReleasedFor.TotalSeconds);
        command.Parameters.AddWithValue("$unprotectedSeconds", aggregate.UnprotectedFor.TotalSeconds);
        command.Parameters.AddWithValue("$sshMax", aggregate.SshSessionMaximum);
        command.ExecuteNonQuery();
    }

    private int DeleteBefore(string sql, DateTimeOffset cutoffUtc)
    {
        using var command = Command(sql);
        command.Parameters.AddWithValue("$cutoff", Timestamps.ToText(cutoffUtc));
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        return command;
    }

    // A nullable value type boxes to a null reference when it has no value, so one overload covers
    // strings, doubles and integers alike.
    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? (object)DBNull.Value);
}

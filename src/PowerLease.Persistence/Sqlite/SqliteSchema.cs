namespace PowerLease.Persistence.Sqlite;

/// <summary>
/// The database schema, as migrations.
/// <para>
/// Durations are stored as seconds in a REAL column because SQLite has no interval type, and
/// timestamps as ISO-8601 UTC text because that sorts correctly as a string and survives being read
/// by anything else.
/// </para>
/// </summary>
public static class SqliteSchema
{
    /// <summary>
    /// The initial schema. Applied as one statement batch inside one transaction, so a machine either
    /// has the whole schema or none of it.
    /// </summary>
    public const string Version1 = """
        CREATE TABLE schema_versions (
            version         INTEGER PRIMARY KEY,
            applied_at_utc  TEXT NOT NULL,
            description     TEXT
        );

        CREATE TABLE power_sessions (
            id              TEXT PRIMARY KEY,
            boot_id         TEXT,
            started_at_utc  TEXT NOT NULL,
            ended_at_utc    TEXT,
            start_reason    TEXT,
            end_reason      TEXT,
            energy_wh       REAL,
            energy_source   TEXT,
            is_estimated    INTEGER NOT NULL
        );

        CREATE TABLE keep_awake_leases (
            id                              TEXT PRIMARY KEY,
            source                          TEXT NOT NULL,
            reason                          TEXT,
            owner_user                      TEXT,

            -- Who may renew or release this lease after a restart. Stored rather than resolved from
            -- owner_user on the way back in, because that resolution can quietly land on a different
            -- account. Null means unknown, which the kernel treats as administrator-only.
            owner_sid                       TEXT,
            remote_ip                       TEXT,
            process_id                      INTEGER,
            process_name                    TEXT,
            started_at_utc                  TEXT NOT NULL,
            expires_at_utc                  TEXT,
            last_renewed_at_utc             TEXT,
            auto_renew                      INTEGER NOT NULL,
            status                          TEXT NOT NULL,
            ended_at_utc                    TEXT,
            end_reason                      TEXT,

            -- Restoring a lease after the monotonic clock epoch changes. The remaining time is the
            -- trustworthy field; checkpoint_utc is kept for display and forensics and is deliberately
            -- not used to age the lease, because the wall clock can be adjusted between epochs.
            epoch_id                        TEXT NOT NULL,
            original_duration_seconds       REAL NOT NULL,
            last_renew_duration_seconds     REAL,
            remaining_at_checkpoint_seconds REAL,
            checkpoint_utc                  TEXT
        );

        CREATE INDEX ix_keep_awake_leases_status ON keep_awake_leases (status);

        CREATE TABLE ssh_sessions (
            id                      TEXT PRIMARY KEY,
            user_name               TEXT,
            remote_ip               TEXT,
            remote_port             INTEGER,
            local_port              INTEGER,
            started_at_utc          TEXT NOT NULL,
            authenticated_at_utc    TEXT,
            ended_at_utc            TEXT,
            detection_source        TEXT,
            confidence              TEXT,
            lease_id                TEXT
        );

        CREATE INDEX ix_ssh_sessions_open ON ssh_sessions (ended_at_utc);

        CREATE TABLE raw_samples (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            power_session_id    TEXT NOT NULL,
            sampled_at_utc      TEXT NOT NULL,
            cpu_percent         REAL,
            gpu_percent         REAL,
            memory_percent      REAL,
            disk_read_bps       REAL,
            disk_write_bps      REAL,
            network_rx_bps      REAL,
            network_tx_bps      REAL,
            cpu_temp_c          REAL,
            gpu_temp_c          REAL,
            ssd_temp_c          REAL,
            fan_rpm             REAL,
            power_w             REAL,
            power_source        TEXT,
            power_estimated     INTEGER,
            state               TEXT NOT NULL,
            ssh_session_count   INTEGER NOT NULL,
            quality_flags       INTEGER NOT NULL,

            -- Declared so a sample cannot be written against a session that does not exist. Without
            -- it, samples orphaned by a bug would still aggregate and quietly report history that
            -- belongs to no run of the machine.
            FOREIGN KEY (power_session_id) REFERENCES power_sessions (id)
        );

        CREATE INDEX ix_raw_samples_sampled_at ON raw_samples (sampled_at_utc);

        CREATE TABLE minute_aggregates (
            bucket_start_utc            TEXT PRIMARY KEY,
            sample_count                INTEGER NOT NULL,
            expected_sample_count       INTEGER NOT NULL,
            completeness                REAL NOT NULL,
            cpu_avg                     REAL,
            cpu_min                     REAL,
            cpu_max                     REAL,
            gpu_avg                     REAL,
            gpu_max                     REAL,
            memory_avg                  REAL,
            disk_read_avg               REAL,
            disk_read_max               REAL,
            disk_write_avg              REAL,
            disk_write_max              REAL,
            network_rx_avg              REAL,
            network_rx_max              REAL,
            network_tx_avg              REAL,
            network_tx_max              REAL,
            cpu_temp_avg                REAL,
            cpu_temp_max                REAL,
            gpu_temp_avg                REAL,
            gpu_temp_max                REAL,
            ssd_temp_avg                REAL,
            ssd_temp_max                REAL,
            fan_rpm_avg                 REAL,
            power_avg                   REAL,
            power_max                   REAL,
            energy_wh                   REAL,
            high_temperature_seconds    REAL NOT NULL DEFAULT 0,
            protected_seconds           REAL NOT NULL DEFAULT 0,
            released_seconds            REAL NOT NULL DEFAULT 0,
            unprotected_seconds         REAL NOT NULL DEFAULT 0,
            ssh_session_max             INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE hour_aggregates (
            bucket_start_utc            TEXT PRIMARY KEY,
            sample_count                INTEGER NOT NULL,
            expected_sample_count       INTEGER NOT NULL,
            completeness                REAL NOT NULL,
            cpu_avg                     REAL,
            cpu_min                     REAL,
            cpu_max                     REAL,
            gpu_avg                     REAL,
            gpu_max                     REAL,
            memory_avg                  REAL,
            disk_read_avg               REAL,
            disk_read_max               REAL,
            disk_write_avg              REAL,
            disk_write_max              REAL,
            network_rx_avg              REAL,
            network_rx_max              REAL,
            network_tx_avg              REAL,
            network_tx_max              REAL,
            cpu_temp_avg                REAL,
            cpu_temp_max                REAL,
            gpu_temp_avg                REAL,
            gpu_temp_max                REAL,
            ssd_temp_avg                REAL,
            ssd_temp_max                REAL,
            fan_rpm_avg                 REAL,
            power_avg                   REAL,
            power_max                   REAL,
            energy_wh                   REAL,
            high_temperature_seconds    REAL NOT NULL DEFAULT 0,
            protected_seconds           REAL NOT NULL DEFAULT 0,
            released_seconds            REAL NOT NULL DEFAULT 0,
            unprotected_seconds         REAL NOT NULL DEFAULT 0,
            ssh_session_max             INTEGER NOT NULL DEFAULT 0
        );

        -- How far each aggregation level has been completed. Source rows are only ever deleted up to
        -- the watermark of the level built from them, so a crash between aggregating and deleting
        -- cannot lose a bucket.
        CREATE TABLE aggregate_watermarks (
            name                    TEXT PRIMARY KEY,
            completed_through_utc   TEXT NOT NULL
        );

        CREATE TABLE power_events (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc     TEXT NOT NULL,
            event_type          TEXT NOT NULL,
            from_state          TEXT,
            to_state            TEXT,
            reason_code         TEXT,
            reason_text         TEXT,
            automatic           INTEGER NOT NULL,
            requested_by        TEXT,
            result              TEXT,
            error_code          TEXT,

            -- This version holds the machine awake and never puts it to sleep, so it can never be the
            -- cause of a sleep or hibernate transition and never performs an automatic power action.
            -- Enforced here so the claim cannot be broken by a later code change without a migration
            -- that says so out loud.
            CHECK (from_state IS NULL OR from_state NOT IN ('Sleeping', 'Hibernating')),
            CHECK (to_state IS NULL OR to_state NOT IN ('Sleeping', 'Hibernating')),
            CHECK (automatic = 0)
        );

        CREATE INDEX ix_power_events_occurred_at ON power_events (occurred_at_utc);

        -- Keep-awake protection being established or released. Kept apart from power_events so that
        -- holding the machine awake is never confused with a power transition.
        CREATE TABLE inhibit_events (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc     TEXT NOT NULL,
            change              TEXT NOT NULL,
            protection_state    TEXT NOT NULL,
            inhibitor_kinds     TEXT,
            reason              TEXT,
            revision            INTEGER NOT NULL,

            CHECK (protection_state IN ('Released', 'Protected', 'Unprotected'))
        );

        CREATE INDEX ix_inhibit_events_occurred_at ON inhibit_events (occurred_at_utc);

        CREATE TABLE wake_events (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            scheduled_for_utc   TEXT,
            occurred_at_utc     TEXT,
            wake_source         TEXT,
            result              TEXT,
            diagnostics         TEXT
        );

        CREATE TABLE alerts (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            raised_at_utc       TEXT NOT NULL,
            category            TEXT NOT NULL,
            severity            TEXT NOT NULL,
            message             TEXT NOT NULL,
            detail              TEXT,
            cleared_at_utc      TEXT
        );

        CREATE TABLE audit_events (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc     TEXT NOT NULL,
            action              TEXT NOT NULL,
            caller_sid          TEXT,
            caller_name         TEXT,
            elevated            INTEGER NOT NULL,
            detail              TEXT,
            result              TEXT
        );

        CREATE INDEX ix_audit_events_occurred_at ON audit_events (occurred_at_utc);

        -- Which external records have already been acted on. Written in the same transaction as the
        -- effects they caused, so replaying a log cannot create a second lease for one login.
        CREATE TABLE processed_event (
            channel             TEXT NOT NULL,
            record_id           TEXT NOT NULL,
            processed_at_utc    TEXT NOT NULL,
            PRIMARY KEY (channel, record_id)
        );

        -- How far each external log has been read. Advanced in the same transaction as the effects,
        -- because advancing it first and then crashing would silently drop a login event and lose the
        -- lease it should have created.
        CREATE TABLE event_bookmarks (
            channel         TEXT PRIMARY KEY,
            bookmark        TEXT NOT NULL,
            updated_at_utc  TEXT NOT NULL
        );

        -- Commands already carried out, so a client that retries after a timeout does not get a second
        -- lease. The payload hash is stored so the same request identifier carrying different content
        -- is refused rather than answered from the first result.
        CREATE TABLE idempotent_commands (
            caller_sid          TEXT NOT NULL,
            request_id          TEXT NOT NULL,
            payload_hash        TEXT NOT NULL,
            result_json         TEXT,
            completed_at_utc    TEXT NOT NULL,
            PRIMARY KEY (caller_sid, request_id)
        );
        """;
}

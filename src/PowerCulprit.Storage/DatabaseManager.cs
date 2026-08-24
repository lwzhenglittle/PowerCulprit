using System.Data;
using Microsoft.Data.Sqlite;
using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Storage;

/// <summary>
/// Result of compacting raw rows into aggregate history.
/// </summary>
public sealed record DatabaseCompactionResult(
    int ProcessRowsCompacted,
    int GpuRowsCompacted,
    int HardwareRowsCompacted,
    DateTime? CompactedThroughUtc);

/// <summary>
/// Manages the SQLite database for PowerCulprit: initialization, schema,
/// batch writes, time-window queries, and historical data cleanup.
/// </summary>
public class DatabaseManager : IDisposable
{
    // One-shot provider registration. We use Microsoft.Data.Sqlite.Core rather
    // than the bundled Microsoft.Data.Sqlite so we can pin the native library
    // (SourceGear.sqlite3, currently SQLite 3.50.x) and stay clear of
    // CVE-2025-6965 in lib.e_sqlite3 2.1.11. The Core package doesn't auto-init
    // SQLitePCL, so we do it here once, before any SqliteConnection is opened.
    private static readonly object _providerLock = new();
    private static bool _providerInitialized;

    private static void EnsureProviderInitialized()
    {
        if (_providerInitialized) return;
        lock (_providerLock)
        {
            if (_providerInitialized) return;
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            _providerInitialized = true;
        }
    }

    private readonly string _connectionString;
    private bool _initialized;
    private const int BusyTimeoutMilliseconds = 5000;
    private const int CurrentSchemaVersion = 7;
    private const string WmiActivityLastRecordIdMetadataKey = "wmi_activity_last_event_record_id";
    public static readonly TimeSpan DefaultRawRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultAggregationWindow = TimeSpan.FromMinutes(5);

    // Persistent write connection, lazily opened on first write and reused for
    // every subsequent write. MonitoringService.RunSingleCycleAsync fans out up
    // to six Insert*Async calls via Task.WhenAll every 2 s; without sharing, each
    // call paid for a new SqliteConnection (handle setup, pool lookup, WAL lock).
    // SqliteConnection is not thread-safe, so _writeLock serialises access. Reads
    // still use ephemeral pooled connections so they don't contend with writes.
    private SqliteConnection? _writeConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, long> _timestampIdCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _serviceGroupIdCache = new(StringComparer.Ordinal);
    private readonly Dictionary<ProcessIdentityKey, long> _processIdentityIdCache = new();

    /// <summary>
    /// Creates a DatabaseManager with the default or custom database path.
    /// </summary>
    /// <param name="dbPath">
    /// Full path to the SQLite database file.
    /// Defaults to %LocalAppData%\PowerCulprit\powerculprit.db.
    /// </param>
    public DatabaseManager(string? dbPath = null)
    {
        EnsureProviderInitialized();

        dbPath ??= GetDefaultDatabasePath();
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    /// <summary>
    /// Returns the default database path: %LocalAppData%\PowerCulprit\powerculprit.db
    /// </summary>
    public static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "PowerCulprit", "powerculprit.db");
    }

    /// <summary>
    /// Returns the default log directory path: %LocalAppData%\PowerCulprit\logs
    /// </summary>
    public static string GetDefaultLogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "PowerCulprit", "logs");
    }

    // ──────────────────────────────────────────────
    //  Initialization
    // ──────────────────────────────────────────────

    /// <summary>
    /// Initializes the database: creates tables and indexes if they don't exist.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        // Use an ephemeral connection here — initialization runs once at startup,
        // before the write loop starts, so there's no value in holding it open.
        await using var connection = await OpenConnectionAsync();
        await EnableWalAsync(connection);
        await DropObsoleteIndexesAsync(connection);

        var schemaVersion = await GetUserVersionAsync(connection);
        if (schemaVersion < 2)
        {
            await RebuildForSchemaV2Async(connection);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await CreateSystemPowerSamplesTableAsync(connection, transaction);
        // v7: make is_ac_online nullable so "AC status unknown" can be stored
        // as NULL instead of being fabricated to a default. Idempotent — a no-op
        // for databases where the column is already nullable (fresh v7 tables).
        await MigrateSystemPowerAcColumnToNullableAsync(connection, transaction);
        await CreateProcessSamplesTableAsync(connection, transaction);
        await CreateGpuProcessSamplesTableAsync(connection, transaction);
        await CreateHardwareSensorSamplesTableAsync(connection, transaction);
        await CreateAnalysisReportsTableAsync(connection, transaction);
        await CreateSourceStatusTableAsync(connection, transaction);
        await CreateWmiActivitySamplesTableAsync(connection, transaction);
        await CreateBatteryCycleTablesAsync(connection, transaction);
        await CreateMetadataTableAsync(connection, transaction);
        await CreateAggregateTablesAsync(connection, transaction);
        await CreatePowerStateEventsTableAsync(connection, transaction);

        await transaction.CommitAsync();
        await SetUserVersionAsync(connection, CurrentSchemaVersion);
        _initialized = true;
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection)
    {
        await using var cmd = new SqliteCommand("PRAGMA user_version;", connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task DropObsoleteIndexesAsync(SqliteConnection connection)
    {
        const string sql = """
            DROP INDEX IF EXISTS idx_process_facts_timestamp_pid;
            DROP INDEX IF EXISTS idx_process_facts_timestamp_identity;
            DROP INDEX IF EXISTS idx_gpu_process_pid;
            DROP INDEX IF EXISTS idx_gpu_process_ts_pid;
            DROP INDEX IF EXISTS idx_hw_sensor_source;
            DROP INDEX IF EXISTS idx_hw_sensor_ts_metric;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, int version)
    {
        await using var cmd = new SqliteCommand($"PRAGMA user_version={version};", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RebuildForSchemaV2Async(SqliteConnection connection)
    {
        await using (var cmd = new SqliteCommand(
            """
            DROP INDEX IF EXISTS idx_process_samples_ts;
            DROP INDEX IF EXISTS idx_process_samples_pid;
            DROP INDEX IF EXISTS idx_process_samples_ts_pid;
            DROP INDEX IF EXISTS idx_process_samples_ts_name;
            DROP TABLE IF EXISTS process_samples;
            DROP TABLE IF EXISTS process_sample_facts;
            DROP TABLE IF EXISTS process_identities;
            DROP TABLE IF EXISTS service_group_members;
            DROP TABLE IF EXISTS service_groups;
            DROP TABLE IF EXISTS sample_timestamps;
            DROP TABLE IF EXISTS battery_cycles;
            DROP TABLE IF EXISTS battery_display_cycles;
            DROP TABLE IF EXISTS system_power_samples;
            DROP TABLE IF EXISTS gpu_process_samples;
            DROP TABLE IF EXISTS hardware_sensor_samples;
            DROP TABLE IF EXISTS analysis_reports;
            DROP TABLE IF EXISTS source_status;
            DROP TABLE IF EXISTS wmi_activity_samples;
            DROP TABLE IF EXISTS session_start_markers;
            DROP TABLE IF EXISTS power_state_events;
            DROP TABLE IF EXISTS process_analysis_aggregates;
            DROP TABLE IF EXISTS gpu_analysis_aggregates;
            DROP TABLE IF EXISTS hardware_sensor_aggregates;
            DROP TABLE IF EXISTS metadata;
            """,
            connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        _timestampIdCache.Clear();
        _serviceGroupIdCache.Clear();
        _processIdentityIdCache.Clear();

        await VacuumCoreAsync(connection);
    }

    // ──────────────────────────────────────────────
    //  Persistent write connection
    // ──────────────────────────────────────────────

    /// <summary>
    /// Returns the persistent write connection, opening it on first use.
    /// MUST be called under <see cref="_writeLock"/>. SqliteConnection is not
    /// thread-safe; the lock serialises every write path.
    /// </summary>
    private async Task<SqliteConnection> GetOrOpenWriteConnectionAsync()
    {
        if (_writeConnection is { State: ConnectionState.Open })
            return _writeConnection;

        // Replace any broken/closed connection.
        if (_writeConnection is not null)
        {
            try { await _writeConnection.DisposeAsync(); } catch { /* best effort */ }
            _writeConnection = null;
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyConnectionPragmasAsync(connection);
        await RegisterSqliteFunctionsAsync(connection);
        _writeConnection = connection;
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyConnectionPragmasAsync(connection);
        await RegisterSqliteFunctionsAsync(connection);
        return connection;
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection)
    {
        await using var cmd = new SqliteCommand(
            $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};",
            connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task RegisterSqliteFunctionsAsync(SqliteConnection connection)
    {
        connection.CreateFunction("ToIsoUtc", (string value) =>
            DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal)
                .ToUniversalTime()
                .ToString("O"));
        await Task.CompletedTask;
    }

    private static async Task EnableWalAsync(SqliteConnection connection)
    {
        await using var cmd = new SqliteCommand("PRAGMA journal_mode=WAL;", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    // ──────────────────────────────────────────────
    //  Schema: Table creation (private)
    // ──────────────────────────────────────────────

    private static async Task CreateSystemPowerSamplesTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS system_power_samples (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc   TEXT    NOT NULL,
                is_ac_online    INTEGER,
                battery_percent REAL,
                charge_rate_milliwatts REAL,
                remaining_capacity_mwh   REAL,
                full_charge_capacity_mwh  REAL,
                estimated_discharge_watts REAL,
                power_mode      TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_system_power_ts
                ON system_power_samples(timestamp_utc);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateProcessSamplesTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS sample_timestamps (
                timestamp_id  INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS service_groups (
                service_group_id INTEGER PRIMARY KEY AUTOINCREMENT,
                fingerprint      TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS service_group_members (
                service_group_id INTEGER NOT NULL,
                service_name     TEXT    NOT NULL,
                PRIMARY KEY (service_group_id, service_name)
            );

            CREATE TABLE IF NOT EXISTS process_identities (
                process_identity_id INTEGER PRIMARY KEY AUTOINCREMENT,
                process_name        TEXT    NOT NULL,
                executable_path     TEXT,
                command_line        TEXT,
                parent_pid          INTEGER,
                service_group_id    INTEGER
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_process_identities_key
                ON process_identities(
                    process_name,
                    COALESCE(executable_path, ''),
                    COALESCE(command_line, ''),
                    COALESCE(parent_pid, -1),
                    COALESCE(service_group_id, 0));

            CREATE TABLE IF NOT EXISTS process_sample_facts (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_id    INTEGER NOT NULL,
                process_identity_id INTEGER NOT NULL,
                pid             INTEGER NOT NULL,
                cpu_percent     REAL,
                working_set_mb  REAL,
                private_memory_mb REAL,
                thread_count    INTEGER,
                handle_count    INTEGER,
                disk_read_bytes_per_second  REAL,
                disk_write_bytes_per_second REAL,
                network_receive_bytes_per_second REAL,
                network_send_bytes_per_second    REAL,
                is_foreground_process INTEGER NOT NULL,
                process_start_count INTEGER,
                process_stop_count INTEGER,
                process_short_lived_count INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_process_facts_timestamp
                ON process_sample_facts(timestamp_id);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string type)
    {
        if (await ColumnExistsAsync(connection, transaction, table, column))
            return;

        await using var alter = new SqliteCommand(
            $"ALTER TABLE {table} ADD COLUMN {column} {type};",
            connection, transaction);
        await alter.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        string table, string column)
    {
        await using var cmd = new SqliteCommand(
            $"PRAGMA table_info({table});", connection, transaction);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// v7 migration: relax <c>is_ac_online</c> from NOT NULL to nullable so the
    /// "AC status unknown" state can be persisted as NULL. SQLite cannot drop a
    /// NOT NULL constraint in place, so the column is relaxed via the standard
    /// table-rebuild (create-copy-drop-rename). Idempotent: if the column is
    /// already nullable (fresh v7 DB, or already migrated) it is a no-op.
    /// </summary>
    private static async Task MigrateSystemPowerAcColumnToNullableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        // PRAGMA table_info: cid, name, type, notnull(0/1), dflt_value, pk
        var notnull = -1;
        await using (var info = new SqliteCommand(
            "PRAGMA table_info(system_power_samples);", connection, transaction))
        await using (var reader = await info.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "is_ac_online", StringComparison.OrdinalIgnoreCase))
                {
                    notnull = reader.GetInt32(3);
                    break;
                }
            }
        }

        // notnull == 0 (or column missing) → already nullable, nothing to do.
        if (notnull != 1)
            return;

        const string rebuild = """
            CREATE TABLE _system_power_samples_v7 (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc   TEXT    NOT NULL,
                is_ac_online    INTEGER,
                battery_percent REAL,
                charge_rate_milliwatts REAL,
                remaining_capacity_mwh   REAL,
                full_charge_capacity_mwh  REAL,
                estimated_discharge_watts REAL,
                power_mode      TEXT
            );
            INSERT INTO _system_power_samples_v7 (id, timestamp_utc, is_ac_online, battery_percent,
                charge_rate_milliwatts, remaining_capacity_mwh, full_charge_capacity_mwh,
                estimated_discharge_watts, power_mode)
            SELECT id, timestamp_utc, is_ac_online, battery_percent,
                charge_rate_milliwatts, remaining_capacity_mwh, full_charge_capacity_mwh,
                estimated_discharge_watts, power_mode
            FROM system_power_samples;
            DROP TABLE system_power_samples;
            ALTER TABLE _system_power_samples_v7 RENAME TO system_power_samples;
            CREATE INDEX IF NOT EXISTS idx_system_power_ts
                ON system_power_samples(timestamp_utc);
            """;

        await using var cmd = new SqliteCommand(rebuild, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateGpuProcessSamplesTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS gpu_process_samples (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc   TEXT    NOT NULL,
                pid             INTEGER,
                process_name    TEXT,
                engine_name     TEXT    NOT NULL,
                engine_type     TEXT    NOT NULL,
                utilization_percent REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_gpu_process_ts
                ON gpu_process_samples(timestamp_utc);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateHardwareSensorSamplesTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS hardware_sensor_samples (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc   TEXT    NOT NULL,
                source          TEXT    NOT NULL,
                device_name     TEXT    NOT NULL,
                sensor_name     TEXT    NOT NULL,
                metric_name     TEXT    NOT NULL,
                value           REAL    NOT NULL,
                unit            TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_hw_sensor_ts
                ON hardware_sensor_samples(timestamp_utc);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateAnalysisReportsTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS analysis_reports (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc       TEXT    NOT NULL,
                window_start_utc    TEXT    NOT NULL,
                window_end_utc      TEXT    NOT NULL,
                process_name        TEXT    NOT NULL,
                pid                 INTEGER,
                score               REAL    NOT NULL,
                rank                INTEGER NOT NULL,
                avg_cpu_percent     REAL,
                max_cpu_percent     REAL,
                avg_gpu_percent     REAL,
                max_gpu_percent     REAL,
                disk_mb             REAL,
                network_mb          REAL,
                background_active_seconds REAL,
                power_correlation   REAL,
                cpu_power_correlation REAL,
                gpu_activity_correlation REAL,
                reason              TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_analysis_reports_ts
                ON analysis_reports(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_analysis_reports_rank
                ON analysis_reports(rank);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateSourceStatusTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS source_status (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc   TEXT    NOT NULL,
                source_name     TEXT    NOT NULL,
                is_available    INTEGER NOT NULL,
                status          TEXT    NOT NULL,
                details         TEXT,
                requires_admin  INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_source_status_ts
                ON source_status(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_source_status_name
                ON source_status(source_name);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateMetadataTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS metadata (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateAggregateTablesAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS process_analysis_aggregates (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                window_start_utc TEXT NOT NULL,
                window_end_utc   TEXT NOT NULL,
                process_name     TEXT NOT NULL,
                service_name     TEXT,
                sample_count     INTEGER NOT NULL,
                cpu_sum          REAL,
                cpu_count        INTEGER NOT NULL DEFAULT 0,
                avg_cpu_percent  REAL,
                max_cpu_percent  REAL,
                working_set_sum  REAL,
                working_set_count INTEGER NOT NULL DEFAULT 0,
                avg_working_set_mb REAL,
                total_disk_bytes_per_second REAL NOT NULL DEFAULT 0,
                total_network_bytes_per_second REAL NOT NULL DEFAULT 0,
                foreground_sample_count INTEGER NOT NULL DEFAULT 0,
                background_sample_count INTEGER NOT NULL DEFAULT 0,
                process_start_count INTEGER,
                process_stop_count INTEGER,
                short_lived_process_count INTEGER,
                activity_score REAL NOT NULL DEFAULT 0,
                is_active_at_window_end INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_process_agg_window_name_service
                ON process_analysis_aggregates(window_start_utc, window_end_utc, process_name, service_name);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_process_agg_window_name_null_service
                ON process_analysis_aggregates(window_start_utc, window_end_utc, process_name)
                WHERE service_name IS NULL;
            CREATE INDEX IF NOT EXISTS idx_process_agg_window
                ON process_analysis_aggregates(window_start_utc, window_end_utc);

            CREATE TABLE IF NOT EXISTS gpu_analysis_aggregates (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                window_start_utc TEXT NOT NULL,
                window_end_utc   TEXT NOT NULL,
                process_name     TEXT NOT NULL,
                sample_count     INTEGER NOT NULL,
                utilization_sum  REAL NOT NULL DEFAULT 0,
                avg_utilization_percent REAL NOT NULL DEFAULT 0,
                max_utilization_percent REAL NOT NULL DEFAULT 0,
                video_activity_percent REAL NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_gpu_agg_window_name
                ON gpu_analysis_aggregates(window_start_utc, window_end_utc, process_name);
            CREATE INDEX IF NOT EXISTS idx_gpu_agg_window
                ON gpu_analysis_aggregates(window_start_utc, window_end_utc);

            CREATE TABLE IF NOT EXISTS hardware_sensor_aggregates (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                window_start_utc TEXT NOT NULL,
                window_end_utc   TEXT NOT NULL,
                source          TEXT NOT NULL,
                device_name     TEXT NOT NULL,
                sensor_name     TEXT NOT NULL,
                metric_name     TEXT NOT NULL,
                unit            TEXT NOT NULL,
                sample_count    INTEGER NOT NULL,
                avg_value       REAL NOT NULL,
                min_value       REAL NOT NULL,
                max_value       REAL NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_hw_agg_window_sensor
                ON hardware_sensor_aggregates(window_start_utc, window_end_utc, source, device_name, sensor_name, metric_name, unit);
            CREATE INDEX IF NOT EXISTS idx_hw_agg_window
                ON hardware_sensor_aggregates(window_start_utc, window_end_utc);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task CreateWmiActivitySamplesTableAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS wmi_activity_samples (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc       TEXT    NOT NULL,
                event_record_id     INTEGER,
                client_process_id   INTEGER NOT NULL,
                event_id            INTEGER NOT NULL,
                user                TEXT,
                operation           TEXT,
                namespace_name      TEXT,
                query_text          TEXT,
                result_code         TEXT,
                possible_cause      TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_wmi_activity_ts
                ON wmi_activity_samples(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_wmi_activity_pid
                ON wmi_activity_samples(client_process_id);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
        await AddColumnIfMissingAsync(connection, transaction, "wmi_activity_samples", "event_record_id", "INTEGER");

        await using var indexCmd = new SqliteCommand(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_wmi_activity_event_record
                ON wmi_activity_samples(event_record_id)
                WHERE event_record_id IS NOT NULL;
            """,
            connection,
            transaction);
        await indexCmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateBatteryCycleTablesAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS battery_display_cycles (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                start_utc               TEXT    NOT NULL,
                end_utc                 TEXT,
                last_sample_utc         TEXT    NOT NULL,
                start_battery_percent   REAL,
                end_battery_percent     REAL,
                discharge_percent       REAL,
                discharge_wh            REAL,
                raw_cycle_count         INTEGER NOT NULL,
                started_at_full_charge  INTEGER NOT NULL,
                is_open                 INTEGER NOT NULL,
                confidence              TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS battery_cycles (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                display_cycle_id        INTEGER NOT NULL,
                start_utc               TEXT    NOT NULL,
                end_utc                 TEXT,
                last_sample_utc         TEXT    NOT NULL,
                start_battery_percent   REAL,
                end_battery_percent     REAL,
                discharge_percent       REAL,
                start_remaining_mwh     REAL,
                end_remaining_mwh       REAL,
                discharge_wh            REAL,
                sample_count            INTEGER NOT NULL,
                started_at_full_charge  INTEGER NOT NULL,
                started_at_session_boundary INTEGER NOT NULL DEFAULT 0,
                is_open                 INTEGER NOT NULL,
                confidence              TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_battery_cycles_display_start
                ON battery_cycles(display_cycle_id, start_utc);
            CREATE INDEX IF NOT EXISTS idx_battery_cycles_start_end
                ON battery_cycles(start_utc, end_utc);
            CREATE INDEX IF NOT EXISTS idx_battery_display_cycles_start_end
                ON battery_display_cycles(start_utc, end_utc);
            CREATE TABLE IF NOT EXISTS session_start_markers (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_session_start_markers_ts
                ON session_start_markers(timestamp_utc);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "battery_cycles",
            "started_at_session_boundary",
            "INTEGER NOT NULL DEFAULT 0");
    }

    private static async Task CreatePowerStateEventsTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS power_state_events (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT    NOT NULL,
                kind          TEXT    NOT NULL,
                source        TEXT    NOT NULL,
                details       TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_power_state_events_ts
                ON power_state_events(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_power_state_events_kind
                ON power_state_events(kind);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts a batch of SystemPowerSample rows in a single transaction.
    /// If the batch is empty, returns immediately.
    /// </summary>
    public async Task InsertSystemPowerSamplesAsync(IReadOnlyList<SystemPowerSample> samples)
    {
        if (samples.Count == 0) return;

        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            const string sql = """
                INSERT INTO system_power_samples
                    (timestamp_utc, is_ac_online, battery_percent, charge_rate_milliwatts,
                     remaining_capacity_mwh, full_charge_capacity_mwh,
                     estimated_discharge_watts, power_mode)
                VALUES
                    ($ts, $ac, $bp, $cr, $rc, $fc, $ed, $pm);
                """;

            await using var cmd = new SqliteCommand(sql, connection, transaction);
            var tsParam = cmd.Parameters.Add("$ts", SqliteType.Text);
            var acParam = cmd.Parameters.Add("$ac", SqliteType.Integer);
            var bpParam = cmd.Parameters.Add("$bp", SqliteType.Real);
            var crParam = cmd.Parameters.Add("$cr", SqliteType.Real);
            var rcParam = cmd.Parameters.Add("$rc", SqliteType.Real);
            var fcParam = cmd.Parameters.Add("$fc", SqliteType.Real);
            var edParam = cmd.Parameters.Add("$ed", SqliteType.Real);
            var pmParam = cmd.Parameters.Add("$pm", SqliteType.Text);

            foreach (var sample in samples)
            {
                tsParam.Value = SerializeTimestamp(sample.TimestampUtc);
                acParam.Value = sample.IsAcOnline switch { true => 1L, false => 0L, _ => DBNull.Value };
                bpParam.Value = (object?)sample.BatteryPercent ?? DBNull.Value;
                crParam.Value = (object?)sample.ChargeRateMilliwatts ?? DBNull.Value;
                rcParam.Value = (object?)sample.RemainingCapacityMWh ?? DBNull.Value;
                fcParam.Value = (object?)sample.FullChargeCapacityMWh ?? DBNull.Value;
                edParam.Value = (object?)sample.EstimatedDischargeWatts ?? DBNull.Value;
                pmParam.Value = (object?)sample.PowerMode ?? DBNull.Value;

                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Inserts a batch of ProcessSample rows in a single transaction.
    /// </summary>
    public async Task InsertProcessSamplesAsync(IReadOnlyList<ProcessSample> samples)
    {
        if (samples.Count == 0) return;

        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            await InsertProcessSamplesInTransactionAsync(connection, transaction, samples);

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Inserts a batch of GpuProcessSample rows in a single transaction.
    /// </summary>
    public async Task InsertGpuProcessSamplesAsync(IReadOnlyList<GpuProcessSample> samples)
    {
        if (samples.Count == 0) return;

        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            await InsertGpuProcessSamplesInTransactionAsync(connection, transaction, samples);

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Inserts a batch of HardwareSensorSample rows in a single transaction.
    /// </summary>
    public async Task InsertHardwareSensorSamplesAsync(IReadOnlyList<HardwareSensorSample> samples)
    {
        if (samples.Count == 0) return;

        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            await InsertHardwareSensorSamplesInTransactionAsync(connection, transaction, samples);

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertWmiActivitySamplesAsync(IReadOnlyList<WmiActivitySample> samples)
    {
        if (samples.Count == 0) return;

        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            await InsertWmiActivitySamplesInTransactionAsync(connection, transaction, samples);

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task InsertWmiActivitySamplesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<WmiActivitySample> samples)
    {
        const string sql = """
            INSERT OR IGNORE INTO wmi_activity_samples
                (timestamp_utc, event_record_id, client_process_id, event_id, user, operation,
                 namespace_name, query_text, result_code, possible_cause)
            VALUES
                ($ts, $rid, $pid, $eid, $user, $op, $ns, $query, $result, $cause);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        var p_ts = cmd.Parameters.Add("$ts", SqliteType.Text);
        var p_rid = cmd.Parameters.Add("$rid", SqliteType.Integer);
        var p_pid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var p_eid = cmd.Parameters.Add("$eid", SqliteType.Integer);
        var p_user = cmd.Parameters.Add("$user", SqliteType.Text);
        var p_op = cmd.Parameters.Add("$op", SqliteType.Text);
        var p_ns = cmd.Parameters.Add("$ns", SqliteType.Text);
        var p_query = cmd.Parameters.Add("$query", SqliteType.Text);
        var p_result = cmd.Parameters.Add("$result", SqliteType.Text);
        var p_cause = cmd.Parameters.Add("$cause", SqliteType.Text);

        foreach (var sample in samples)
        {
            p_ts.Value = SerializeTimestamp(sample.TimestampUtc);
            p_rid.Value = (object?)sample.EventRecordId ?? DBNull.Value;
            p_pid.Value = sample.ClientProcessId;
            p_eid.Value = sample.EventId;
            p_user.Value = (object?)sample.User ?? DBNull.Value;
            p_op.Value = (object?)sample.Operation ?? DBNull.Value;
            p_ns.Value = (object?)sample.NamespaceName ?? DBNull.Value;
            p_query.Value = (object?)sample.QueryText ?? DBNull.Value;
            p_result.Value = (object?)sample.ResultCode ?? DBNull.Value;
            p_cause.Value = (object?)sample.PossibleCause ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        var maxRecordId = samples
            .Where(s => s.EventRecordId.HasValue)
            .Select(s => s.EventRecordId!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (maxRecordId > 0)
        {
            await SetMetadataAsync(
                connection,
                transaction,
                WmiActivityLastRecordIdMetadataKey,
                maxRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public async Task<long?> GetLatestWmiActivityEventRecordIdAsync()
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var metadataRecordId = await GetMetadataLongAsync(connection, transaction, WmiActivityLastRecordIdMetadataKey);

        const string sql = """
            SELECT MAX(event_record_id)
            FROM wmi_activity_samples
            WHERE event_record_id IS NOT NULL;
            """;
        await using var cmd = new SqliteCommand(sql, connection, transaction);
        var result = await cmd.ExecuteScalarAsync();
        long? sampleRecordId = result is null or DBNull
            ? null
            : Convert.ToInt64(result);
        await transaction.CommitAsync();

        if (metadataRecordId.HasValue && sampleRecordId.HasValue)
            return Math.Max(metadataRecordId.Value, sampleRecordId.Value);
        return metadataRecordId ?? sampleRecordId;
    }

    /// <summary>
    /// Inserts a single SourceStatus record.
    /// Only the latest status per source is typically needed, but all are retained for history.
    /// </summary>
    public async Task InsertSourceStatusAsync(SourceStatus status)
    {
        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();

            const string sql = """
                INSERT INTO source_status
                    (timestamp_utc, source_name, is_available, status, details, requires_admin)
                VALUES
                    ($ts, $sn, $ia, $st, $d, $ra);
                """;

            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("$ts", SerializeTimestamp(status.TimestampUtc));
            cmd.Parameters.AddWithValue("$sn", status.SourceName);
            cmd.Parameters.AddWithValue("$ia", status.IsAvailable ? 1L : 0L);
            cmd.Parameters.AddWithValue("$st", status.Status);
            cmd.Parameters.AddWithValue("$d",  (object?)status.Details ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ra", status.RequiresAdmin.HasValue
                ? (status.RequiresAdmin.Value ? 1L : 0L)
                : DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Inserts all samples from one monitoring cycle using one write lock,
    /// one connection, and one SQLite transaction.
    /// </summary>
    public async Task InsertMonitoringCycleAsync(
        SystemPowerSample? powerSample,
        IReadOnlyList<ProcessSample> processSamples,
        IReadOnlyList<GpuProcessSample> gpuSamples,
        IReadOnlyList<HardwareSensorSample> hardwareSamples,
        IReadOnlyList<SourceStatus> sourceStatuses,
        IReadOnlyList<WmiActivitySample>? wmiActivitySamples = null)
    {
        wmiActivitySamples ??= Array.Empty<WmiActivitySample>();

        if (powerSample is null &&
            processSamples.Count == 0 &&
            gpuSamples.Count == 0 &&
            hardwareSamples.Count == 0 &&
            sourceStatuses.Count == 0 &&
            wmiActivitySamples.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            if (powerSample is not null)
                await InsertPowerSampleInTransactionAsync(connection, transaction, powerSample);

            if (processSamples.Count > 0)
                await InsertProcessSamplesInTransactionAsync(connection, transaction, processSamples);

            if (gpuSamples.Count > 0)
                await InsertGpuProcessSamplesInTransactionAsync(connection, transaction, gpuSamples);

            if (hardwareSamples.Count > 0)
                await InsertHardwareSensorSamplesInTransactionAsync(connection, transaction, hardwareSamples);

            if (sourceStatuses.Count > 0)
                await InsertSourceStatusesInTransactionAsync(connection, transaction, sourceStatuses);

            if (wmiActivitySamples.Count > 0)
                await InsertWmiActivitySamplesInTransactionAsync(connection, transaction, wmiActivitySamples);

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task InsertPowerSampleInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SystemPowerSample sample)
    {
        const string sql = """
            INSERT INTO system_power_samples
                (timestamp_utc, is_ac_online, battery_percent, charge_rate_milliwatts,
                 remaining_capacity_mwh, full_charge_capacity_mwh,
                 estimated_discharge_watts, power_mode)
            VALUES
                ($ts, $ac, $bp, $cr, $rc, $fc, $ed, $pm);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("$ts", SerializeTimestamp(sample.TimestampUtc));
        cmd.Parameters.AddWithValue("$ac", sample.IsAcOnline switch { true => 1L, false => 0L, _ => DBNull.Value });
        cmd.Parameters.AddWithValue("$bp", (object?)sample.BatteryPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cr", (object?)sample.ChargeRateMilliwatts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rc", (object?)sample.RemainingCapacityMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fc", (object?)sample.FullChargeCapacityMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ed", (object?)sample.EstimatedDischargeWatts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pm", (object?)sample.PowerMode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertProcessSamplesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ProcessSample> samples)
    {
        const string sql = """
            INSERT INTO process_sample_facts
                (timestamp_id, process_identity_id, pid, cpu_percent,
                 working_set_mb, private_memory_mb, thread_count, handle_count,
                 disk_read_bytes_per_second, disk_write_bytes_per_second,
                 network_receive_bytes_per_second, network_send_bytes_per_second,
                 is_foreground_process, process_start_count, process_stop_count,
                 process_short_lived_count)
            VALUES
                ($tsid, $piid, $pid, $cpu, $ws, $pmem, $tc, $hc,
                 $dr, $dw, $nr, $ns, $fg, $psc, $pstc, $slc);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        var p_tsid = cmd.Parameters.Add("$tsid", SqliteType.Integer);
        var p_piid = cmd.Parameters.Add("$piid", SqliteType.Integer);
        var p_pid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var p_cpu = cmd.Parameters.Add("$cpu", SqliteType.Real);
        var p_ws = cmd.Parameters.Add("$ws", SqliteType.Real);
        var p_pmem = cmd.Parameters.Add("$pmem", SqliteType.Real);
        var p_tc = cmd.Parameters.Add("$tc", SqliteType.Integer);
        var p_hc = cmd.Parameters.Add("$hc", SqliteType.Integer);
        var p_dr = cmd.Parameters.Add("$dr", SqliteType.Real);
        var p_dw = cmd.Parameters.Add("$dw", SqliteType.Real);
        var p_nr = cmd.Parameters.Add("$nr", SqliteType.Real);
        var p_ns = cmd.Parameters.Add("$ns", SqliteType.Real);
        var p_fg = cmd.Parameters.Add("$fg", SqliteType.Integer);
        var p_psc = cmd.Parameters.Add("$psc", SqliteType.Integer);
        var p_pstc = cmd.Parameters.Add("$pstc", SqliteType.Integer);
        var p_slc = cmd.Parameters.Add("$slc", SqliteType.Integer);

        foreach (var sample in samples)
        {
            var timestampId = await GetOrCreateTimestampIdAsync(connection, transaction, SerializeTimestamp(sample.TimestampUtc));
            var serviceGroupId = await GetOrCreateServiceGroupIdAsync(connection, transaction, sample.ServiceName);
            var identityId = await GetOrCreateProcessIdentityIdAsync(connection, transaction, sample, serviceGroupId);

            p_tsid.Value = timestampId;
            p_piid.Value = identityId;
            p_pid.Value = sample.Pid;
            p_cpu.Value = (object?)sample.CpuPercent ?? DBNull.Value;
            p_ws.Value  = (object?)sample.WorkingSetMb ?? DBNull.Value;
            p_pmem.Value = (object?)sample.PrivateMemoryMb ?? DBNull.Value;
            p_tc.Value  = (object?)sample.ThreadCount ?? DBNull.Value;
            p_hc.Value  = (object?)sample.HandleCount ?? DBNull.Value;
            p_dr.Value  = (object?)sample.DiskReadBytesPerSecond ?? DBNull.Value;
            p_dw.Value  = (object?)sample.DiskWriteBytesPerSecond ?? DBNull.Value;
            p_nr.Value  = (object?)sample.NetworkReceiveBytesPerSecond ?? DBNull.Value;
            p_ns.Value  = (object?)sample.NetworkSendBytesPerSecond ?? DBNull.Value;
            p_fg.Value  = sample.IsForegroundProcess ? 1L : 0L;
            p_psc.Value = (object?)sample.ProcessStartCount ?? DBNull.Value;
            p_pstc.Value = (object?)sample.ProcessStopCount ?? DBNull.Value;
            p_slc.Value = (object?)sample.ShortLivedProcessCount ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> GetOrCreateTimestampIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string timestampUtc)
    {
        if (_timestampIdCache.TryGetValue(timestampUtc, out var cached))
            return cached;

        await using (var insert = new SqliteCommand(
            "INSERT OR IGNORE INTO sample_timestamps(timestamp_utc) VALUES ($ts);",
            connection, transaction))
        {
            insert.Parameters.AddWithValue("$ts", timestampUtc);
            await insert.ExecuteNonQueryAsync();
        }

        await using var select = new SqliteCommand(
            "SELECT timestamp_id FROM sample_timestamps WHERE timestamp_utc = $ts;",
            connection, transaction);
        select.Parameters.AddWithValue("$ts", timestampUtc);
        var id = Convert.ToInt64(await select.ExecuteScalarAsync());
        _timestampIdCache[timestampUtc] = id;
        return id;
    }

    private async Task<long?> GetOrCreateServiceGroupIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? serviceName)
    {
        var services = SplitServiceNames(serviceName);
        if (services.Count == 0)
            return null;

        var fingerprint = string.Join('\u001f', services);
        if (_serviceGroupIdCache.TryGetValue(fingerprint, out var cached))
            return cached;

        await using (var insert = new SqliteCommand(
            "INSERT OR IGNORE INTO service_groups(fingerprint) VALUES ($fp);",
            connection, transaction))
        {
            insert.Parameters.AddWithValue("$fp", fingerprint);
            await insert.ExecuteNonQueryAsync();
        }

        await using var select = new SqliteCommand(
            "SELECT service_group_id FROM service_groups WHERE fingerprint = $fp;",
            connection, transaction);
        select.Parameters.AddWithValue("$fp", fingerprint);
        var id = Convert.ToInt64(await select.ExecuteScalarAsync());

        await using var memberInsert = new SqliteCommand(
            """
            INSERT OR IGNORE INTO service_group_members(service_group_id, service_name)
            VALUES ($id, $name);
            """,
            connection, transaction);
        var idParam = memberInsert.Parameters.Add("$id", SqliteType.Integer);
        var nameParam = memberInsert.Parameters.Add("$name", SqliteType.Text);
        foreach (var service in services)
        {
            idParam.Value = id;
            nameParam.Value = service;
            await memberInsert.ExecuteNonQueryAsync();
        }

        _serviceGroupIdCache[fingerprint] = id;
        return id;
    }

    private async Task<long> GetOrCreateProcessIdentityIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessSample sample,
        long? serviceGroupId)
    {
        var key = new ProcessIdentityKey(
            sample.ProcessName,
            sample.ExecutablePath,
            sample.CommandLine,
            sample.ParentPid,
            serviceGroupId);

        if (_processIdentityIdCache.TryGetValue(key, out var cached))
            return cached;

        await using (var insert = new SqliteCommand(
            """
            INSERT OR IGNORE INTO process_identities
                (process_name, executable_path, command_line, parent_pid, service_group_id)
            VALUES ($pn, $ep, $cl, $pp, $sg);
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("$pn", sample.ProcessName);
            insert.Parameters.AddWithValue("$ep", (object?)sample.ExecutablePath ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cl", (object?)sample.CommandLine ?? DBNull.Value);
            insert.Parameters.AddWithValue("$pp", (object?)sample.ParentPid ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sg", (object?)serviceGroupId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }

        await using var select = new SqliteCommand(
            """
            SELECT process_identity_id
            FROM process_identities
            WHERE process_name = $pn
              AND COALESCE(executable_path, '') = COALESCE($ep, '')
              AND COALESCE(command_line, '') = COALESCE($cl, '')
              AND COALESCE(parent_pid, -1) = COALESCE($pp, -1)
              AND COALESCE(service_group_id, 0) = COALESCE($sg, 0);
            """,
            connection, transaction);
        select.Parameters.AddWithValue("$pn", sample.ProcessName);
        select.Parameters.AddWithValue("$ep", (object?)sample.ExecutablePath ?? DBNull.Value);
        select.Parameters.AddWithValue("$cl", (object?)sample.CommandLine ?? DBNull.Value);
        select.Parameters.AddWithValue("$pp", (object?)sample.ParentPid ?? DBNull.Value);
        select.Parameters.AddWithValue("$sg", (object?)serviceGroupId ?? DBNull.Value);
        var id = Convert.ToInt64(await select.ExecuteScalarAsync());
        _processIdentityIdCache[key] = id;
        return id;
    }

    private static async Task InsertGpuProcessSamplesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<GpuProcessSample> samples)
    {
        const string sql = """
            INSERT INTO gpu_process_samples
                (timestamp_utc, pid, process_name, engine_name, engine_type, utilization_percent)
            VALUES
                ($ts, $pid, $pn, $en, $et, $up);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        var p_ts = cmd.Parameters.Add("$ts", SqliteType.Text);
        var p_pid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var p_pn = cmd.Parameters.Add("$pn", SqliteType.Text);
        var p_en = cmd.Parameters.Add("$en", SqliteType.Text);
        var p_et = cmd.Parameters.Add("$et", SqliteType.Text);
        var p_up = cmd.Parameters.Add("$up", SqliteType.Real);

        foreach (var sample in samples)
        {
            // SQLite binds double.NaN/Infinity as NULL, which would violate the
            // NOT NULL constraint on utilization_percent and roll back the whole
            // monitoring-cycle transaction. Skip the bad row instead of losing
            // every sample in the cycle.
            if (!double.IsFinite(sample.UtilizationPercent))
                continue;

            p_ts.Value = SerializeTimestamp(sample.TimestampUtc);
            p_pid.Value = (object?)sample.Pid ?? DBNull.Value;
            p_pn.Value = (object?)sample.ProcessName ?? DBNull.Value;
            p_en.Value = sample.EngineName;
            p_et.Value = sample.EngineType.ToString();
            p_up.Value = sample.UtilizationPercent;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertHardwareSensorSamplesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<HardwareSensorSample> samples)
    {
        const string sql = """
            INSERT INTO hardware_sensor_samples
                (timestamp_utc, source, device_name, sensor_name, metric_name, value, unit)
            VALUES
                ($ts, $src, $dn, $sn, $mn, $val, $u);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        var p_ts  = cmd.Parameters.Add("$ts",  SqliteType.Text);
        var p_src = cmd.Parameters.Add("$src", SqliteType.Text);
        var p_dn  = cmd.Parameters.Add("$dn",  SqliteType.Text);
        var p_sn  = cmd.Parameters.Add("$sn",  SqliteType.Text);
        var p_mn  = cmd.Parameters.Add("$mn",  SqliteType.Text);
        var p_val = cmd.Parameters.Add("$val", SqliteType.Real);
        var p_u   = cmd.Parameters.Add("$u",   SqliteType.Text);

        foreach (var sample in samples)
        {
            // Same NaN guard as the GPU insert above: a non-finite value would
            // bind as NULL, violate NOT NULL on value, and roll back the whole
            // monitoring-cycle transaction.
            if (!double.IsFinite(sample.Value))
                continue;

            p_ts.Value  = SerializeTimestamp(sample.TimestampUtc);
            p_src.Value = sample.Source;
            p_dn.Value  = sample.DeviceName;
            p_sn.Value  = sample.SensorName;
            p_mn.Value  = sample.MetricName;
            p_val.Value = sample.Value;
            p_u.Value   = sample.Unit;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertSourceStatusesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SourceStatus> statuses)
    {
        const string sql = """
            INSERT INTO source_status
                (timestamp_utc, source_name, is_available, status, details, requires_admin)
            VALUES
                ($ts, $sn, $ia, $st, $d, $ra);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        var p_ts = cmd.Parameters.Add("$ts", SqliteType.Text);
        var p_sn = cmd.Parameters.Add("$sn", SqliteType.Text);
        var p_ia = cmd.Parameters.Add("$ia", SqliteType.Integer);
        var p_st = cmd.Parameters.Add("$st", SqliteType.Text);
        var p_d = cmd.Parameters.Add("$d", SqliteType.Text);
        var p_ra = cmd.Parameters.Add("$ra", SqliteType.Integer);

        foreach (var status in statuses)
        {
            p_ts.Value = SerializeTimestamp(status.TimestampUtc);
            p_sn.Value = status.SourceName;
            p_ia.Value = status.IsAvailable ? 1L : 0L;
            p_st.Value = status.Status;
            p_d.Value = (object?)status.Details ?? DBNull.Value;
            p_ra.Value = status.RequiresAdmin.HasValue
                ? (status.RequiresAdmin.Value ? 1L : 0L)
                : DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Inserts a batch of CulpritReportItem rows in a single transaction.
    /// </summary>
    public async Task InsertAnalysisReportAsync(
        DateTime windowStartUtc, DateTime windowEndUtc, IReadOnlyList<CulpritReportItem> items)
    {
        if (items.Count == 0) return;

        await EnsureInitializedAsync();

        var now = DateTime.UtcNow;
        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            const string sql = """
                INSERT INTO analysis_reports
                    (timestamp_utc, window_start_utc, window_end_utc, process_name, pid,
                     score, rank, avg_cpu_percent, max_cpu_percent, avg_gpu_percent,
                     max_gpu_percent, disk_mb, network_mb, background_active_seconds,
                     power_correlation, cpu_power_correlation, gpu_activity_correlation, reason)
                VALUES
                    ($ts, $ws, $we, $pn, $pid, $sc, $rk, $acpu, $mcpu, $agpu,
                     $mgpu, $dmb, $nmb, $bas, $pc, $cpc, $gac, $r);
                """;

            await using var cmd = new SqliteCommand(sql, connection, transaction);
            var p_ts  = cmd.Parameters.Add("$ts",   SqliteType.Text);
            var p_ws  = cmd.Parameters.Add("$ws",   SqliteType.Text);
            var p_we  = cmd.Parameters.Add("$we",   SqliteType.Text);
            var p_pn  = cmd.Parameters.Add("$pn",   SqliteType.Text);
            var p_pid = cmd.Parameters.Add("$pid",  SqliteType.Integer);
            var p_sc  = cmd.Parameters.Add("$sc",   SqliteType.Real);
            var p_rk  = cmd.Parameters.Add("$rk",   SqliteType.Integer);
            var p_acpu = cmd.Parameters.Add("$acpu", SqliteType.Real);
            var p_mcpu = cmd.Parameters.Add("$mcpu", SqliteType.Real);
            var p_agpu = cmd.Parameters.Add("$agpu", SqliteType.Real);
            var p_mgpu = cmd.Parameters.Add("$mgpu", SqliteType.Real);
            var p_dmb  = cmd.Parameters.Add("$dmb",  SqliteType.Real);
            var p_nmb  = cmd.Parameters.Add("$nmb",  SqliteType.Real);
            var p_bas  = cmd.Parameters.Add("$bas",  SqliteType.Real);
            var p_pc   = cmd.Parameters.Add("$pc",   SqliteType.Real);
            var p_cpc  = cmd.Parameters.Add("$cpc",  SqliteType.Real);
            var p_gac  = cmd.Parameters.Add("$gac",  SqliteType.Real);
            var p_r    = cmd.Parameters.Add("$r",    SqliteType.Text);

            foreach (var item in items)
            {
                p_ts.Value  = SerializeTimestamp(now);
                p_ws.Value  = SerializeTimestamp(windowStartUtc);
                p_we.Value  = SerializeTimestamp(windowEndUtc);
                p_pn.Value  = item.ProcessName;
                p_pid.Value = (object?)item.Pid ?? DBNull.Value;
                p_sc.Value  = item.Score;
                p_rk.Value  = item.Rank;
                p_acpu.Value = (object?)item.AvgCpuPercent ?? DBNull.Value;
                p_mcpu.Value = (object?)item.MaxCpuPercent ?? DBNull.Value;
                p_agpu.Value = (object?)item.AvgGpuPercent ?? DBNull.Value;
                p_mgpu.Value = (object?)item.MaxGpuPercent ?? DBNull.Value;
                p_dmb.Value  = (object?)item.DiskMb ?? DBNull.Value;
                p_nmb.Value  = (object?)item.NetworkMb ?? DBNull.Value;
                p_bas.Value  = (object?)item.BackgroundActiveSeconds ?? DBNull.Value;
                p_pc.Value   = (object?)item.PowerCorrelation ?? DBNull.Value;
                p_cpc.Value  = (object?)item.CpuPowerCorrelation ?? DBNull.Value;
                p_gac.Value  = (object?)item.GpuActivityCorrelation ?? DBNull.Value;
                p_r.Value    = item.Reason;

                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ──────────────────────────────────────────────
    //  Queries: recent window retrieval
    // ──────────────────────────────────────────────

    /// <summary>
    /// Returns system power samples within the given UTC time window.
    /// </summary>
    public async Task<IReadOnlyList<SystemPowerSample>> GetSystemPowerSamplesAsync(
        DateTime fromUtc, DateTime toUtc)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            SELECT timestamp_utc, is_ac_online, battery_percent, charge_rate_milliwatts,
                   remaining_capacity_mwh, full_charge_capacity_mwh,
                   estimated_discharge_watts, power_mode
            FROM system_power_samples
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY timestamp_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to",   SerializeTimestamp(toUtc));

        var results = new List<SystemPowerSample>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SystemPowerSample
            {
                TimestampUtc            = DeserializeTimestamp(reader.GetString(0)),
                IsAcOnline              = reader.IsDBNull(1) ? null : (bool?)(reader.GetInt64(1) != 0),
                BatteryPercent          = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                ChargeRateMilliwatts    = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                RemainingCapacityMWh    = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                FullChargeCapacityMWh   = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                EstimatedDischargeWatts = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                PowerMode               = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns process samples within the given UTC time window.
    /// </summary>
    public async Task<IReadOnlyList<ProcessSample>> GetProcessSamplesAsync(
        DateTime fromUtc, DateTime toUtc)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            WITH service_names AS (
                SELECT service_group_id, group_concat(service_name, ', ') AS service_name
                FROM (
                    SELECT service_group_id, service_name
                    FROM service_group_members
                    ORDER BY service_group_id, service_name
                )
                GROUP BY service_group_id
            )
            SELECT t.timestamp_utc, f.pid, i.process_name, i.executable_path, i.command_line,
                   i.parent_pid, f.cpu_percent, f.working_set_mb, f.private_memory_mb,
                   f.thread_count, f.handle_count, f.disk_read_bytes_per_second,
                   f.disk_write_bytes_per_second, f.network_receive_bytes_per_second,
                   f.network_send_bytes_per_second, f.is_foreground_process, sn.service_name,
                   f.process_start_count, f.process_stop_count, f.process_short_lived_count
            FROM process_sample_facts f
            JOIN sample_timestamps t ON t.timestamp_id = f.timestamp_id
            JOIN process_identities i ON i.process_identity_id = f.process_identity_id
            LEFT JOIN service_names sn ON sn.service_group_id = i.service_group_id
            WHERE t.timestamp_utc >= $from AND t.timestamp_utc <= $to
            ORDER BY t.timestamp_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to",   SerializeTimestamp(toUtc));

        var results = new List<ProcessSample>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ProcessSample
            {
                TimestampUtc                 = DeserializeTimestamp(reader.GetString(0)),
                Pid                          = reader.GetInt32(1),
                ProcessName                  = reader.GetString(2),
                ExecutablePath               = reader.IsDBNull(3) ? null : reader.GetString(3),
                CommandLine                  = reader.IsDBNull(4) ? null : reader.GetString(4),
                ParentPid                    = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                CpuPercent                   = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                WorkingSetMb                 = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                PrivateMemoryMb              = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                ThreadCount                  = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                HandleCount                  = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                DiskReadBytesPerSecond       = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                DiskWriteBytesPerSecond      = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                NetworkReceiveBytesPerSecond = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                NetworkSendBytesPerSecond    = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                IsForegroundProcess          = reader.GetInt64(15) != 0,
                ServiceName                  = reader.IsDBNull(16) ? null : reader.GetString(16),
                ProcessStartCount            = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                ProcessStopCount             = reader.IsDBNull(18) ? null : reader.GetInt32(18),
                ShortLivedProcessCount       = reader.IsDBNull(19) ? null : reader.GetInt32(19)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns the process fields needed by the historical analyzer.
    /// </summary>
    public async Task<IReadOnlyList<ProcessSample>> GetProcessSamplesForAnalysisAsync(
        DateTime fromUtc, DateTime toUtc)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            WITH service_names AS (
                SELECT service_group_id, group_concat(service_name, ', ') AS service_name
                FROM (
                    SELECT service_group_id, service_name
                    FROM service_group_members
                    ORDER BY service_group_id, service_name
                )
                GROUP BY service_group_id
            )
            SELECT t.timestamp_utc, f.pid, i.process_name, f.cpu_percent, f.working_set_mb,
                   f.disk_read_bytes_per_second, f.disk_write_bytes_per_second,
                   f.network_receive_bytes_per_second, f.network_send_bytes_per_second,
                   f.is_foreground_process, sn.service_name, f.process_start_count,
                   f.process_stop_count, f.process_short_lived_count
            FROM process_sample_facts f
            JOIN sample_timestamps t ON t.timestamp_id = f.timestamp_id
            JOIN process_identities i ON i.process_identity_id = f.process_identity_id
            LEFT JOIN service_names sn ON sn.service_group_id = i.service_group_id
            WHERE t.timestamp_utc >= $from AND t.timestamp_utc <= $to
            ORDER BY t.timestamp_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to", SerializeTimestamp(toUtc));

        var results = new List<ProcessSample>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ProcessSample
            {
                TimestampUtc = DeserializeTimestamp(reader.GetString(0)),
                Pid = reader.GetInt32(1),
                ProcessName = reader.GetString(2),
                CpuPercent = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                WorkingSetMb = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                DiskReadBytesPerSecond = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                DiskWriteBytesPerSecond = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                NetworkReceiveBytesPerSecond = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                NetworkSendBytesPerSecond = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                IsForegroundProcess = reader.GetInt64(9) != 0,
                ServiceName = reader.IsDBNull(10) ? null : reader.GetString(10),
                ProcessStartCount = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                ProcessStopCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                ShortLivedProcessCount = reader.IsDBNull(13) ? null : reader.GetInt32(13)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns SQL-side process aggregates for the historical analyzer.
    /// </summary>
    public async Task<IReadOnlyList<ProcessAnalysisAggregate>> GetProcessAggregatesForAnalysisAsync(
        IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> intervals)
    {
        await EnsureInitializedAsync();
        if (intervals.Count == 0)
            return Array.Empty<ProcessAnalysisAggregate>();

        await using var connection = await OpenConnectionAsync();
        var (rawWhereClause, rawParameters) = BuildIntervalWhereClause(intervals, "t.timestamp_utc");
        var (aggregateWhereClause, aggregateParameters) = BuildIntervalWhereClause(intervals, "window_end_utc", "agg");

        var sql = $"""
            WITH service_names AS (
                SELECT service_group_id, group_concat(service_name, ', ') AS service_name
                FROM (
                    SELECT service_group_id, service_name
                    FROM service_group_members
                    ORDER BY service_group_id, service_name
                )
                GROUP BY service_group_id
            ),
            raw_agg AS (
                SELECT i.process_name,
                       sn.service_name,
                       SUM(f.cpu_percent) AS sum_cpu,
                       COUNT(f.cpu_percent) AS count_cpu,
                       MAX(f.cpu_percent) AS max_cpu,
                       SUM(f.working_set_mb) AS sum_ws,
                       COUNT(f.working_set_mb) AS count_ws,
                       SUM(COALESCE(f.disk_read_bytes_per_second, 0) + COALESCE(f.disk_write_bytes_per_second, 0)) AS total_disk,
                       SUM(COALESCE(f.network_receive_bytes_per_second, 0) + COALESCE(f.network_send_bytes_per_second, 0)) AS total_network,
                       COUNT(f.process_start_count) AS start_count,
                       SUM(COALESCE(f.process_start_count, 0)) AS starts,
                       COUNT(f.process_stop_count) AS stop_count,
                       SUM(COALESCE(f.process_stop_count, 0)) AS stops,
                       COUNT(f.process_short_lived_count) AS short_lived_count,
                       SUM(COALESCE(f.process_short_lived_count, 0)) AS short_lived,
                       SUM(CASE WHEN f.is_foreground_process != 0 THEN 1 ELSE 0 END) AS fg_samples,
                       SUM(CASE WHEN f.is_foreground_process = 0 THEN 1 ELSE 0 END) AS bg_samples,
                       COUNT(*) AS sample_count
                FROM process_sample_facts f
                JOIN sample_timestamps t ON t.timestamp_id = f.timestamp_id
                JOIN process_identities i ON i.process_identity_id = f.process_identity_id
                LEFT JOIN service_names sn ON sn.service_group_id = i.service_group_id
                WHERE f.pid NOT IN (0, 4)
                  AND ({rawWhereClause})
                GROUP BY i.process_name, sn.service_name
            ),
            stored_agg AS (
                SELECT process_name,
                       service_name,
                       SUM(cpu_sum) AS sum_cpu,
                       SUM(cpu_count) AS count_cpu,
                       MAX(max_cpu_percent) AS max_cpu,
                       SUM(working_set_sum) AS sum_ws,
                       SUM(working_set_count) AS count_ws,
                       SUM(total_disk_bytes_per_second) AS total_disk,
                       SUM(total_network_bytes_per_second) AS total_network,
                       COUNT(process_start_count) AS start_count,
                       SUM(COALESCE(process_start_count, 0)) AS starts,
                       COUNT(process_stop_count) AS stop_count,
                       SUM(COALESCE(process_stop_count, 0)) AS stops,
                       COUNT(short_lived_process_count) AS short_lived_count,
                       SUM(COALESCE(short_lived_process_count, 0)) AS short_lived,
                       SUM(foreground_sample_count) AS fg_samples,
                       SUM(background_sample_count) AS bg_samples,
                       SUM(sample_count) AS sample_count
                FROM process_analysis_aggregates
                WHERE {aggregateWhereClause}
                GROUP BY process_name, service_name
            ),
            combined AS (
                SELECT * FROM raw_agg
                UNION ALL
                SELECT * FROM stored_agg
            )
            SELECT process_name,
                   service_name,
                   CASE WHEN SUM(count_cpu) = 0 THEN NULL ELSE SUM(sum_cpu) / SUM(count_cpu) END AS avg_cpu,
                   MAX(max_cpu) AS max_cpu,
                   CASE WHEN SUM(count_ws) = 0 THEN NULL ELSE SUM(sum_ws) / SUM(count_ws) END AS avg_ws,
                   SUM(total_disk) AS total_disk,
                   SUM(total_network) AS total_network,
                   CASE WHEN SUM(start_count) = 0 THEN NULL ELSE SUM(starts) END AS starts,
                   CASE WHEN SUM(stop_count) = 0 THEN NULL ELSE SUM(stops) END AS stops,
                   CASE WHEN SUM(short_lived_count) = 0 THEN NULL ELSE SUM(short_lived) END AS short_lived,
                   SUM(fg_samples) AS fg_samples,
                   SUM(bg_samples) AS bg_samples,
                   SUM(sample_count) AS sample_count
            FROM combined
            GROUP BY process_name, service_name
            ORDER BY avg_cpu DESC;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        foreach (var (name, value) in rawParameters)
            cmd.Parameters.AddWithValue(name, value);
        foreach (var (name, value) in aggregateParameters)
            cmd.Parameters.AddWithValue(name, value);

        var results = new List<ProcessAnalysisAggregate>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ProcessAnalysisAggregate
            {
                ProcessName = reader.GetString(0),
                ServiceName = reader.IsDBNull(1) ? null : reader.GetString(1),
                AvgCpuPercent = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                MaxCpuPercent = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                AvgWorkingSetMb = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                TotalDiskBytesPerSecond = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                TotalNetworkBytesPerSecond = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                ProcessStartCount = reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetInt64(7)),
                ProcessStopCount = reader.IsDBNull(8) ? null : Convert.ToInt32(reader.GetInt64(8)),
                ShortLivedProcessCount = reader.IsDBNull(9) ? null : Convert.ToInt32(reader.GetInt64(9)),
                ForegroundSampleCount = Convert.ToInt32(reader.GetInt64(10)),
                BackgroundSampleCount = Convert.ToInt32(reader.GetInt64(11)),
                SampleCount = Convert.ToInt32(reader.GetInt64(12))
            });
        }

        return results;
    }

    /// <summary>
    /// Returns GPU process samples within the given UTC time window.
    /// </summary>
    public async Task<IReadOnlyList<GpuProcessSample>> GetGpuProcessSamplesAsync(
        DateTime fromUtc, DateTime toUtc)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            SELECT timestamp_utc, pid, process_name, engine_name, engine_type, utilization_percent
            FROM gpu_process_samples
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY timestamp_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to", SerializeTimestamp(toUtc));

        var results = new List<GpuProcessSample>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new GpuProcessSample
            {
                TimestampUtc = DeserializeTimestamp(reader.GetString(0)),
                Pid = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                ProcessName = reader.IsDBNull(2) ? null : reader.GetString(2),
                EngineName = reader.GetString(3),
                EngineType = Enum.TryParse<GpuEngineType>(reader.GetString(4), out var engineType)
                    ? engineType
                    : GpuEngineType.Other,
                UtilizationPercent = reader.GetDouble(5)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<WmiCallerAggregate>> GetWmiCallerAggregatesAsync(
        DateTime fromUtc, DateTime toUtc, int limit = 100)
    {
        await EnsureInitializedAsync();
        if (limit <= 0)
            return Array.Empty<WmiCallerAggregate>();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            WITH wmi AS (
                SELECT id, timestamp_utc, client_process_id, operation, result_code, possible_cause
                FROM wmi_activity_samples
                WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ),
            process_names AS (
                SELECT f.pid,
                       i.process_name,
                       i.executable_path,
                       COUNT(*) AS sample_count,
                       MAX(t.timestamp_utc) AS latest_seen
                FROM process_sample_facts f
                JOIN sample_timestamps t ON t.timestamp_id = f.timestamp_id
                JOIN process_identities i ON i.process_identity_id = f.process_identity_id
                WHERE t.timestamp_utc >= $from AND t.timestamp_utc <= $to
                GROUP BY f.pid, i.process_name, i.executable_path
            ),
            ranked_names AS (
                SELECT pid,
                       process_name,
                       executable_path,
                       ROW_NUMBER() OVER (
                           PARTITION BY pid
                           ORDER BY sample_count DESC, latest_seen DESC
                       ) AS rn
                FROM process_names
            ),
            last_events AS (
                SELECT w.*
                FROM wmi w
                WHERE w.id = (
                    SELECT w2.id
                    FROM wmi w2
                    WHERE w2.client_process_id = w.client_process_id
                    ORDER BY w2.timestamp_utc DESC, w2.id DESC
                    LIMIT 1
                )
            )
            SELECT w.client_process_id,
                   rn.process_name,
                   rn.executable_path,
                   COUNT(*) AS call_count,
                   SUM(CASE WHEN w.result_code IS NOT NULL AND w.result_code NOT IN ('0x0', '0') THEN 1 ELSE 0 END) AS failure_count,
                   COUNT(DISTINCT COALESCE(w.operation, '')) AS unique_operation_count,
                   MIN(w.timestamp_utc) AS first_seen_utc,
                   MAX(w.timestamp_utc) AS last_seen_utc,
                   le.operation AS last_operation,
                   le.result_code AS last_result_code,
                   le.possible_cause AS last_possible_cause
            FROM wmi w
            LEFT JOIN ranked_names rn ON rn.pid = w.client_process_id AND rn.rn = 1
            LEFT JOIN last_events le ON le.client_process_id = w.client_process_id
            GROUP BY w.client_process_id, rn.process_name, rn.executable_path,
                     le.operation, le.result_code, le.possible_cause
            ORDER BY call_count DESC, failure_count DESC, last_seen_utc DESC
            LIMIT $limit;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to", SerializeTimestamp(toUtc));
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<WmiCallerAggregate>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new WmiCallerAggregate
            {
                ClientProcessId = reader.GetInt32(0),
                ProcessName = reader.IsDBNull(1) ? null : reader.GetString(1),
                ExecutablePath = reader.IsDBNull(2) ? null : reader.GetString(2),
                CallCount = Convert.ToInt32(reader.GetInt64(3)),
                FailureCount = Convert.ToInt32(reader.GetInt64(4)),
                UniqueOperationCount = Convert.ToInt32(reader.GetInt64(5)),
                FirstSeenUtc = DeserializeTimestamp(reader.GetString(6)),
                LastSeenUtc = DeserializeTimestamp(reader.GetString(7)),
                LastOperation = reader.IsDBNull(8) ? null : reader.GetString(8),
                LastResultCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                LastPossibleCause = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns SQL-side GPU Engine aggregates for the historical analyzer.
    /// </summary>
    public async Task<IReadOnlyList<GpuProcessAnalysisAggregate>> GetGpuProcessAggregatesAsync(
        IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> intervals)
    {
        await EnsureInitializedAsync();
        if (intervals.Count == 0)
            return Array.Empty<GpuProcessAnalysisAggregate>();

        await using var connection = await OpenConnectionAsync();
        var (rawWhereClause, rawParameters) = BuildIntervalWhereClause(intervals);
        var (aggregateWhereClause, aggregateParameters) = BuildIntervalWhereClause(intervals, "window_end_utc", "agg");

        var sql = $"""
            WITH combined AS (
                SELECT process_name,
                       SUM(utilization_percent) AS util_sum,
                       COUNT(*) AS sample_count,
                       MAX(utilization_percent) AS max_util,
                       SUM(CASE WHEN engine_type IN ('VideoDecode', 'VideoEncode')
                                THEN utilization_percent ELSE 0 END) AS video_activity
                FROM gpu_process_samples
                WHERE process_name IS NOT NULL
                  AND ({rawWhereClause})
                GROUP BY process_name
                UNION ALL
                SELECT process_name,
                       SUM(utilization_sum) AS util_sum,
                       SUM(sample_count) AS sample_count,
                       MAX(max_utilization_percent) AS max_util,
                       SUM(video_activity_percent) AS video_activity
                FROM gpu_analysis_aggregates
                WHERE {aggregateWhereClause}
                GROUP BY process_name
            )
            SELECT process_name,
                   CASE WHEN SUM(sample_count) = 0 THEN 0 ELSE SUM(util_sum) / SUM(sample_count) END AS avg_util,
                   MAX(max_util) AS max_util,
                   -- Per-engine-row average, same convention as avg_util: video_activity
                   -- is stored as a SUM, so divide by the row count to keep the result
                   -- a true percentage that does not grow with the window length.
                   CASE WHEN SUM(sample_count) = 0 THEN 0 ELSE SUM(video_activity) / SUM(sample_count) END AS video_activity
            FROM combined
            GROUP BY process_name;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        foreach (var (name, value) in rawParameters)
            cmd.Parameters.AddWithValue(name, value);
        foreach (var (name, value) in aggregateParameters)
            cmd.Parameters.AddWithValue(name, value);

        var results = new List<GpuProcessAnalysisAggregate>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new GpuProcessAnalysisAggregate
            {
                ProcessName = reader.GetString(0),
                AvgUtilizationPercent = reader.GetDouble(1),
                MaxUtilizationPercent = reader.GetDouble(2),
                VideoActivityPercent = reader.IsDBNull(3) ? 0 : reader.GetDouble(3)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns hardware sensor samples within the given UTC time window.
    /// </summary>
    public async Task<IReadOnlyList<HardwareSensorSample>> GetHardwareSensorSamplesAsync(
        DateTime fromUtc, DateTime toUtc)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            SELECT timestamp_utc, source, device_name, sensor_name, metric_name, value, unit
            FROM hardware_sensor_samples
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            UNION ALL
            SELECT window_start_utc AS timestamp_utc, source, device_name, sensor_name, metric_name, avg_value AS value, unit
            FROM hardware_sensor_aggregates
            WHERE window_end_utc >= $from AND window_start_utc <= $to
            ORDER BY timestamp_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to", SerializeTimestamp(toUtc));

        var results = new List<HardwareSensorSample>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new HardwareSensorSample
            {
                TimestampUtc = DeserializeTimestamp(reader.GetString(0)),
                Source = reader.GetString(1),
                DeviceName = reader.GetString(2),
                SensorName = reader.GetString(3),
                MetricName = reader.GetString(4),
                Value = reader.GetDouble(5),
                Unit = reader.GetString(6)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns the latest SourceStatus for each source name.
    /// </summary>
    public async Task<IReadOnlyList<SourceStatus>> GetLatestSourceStatusesAsync()
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            SELECT s.timestamp_utc, s.source_name, s.is_available, s.status, s.details, s.requires_admin
            FROM source_status s
            WHERE s.id = (
                SELECT s2.id
                FROM source_status s2
                WHERE s2.source_name = s.source_name
                ORDER BY s2.timestamp_utc DESC, s2.id DESC
                LIMIT 1
            )
            ORDER BY s.source_name;
            """;

        await using var cmd = new SqliteCommand(sql, connection);

        var results = new List<SourceStatus>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SourceStatus
            {
                TimestampUtc  = DeserializeTimestamp(reader.GetString(0)),
                SourceName    = reader.GetString(1),
                IsAvailable   = reader.GetInt64(2) != 0,
                Status        = reader.GetString(3),
                Details       = reader.IsDBNull(4) ? null : reader.GetString(4),
                RequiresAdmin = reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0
            });
        }

        return results;
    }

    // ──────────────────────────────────────────────
    //  Maintenance
    // ──────────────────────────────────────────────

    /// <summary>
    /// Removes duplicate source status rows that share the same source name and timestamp,
    /// keeping the most recently inserted row for each exact timestamp.
    /// </summary>
    public async Task<int> DeduplicateSourceStatusTimestampTiesAsync()
    {
        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            const string sql = """
                DELETE FROM source_status
                WHERE id NOT IN (
                    SELECT MAX(id)
                    FROM source_status
                    GROUP BY source_name, timestamp_utc
                );
                """;

            await using var cmd = new SqliteCommand(sql, connection, transaction);
            var deleted = await cmd.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertSessionStartMarkerAsync(DateTime timestampUtc)
    {
        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            const string sql = """
                INSERT INTO session_start_markers (timestamp_utc)
                VALUES ($ts);
                """;

            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("$ts", SerializeTimestamp(timestampUtc));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Inserts a single power state event (suspend/resume).
    /// Designed for direct synchronous calls so suspend events can be
    /// flushed before the system actually sleeps.
    /// </summary>
    public async Task InsertPowerStateEventAsync(PowerStateEvent evt)
    {
        await EnsureInitializedAsync();

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            const string sql = """
                INSERT INTO power_state_events (timestamp_utc, kind, source, details)
                VALUES ($ts, $kind, $src, $details);
                """;

            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("$ts", SerializeTimestamp(evt.TimestampUtc));
            cmd.Parameters.AddWithValue("$kind", evt.Kind.ToString());
            cmd.Parameters.AddWithValue("$src", evt.Source);
            cmd.Parameters.AddWithValue("$details", (object?)evt.Details ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Returns power state events in the given time range, ordered by time.
    /// </summary>
    public async Task<IReadOnlyList<PowerStateEvent>> GetPowerStateEventsAsync(
        DateTime fromUtc,
        DateTime toUtc)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();
        const string sql = """
            SELECT id, timestamp_utc, kind, source, details
            FROM power_state_events
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY timestamp_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to", SerializeTimestamp(toUtc));

        var results = new List<PowerStateEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new PowerStateEvent
            {
                Id = reader.GetInt64(0),
                TimestampUtc = DeserializeTimestamp(reader.GetString(1)),
                Kind = Enum.Parse<PowerStateEventKind>(reader.GetString(2)),
                Source = reader.GetString(3),
                Details = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<DateTime>> GetSessionStartMarkersAsync(DateTime fromUtc, DateTime toUtc)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();
        const string sql = """
            SELECT timestamp_utc
            FROM session_start_markers
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY timestamp_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$from", SerializeTimestamp(fromUtc));
        cmd.Parameters.AddWithValue("$to", SerializeTimestamp(toUtc));

        var results = new List<DateTime>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(DeserializeTimestamp(reader.GetString(0)));

        return results;
    }

    /// <summary>
    /// Rebuilds persisted battery cycle caches from retained system power samples.
    /// </summary>
    public async Task<BatteryCycleBuildResult> RebuildBatteryCyclesAsync(int retentionDays = 7)
    {
        await EnsureInitializedAsync();

        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddDays(-retentionDays);
        var powerSamples = await GetSystemPowerSamplesAsync(fromUtc, toUtc);
        var sessionStarts = await GetSessionStartMarkersAsync(fromUtc, toUtc);
        var powerEvents = await GetPowerStateEventsAsync(fromUtc, toUtc);
        var sleepIntervals = GapDetector.DetectSleepOnly(powerEvents);
        var result = BatteryCycleBuilder.Build(powerSamples, sessionStarts, sleepIntervals);

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            await using (var deleteRaw = new SqliteCommand("DELETE FROM battery_cycles;", connection, transaction))
                await deleteRaw.ExecuteNonQueryAsync();
            await using (var deleteDisplay = new SqliteCommand("DELETE FROM battery_display_cycles;", connection, transaction))
                await deleteDisplay.ExecuteNonQueryAsync();

            var displayIdMap = new Dictionary<long, long>();
            foreach (var displayCycle in result.DisplayCycles)
            {
                var persistedId = await InsertBatteryDisplayCycleInTransactionAsync(
                    connection,
                    transaction,
                    displayCycle);
                displayIdMap[displayCycle.Id] = persistedId;
            }

            foreach (var cycle in result.RawCycles)
            {
                if (!cycle.DisplayCycleId.HasValue ||
                    !displayIdMap.TryGetValue(cycle.DisplayCycleId.Value, out var displayCycleId))
                {
                    continue;
                }

                await InsertBatteryCycleInTransactionAsync(
                    connection,
                    transaction,
                    cycle,
                    displayCycleId);
            }

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }

        return result;
    }

    public async Task<IReadOnlyList<BatteryDisplayCycle>> GetLatestBatteryDisplayCyclesAsync(int count)
    {
        await EnsureInitializedAsync();
        if (count <= 0)
            return Array.Empty<BatteryDisplayCycle>();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            SELECT id, start_utc, end_utc, last_sample_utc,
                   start_battery_percent, end_battery_percent,
                   discharge_percent, discharge_wh, raw_cycle_count,
                   started_at_full_charge, is_open, confidence
            FROM battery_display_cycles
            ORDER BY COALESCE(end_utc, last_sample_utc) DESC, start_utc DESC
            LIMIT $count;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$count", count);

        var results = new List<BatteryDisplayCycle>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadBatteryDisplayCycle(reader));

        return results;
    }

    public async Task<IReadOnlyList<BatteryCycle>> GetBatteryCyclesForDisplayCycleAsync(long displayCycleId)
    {
        await EnsureInitializedAsync();

        await using var connection = await OpenConnectionAsync();

        const string sql = """
            SELECT id, display_cycle_id, start_utc, end_utc, last_sample_utc,
                   start_battery_percent, end_battery_percent, discharge_percent,
                   start_remaining_mwh, end_remaining_mwh, discharge_wh,
                   sample_count, started_at_full_charge, started_at_session_boundary,
                   is_open, confidence
            FROM battery_cycles
            WHERE display_cycle_id = $displayCycleId
            ORDER BY start_utc;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$displayCycleId", displayCycleId);

        var results = new List<BatteryCycle>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadBatteryCycle(reader));

        return results;
    }

    /// <summary>
    /// Compacts raw process/GPU/hardware rows older than the cutoff into fixed-size aggregate windows.
    /// </summary>
    public async Task<DatabaseCompactionResult> CompactRawDataAsync(
        DateTime compactBeforeUtc,
        TimeSpan? windowSize = null)
    {
        await EnsureInitializedAsync();

        var effectiveWindow = windowSize ?? DefaultAggregationWindow;
        if (effectiveWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Compaction window must be positive.");

        var compactThroughUtc = AlignDown(compactBeforeUtc, effectiveWindow);
        if (compactThroughUtc <= DateTime.MinValue.AddDays(1))
            return new DatabaseCompactionResult(0, 0, 0, null);

        var compactThroughStr = SerializeTimestamp(compactThroughUtc);
        var processRows = 0;
        var gpuRows = 0;
        var hardwareRows = 0;

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            var lastCompacted = await GetMetadataTimestampAsync(connection, transaction, "last_compaction_window_end_utc");
            var lowerBound = lastCompacted is null ? null : SerializeTimestamp(lastCompacted.Value);

            processRows = await CompactProcessRowsAsync(connection, transaction, lowerBound, compactThroughStr, effectiveWindow);
            gpuRows = await CompactGpuRowsAsync(connection, transaction, lowerBound, compactThroughStr, effectiveWindow);
            hardwareRows = await CompactHardwareRowsAsync(connection, transaction, lowerBound, compactThroughStr, effectiveWindow);

            await DeleteCompactedRawRowsAsync(connection, transaction, lowerBound, compactThroughStr);
            await CleanupOrphanProcessMetadataAsync(connection, transaction);
            await SetMetadataAsync(connection, transaction, "last_compaction_window_end_utc", compactThroughStr);

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }

        if (processRows > 0 || gpuRows > 0 || hardwareRows > 0)
            await CheckpointWalTruncateAsync();

        return new DatabaseCompactionResult(processRows, gpuRows, hardwareRows, compactThroughUtc);
    }

    private static async Task<DateTime?> GetMetadataTimestampAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key)
    {
        await using var cmd = new SqliteCommand("SELECT value FROM metadata WHERE key = $key;", connection, transaction);
        cmd.Parameters.AddWithValue("$key", key);
        var value = await cmd.ExecuteScalarAsync();
        return value is string text ? DeserializeTimestamp(text) : null;
    }

    private static async Task<long?> GetMetadataLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key)
    {
        await using var cmd = new SqliteCommand("SELECT value FROM metadata WHERE key = $key;", connection, transaction);
        cmd.Parameters.AddWithValue("$key", key);
        var value = await cmd.ExecuteScalarAsync();
        return value is string text &&
               long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static async Task SetMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        await using var cmd = new SqliteCommand(
            """
            INSERT INTO metadata(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """,
            connection, transaction);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CompactProcessRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? lowerBound,
        string compactThroughStr,
        TimeSpan windowSize)
    {
        var lowerClause = lowerBound is null ? string.Empty : "AND t.timestamp_utc > $lower";
        var sql = $"""
            WITH service_names AS (
                SELECT service_group_id, group_concat(service_name, ', ') AS service_name
                FROM (
                    SELECT service_group_id, service_name
                    FROM service_group_members
                    ORDER BY service_group_id, service_name
                )
                GROUP BY service_group_id
            ),
            raw AS (
                SELECT datetime((CAST(strftime('%s', t.timestamp_utc) AS INTEGER) / $windowSeconds) * $windowSeconds, 'unixepoch') AS window_start,
                       datetime(((CAST(strftime('%s', t.timestamp_utc) AS INTEGER) / $windowSeconds) + 1) * $windowSeconds, 'unixepoch') AS window_end,
                       i.process_name,
                       sn.service_name,
                       f.cpu_percent,
                       f.working_set_mb,
                       COALESCE(f.disk_read_bytes_per_second, 0) + COALESCE(f.disk_write_bytes_per_second, 0) AS disk_total,
                       COALESCE(f.network_receive_bytes_per_second, 0) + COALESCE(f.network_send_bytes_per_second, 0) AS network_total,
                       f.is_foreground_process,
                       f.process_start_count,
                       f.process_stop_count,
                       f.process_short_lived_count
                FROM process_sample_facts f
                JOIN sample_timestamps t ON t.timestamp_id = f.timestamp_id
                JOIN process_identities i ON i.process_identity_id = f.process_identity_id
                LEFT JOIN service_names sn ON sn.service_group_id = i.service_group_id
                WHERE t.timestamp_utc <= $through
                  {lowerClause}
                  AND f.pid NOT IN (0, 4)
            ),
            grouped AS (
                SELECT window_start,
                       window_end,
                       process_name,
                       service_name,
                       COUNT(*) AS sample_count,
                       SUM(cpu_percent) AS cpu_sum,
                       COUNT(cpu_percent) AS cpu_count,
                       AVG(cpu_percent) AS avg_cpu,
                       MAX(cpu_percent) AS max_cpu,
                       SUM(working_set_mb) AS ws_sum,
                       COUNT(working_set_mb) AS ws_count,
                       AVG(working_set_mb) AS avg_ws,
                       SUM(disk_total) AS total_disk,
                       SUM(network_total) AS total_network,
                       SUM(CASE WHEN is_foreground_process != 0 THEN 1 ELSE 0 END) AS fg_samples,
                       SUM(CASE WHEN is_foreground_process = 0 THEN 1 ELSE 0 END) AS bg_samples,
                       COUNT(process_start_count) AS start_count,
                       SUM(COALESCE(process_start_count, 0)) AS starts,
                       COUNT(process_stop_count) AS stop_count,
                       SUM(COALESCE(process_stop_count, 0)) AS stops,
                       COUNT(process_short_lived_count) AS short_count,
                       SUM(COALESCE(process_short_lived_count, 0)) AS short_lived
                FROM raw
                GROUP BY window_start, window_end, process_name, service_name
            )
            INSERT OR REPLACE INTO process_analysis_aggregates
                (window_start_utc, window_end_utc, process_name, service_name, sample_count,
                 cpu_sum, cpu_count, avg_cpu_percent, max_cpu_percent,
                 working_set_sum, working_set_count, avg_working_set_mb,
                 total_disk_bytes_per_second, total_network_bytes_per_second,
                 foreground_sample_count, background_sample_count,
                 process_start_count, process_stop_count, short_lived_process_count,
                 activity_score, is_active_at_window_end)
            SELECT ToIsoUtc(window_start), ToIsoUtc(window_end), process_name, service_name, sample_count,
                   cpu_sum, cpu_count, avg_cpu, max_cpu,
                   ws_sum, ws_count, avg_ws,
                   total_disk, total_network,
                   fg_samples, bg_samples,
                   CASE WHEN start_count = 0 THEN NULL ELSE starts END,
                   CASE WHEN stop_count = 0 THEN NULL ELSE stops END,
                   CASE WHEN short_count = 0 THEN NULL ELSE short_lived END,
                   COALESCE(avg_cpu, 0) + (COALESCE(total_disk, 0) + COALESCE(total_network, 0)) / 1048576.0,
                   1
            FROM grouped;
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("$windowSeconds", Convert.ToInt64(windowSize.TotalSeconds));
        cmd.Parameters.AddWithValue("$through", compactThroughStr);
        if (lowerBound is not null)
            cmd.Parameters.AddWithValue("$lower", lowerBound);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CompactGpuRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? lowerBound,
        string compactThroughStr,
        TimeSpan windowSize)
    {
        var lowerClause = lowerBound is null ? string.Empty : "AND timestamp_utc > $lower";
        var sql = $"""
            WITH raw AS (
                SELECT datetime((CAST(strftime('%s', timestamp_utc) AS INTEGER) / $windowSeconds) * $windowSeconds, 'unixepoch') AS window_start,
                       datetime(((CAST(strftime('%s', timestamp_utc) AS INTEGER) / $windowSeconds) + 1) * $windowSeconds, 'unixepoch') AS window_end,
                       process_name,
                       engine_type,
                       utilization_percent
                FROM gpu_process_samples
                WHERE timestamp_utc <= $through
                  {lowerClause}
                  AND process_name IS NOT NULL
                  AND utilization_percent > 0
            ),
            grouped AS (
                SELECT window_start,
                       window_end,
                       process_name,
                       COUNT(*) AS sample_count,
                       SUM(utilization_percent) AS util_sum,
                       AVG(utilization_percent) AS avg_util,
                       MAX(utilization_percent) AS max_util,
                       SUM(CASE WHEN engine_type IN ('VideoDecode', 'VideoEncode') THEN utilization_percent ELSE 0 END) AS video_activity
                FROM raw
                GROUP BY window_start, window_end, process_name
                HAVING max_util > 0
            )
            INSERT OR REPLACE INTO gpu_analysis_aggregates
                (window_start_utc, window_end_utc, process_name, sample_count,
                 utilization_sum, avg_utilization_percent, max_utilization_percent, video_activity_percent)
            SELECT ToIsoUtc(window_start), ToIsoUtc(window_end), process_name, sample_count,
                   util_sum, avg_util, max_util, video_activity
            FROM grouped;
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("$windowSeconds", Convert.ToInt64(windowSize.TotalSeconds));
        cmd.Parameters.AddWithValue("$through", compactThroughStr);
        if (lowerBound is not null)
            cmd.Parameters.AddWithValue("$lower", lowerBound);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CompactHardwareRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? lowerBound,
        string compactThroughStr,
        TimeSpan windowSize)
    {
        var lowerClause = lowerBound is null ? string.Empty : "AND timestamp_utc > $lower";
        var sql = $"""
            WITH raw AS (
                SELECT datetime((CAST(strftime('%s', timestamp_utc) AS INTEGER) / $windowSeconds) * $windowSeconds, 'unixepoch') AS window_start,
                       datetime(((CAST(strftime('%s', timestamp_utc) AS INTEGER) / $windowSeconds) + 1) * $windowSeconds, 'unixepoch') AS window_end,
                       source, device_name, sensor_name, metric_name, unit, value
                FROM hardware_sensor_samples
                WHERE timestamp_utc <= $through
                  {lowerClause}
            ),
            grouped AS (
                SELECT window_start,
                       window_end,
                       source,
                       device_name,
                       sensor_name,
                       metric_name,
                       unit,
                       COUNT(*) AS sample_count,
                       AVG(value) AS avg_value,
                       MIN(value) AS min_value,
                       MAX(value) AS max_value
                FROM raw
                GROUP BY window_start, window_end, source, device_name, sensor_name, metric_name, unit
            )
            INSERT OR REPLACE INTO hardware_sensor_aggregates
                (window_start_utc, window_end_utc, source, device_name, sensor_name, metric_name, unit,
                 sample_count, avg_value, min_value, max_value)
            SELECT ToIsoUtc(window_start), ToIsoUtc(window_end), source, device_name, sensor_name, metric_name, unit,
                   sample_count, avg_value, min_value, max_value
            FROM grouped;
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("$windowSeconds", Convert.ToInt64(windowSize.TotalSeconds));
        cmd.Parameters.AddWithValue("$through", compactThroughStr);
        if (lowerBound is not null)
            cmd.Parameters.AddWithValue("$lower", lowerBound);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DeleteCompactedRawRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? lowerBound,
        string compactThroughStr)
    {
        var lowerClause = lowerBound is null ? string.Empty : "AND timestamp_utc > $lower";

        await using (var deleteProcess = new SqliteCommand(
            $"""
            DELETE FROM process_sample_facts
            WHERE timestamp_id IN (
                SELECT timestamp_id FROM sample_timestamps
                WHERE timestamp_utc <= $through {lowerClause}
            );
            """,
            connection, transaction))
        {
            deleteProcess.Parameters.AddWithValue("$through", compactThroughStr);
            if (lowerBound is not null)
                deleteProcess.Parameters.AddWithValue("$lower", lowerBound);
            await deleteProcess.ExecuteNonQueryAsync();
        }

        foreach (var table in new[] { "gpu_process_samples", "hardware_sensor_samples" })
        {
            await using var cmd = new SqliteCommand(
                $"DELETE FROM {table} WHERE timestamp_utc <= $through {lowerClause};",
                connection, transaction);
            cmd.Parameters.AddWithValue("$through", compactThroughStr);
            if (lowerBound is not null)
                cmd.Parameters.AddWithValue("$lower", lowerBound);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task CheckpointWalTruncateAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var cmd = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", connection);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static DateTime AlignDown(DateTime timestampUtc, TimeSpan windowSize)
    {
        var utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        var ticks = utc.Ticks - utc.Ticks % windowSize.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }


    /// <summary>
    /// Deletes old history using separate retention windows for raw high-volume tables and longer-lived aggregate/history tables.
    /// </summary>
    public async Task<int> CleanupOldDataAsync(int retentionDays = 7, TimeSpan? rawRetention = null)
    {
        await EnsureInitializedAsync();

        var (rawCutoff, historyCutoff) = ResolveRetentionCutoffs(retentionDays, rawRetention);
        var rawCutoffStr = SerializeTimestamp(rawCutoff);
        var historyCutoffStr = SerializeTimestamp(historyCutoff);
        var totalDeleted = 0;

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            var cycleDeletes = new[]
            {
                "DELETE FROM battery_cycles WHERE COALESCE(end_utc, last_sample_utc) < $cutoff;",
                "DELETE FROM battery_display_cycles WHERE COALESCE(end_utc, last_sample_utc) < $cutoff;",
                "DELETE FROM battery_cycles WHERE display_cycle_id NOT IN (SELECT id FROM battery_display_cycles);"
            };

            foreach (var sql in cycleDeletes)
            {
                await using var cmd = new SqliteCommand(sql, connection, transaction);
                if (sql.Contains("$cutoff", StringComparison.Ordinal))
                    cmd.Parameters.AddWithValue("$cutoff", historyCutoffStr);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            foreach (var table in new[] { "gpu_process_samples", "hardware_sensor_samples", "wmi_activity_samples" })
            {
                var sql = $"DELETE FROM {table} WHERE timestamp_utc < $cutoff;";
                await using var cmd = new SqliteCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("$cutoff", rawCutoffStr);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            foreach (var table in new[]
            {
                "system_power_samples",
                "analysis_reports",
                "source_status",
                "session_start_markers",
                "power_state_events"
            })
            {
                var sql = $"DELETE FROM {table} WHERE timestamp_utc < $cutoff;";
                await using var cmd = new SqliteCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("$cutoff", historyCutoffStr);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            foreach (var table in new[]
            {
                "process_analysis_aggregates",
                "gpu_analysis_aggregates",
                "hardware_sensor_aggregates"
            })
            {
                var sql = $"DELETE FROM {table} WHERE window_end_utc < $cutoff;";
                await using var cmd = new SqliteCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("$cutoff", historyCutoffStr);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            totalDeleted += await DeleteOldProcessFactsAsync(connection, transaction, rawCutoffStr);
            await CleanupOrphanProcessMetadataAsync(connection, transaction);

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }

        if (totalDeleted > 0)
            await CheckpointWalTruncateAsync();

        return totalDeleted;
    }

    private static (DateTime RawCutoff, DateTime HistoryCutoff) ResolveRetentionCutoffs(
        int retentionDays,
        TimeSpan? rawRetention)
    {
        var now = DateTime.UtcNow;
        var historyCutoff = now.AddDays(-retentionDays);
        var rawCutoff = rawRetention.HasValue
            ? now.Subtract(rawRetention.Value)
            : historyCutoff;
        return (rawCutoff, historyCutoff);
    }

    private static async Task<int> DeleteOldProcessFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string cutoffStr)
    {
        const string sql = """
            DELETE FROM process_sample_facts
            WHERE timestamp_id IN (
                SELECT timestamp_id
                FROM sample_timestamps
                WHERE timestamp_utc < $cutoff
            );
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("$cutoff", cutoffStr);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task CleanupOrphanProcessMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var cleanupSql = new[]
        {
            """
            DELETE FROM process_identities
            WHERE process_identity_id NOT IN (
                SELECT DISTINCT process_identity_id FROM process_sample_facts
            );
            """,
            """
            DELETE FROM service_group_members
            WHERE service_group_id NOT IN (
                SELECT DISTINCT service_group_id
                FROM process_identities
                WHERE service_group_id IS NOT NULL
            );
            """,
            """
            DELETE FROM service_groups
            WHERE service_group_id NOT IN (
                SELECT DISTINCT service_group_id
                FROM process_identities
                WHERE service_group_id IS NOT NULL
            );
            """,
            """
            DELETE FROM sample_timestamps
            WHERE timestamp_id NOT IN (
                SELECT DISTINCT timestamp_id FROM process_sample_facts
            );
            """
        };

        foreach (var sql in cleanupSql)
        {
            await using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();
        }

        _timestampIdCache.Clear();
        _serviceGroupIdCache.Clear();
        _processIdentityIdCache.Clear();
    }

    /// <summary>
    /// Deletes all collected history from every persisted data table.
    /// Returns the total number of deleted rows.
    /// </summary>
    public async Task<int> ClearHistoricalDataAsync()
    {
        await EnsureInitializedAsync();

        var totalDeleted = 0;

        await _writeLock.WaitAsync();
        try
        {
            var connection = await GetOrOpenWriteConnectionAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            var tables = new[]
            {
                "battery_cycles",
                "battery_display_cycles",
                "system_power_samples",
                "process_sample_facts",
                "process_identities",
                "service_group_members",
                "service_groups",
                "sample_timestamps",
                "gpu_process_samples",
                "hardware_sensor_samples",
                "analysis_reports",
                "source_status",
                "wmi_activity_samples",
                "session_start_markers",
                "power_state_events",
                "metadata",
                "process_analysis_aggregates",
                "gpu_analysis_aggregates",
                "hardware_sensor_aggregates"
            };

            foreach (var table in tables)
            {
                await using var cmd = new SqliteCommand($"DELETE FROM {table};", connection, transaction);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = new SqliteCommand(
                "INSERT INTO session_start_markers (timestamp_utc) VALUES ($ts);",
                connection,
                transaction))
            {
                cmd.Parameters.AddWithValue("$ts", SerializeTimestamp(DateTime.UtcNow));
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            _timestampIdCache.Clear();
            _serviceGroupIdCache.Clear();
            _processIdentityIdCache.Clear();
        }
        finally
        {
            _writeLock.Release();
        }

        return totalDeleted;
    }

    /// <summary>
    /// Performs a VACUUM to reclaim disk space after large deletions.
    /// This can be slow; call it sparingly (e.g., after cleanup).
    /// </summary>
    public async Task VacuumAsync()
    {
        // VACUUM cannot run inside a transaction and rewrites the entire DB file —
        // use an ephemeral connection so we don't keep the write connection busy.
        await using var connection = await OpenConnectionAsync();
        await VacuumCoreAsync(connection);
    }

    private static async Task VacuumCoreAsync(SqliteConnection connection)
    {
        await using var cmd = new SqliteCommand("VACUUM;", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    private static (string Sql, List<(string Name, string Value)> Parameters) BuildIntervalWhereClause(
        IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> intervals,
        string columnName = "timestamp_utc",
        string parameterPrefix = "")
    {
        var clauses = new List<string>(intervals.Count);
        var parameters = new List<(string Name, string Value)>(intervals.Count * 2);

        for (var i = 0; i < intervals.Count; i++)
        {
            var fromName = $"${parameterPrefix}from{i}";
            var toName = $"${parameterPrefix}to{i}";
            clauses.Add($"({columnName} >= {fromName} AND {columnName} <= {toName})");
            parameters.Add((fromName, SerializeTimestamp(intervals[i].FromUtc)));
            parameters.Add((toName, SerializeTimestamp(intervals[i].ToUtc)));
        }

        return (string.Join(" OR ", clauses), parameters);
    }

    private static IReadOnlyList<string> SplitServiceNames(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return Array.Empty<string>();

        return serviceName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record ProcessIdentityKey(
        string ProcessName,
        string? ExecutablePath,
        string? CommandLine,
        int? ParentPid,
        long? ServiceGroupId);

    private static async Task<long> InsertBatteryDisplayCycleInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatteryDisplayCycle cycle)
    {
        const string sql = """
            INSERT INTO battery_display_cycles
                (start_utc, end_utc, last_sample_utc, start_battery_percent,
                 end_battery_percent, discharge_percent, discharge_wh,
                 raw_cycle_count, started_at_full_charge, is_open, confidence)
            VALUES
                ($start, $end, $last, $startPercent, $endPercent,
                 $dischargePercent, $dischargeWh, $rawCount, $fullStart,
                 $isOpen, $confidence);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("$start", SerializeTimestamp(cycle.StartUtc));
        cmd.Parameters.AddWithValue("$end", cycle.EndUtc.HasValue ? SerializeTimestamp(cycle.EndUtc.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$last", SerializeTimestamp(cycle.LastSampleUtc));
        cmd.Parameters.AddWithValue("$startPercent", (object?)cycle.StartBatteryPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$endPercent", (object?)cycle.EndBatteryPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dischargePercent", (object?)cycle.DischargePercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dischargeWh", (object?)cycle.DischargeWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rawCount", cycle.RawCycleCount);
        cmd.Parameters.AddWithValue("$fullStart", cycle.StartedAtFullCharge ? 1L : 0L);
        cmd.Parameters.AddWithValue("$isOpen", cycle.IsOpen ? 1L : 0L);
        cmd.Parameters.AddWithValue("$confidence", cycle.Confidence.ToString());

        await cmd.ExecuteNonQueryAsync();
        await using var idCmd = new SqliteCommand("SELECT last_insert_rowid();", connection, transaction);
        return Convert.ToInt64(await idCmd.ExecuteScalarAsync());
    }

    private static async Task InsertBatteryCycleInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatteryCycle cycle,
        long displayCycleId)
    {
        const string sql = """
            INSERT INTO battery_cycles
                (display_cycle_id, start_utc, end_utc, last_sample_utc,
                 start_battery_percent, end_battery_percent, discharge_percent,
                 start_remaining_mwh, end_remaining_mwh, discharge_wh,
                 sample_count, started_at_full_charge, started_at_session_boundary,
                 is_open, confidence)
            VALUES
                ($displayId, $start, $end, $last, $startPercent, $endPercent,
                 $dischargePercent, $startMwh, $endMwh, $dischargeWh,
                 $sampleCount, $fullStart, $sessionStart, $isOpen, $confidence);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("$displayId", displayCycleId);
        cmd.Parameters.AddWithValue("$start", SerializeTimestamp(cycle.StartUtc));
        cmd.Parameters.AddWithValue("$end", cycle.EndUtc.HasValue ? SerializeTimestamp(cycle.EndUtc.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$last", SerializeTimestamp(cycle.LastSampleUtc));
        cmd.Parameters.AddWithValue("$startPercent", (object?)cycle.StartBatteryPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$endPercent", (object?)cycle.EndBatteryPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dischargePercent", (object?)cycle.DischargePercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startMwh", (object?)cycle.StartRemainingMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$endMwh", (object?)cycle.EndRemainingMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dischargeWh", (object?)cycle.DischargeWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sampleCount", cycle.SampleCount);
        cmd.Parameters.AddWithValue("$fullStart", cycle.StartedAtFullCharge ? 1L : 0L);
        cmd.Parameters.AddWithValue("$sessionStart", cycle.StartedAtSessionBoundary ? 1L : 0L);
        cmd.Parameters.AddWithValue("$isOpen", cycle.IsOpen ? 1L : 0L);
        cmd.Parameters.AddWithValue("$confidence", cycle.Confidence.ToString());

        await cmd.ExecuteNonQueryAsync();
    }

    private static BatteryDisplayCycle ReadBatteryDisplayCycle(SqliteDataReader reader)
    {
        return new BatteryDisplayCycle
        {
            Id = reader.GetInt64(0),
            StartUtc = DeserializeTimestamp(reader.GetString(1)),
            EndUtc = reader.IsDBNull(2) ? null : DeserializeTimestamp(reader.GetString(2)),
            LastSampleUtc = DeserializeTimestamp(reader.GetString(3)),
            StartBatteryPercent = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            EndBatteryPercent = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            DischargePercent = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            DischargeWh = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            RawCycleCount = reader.GetInt32(8),
            StartedAtFullCharge = reader.GetInt64(9) != 0,
            IsOpen = reader.GetInt64(10) != 0,
            Confidence = ParseConfidence(reader.GetString(11))
        };
    }

    private static BatteryCycle ReadBatteryCycle(SqliteDataReader reader)
    {
        return new BatteryCycle
        {
            Id = reader.GetInt64(0),
            DisplayCycleId = reader.GetInt64(1),
            StartUtc = DeserializeTimestamp(reader.GetString(2)),
            EndUtc = reader.IsDBNull(3) ? null : DeserializeTimestamp(reader.GetString(3)),
            LastSampleUtc = DeserializeTimestamp(reader.GetString(4)),
            StartBatteryPercent = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            EndBatteryPercent = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            DischargePercent = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            StartRemainingMWh = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            EndRemainingMWh = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            DischargeWh = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            SampleCount = reader.GetInt32(11),
            StartedAtFullCharge = reader.GetInt64(12) != 0,
            StartedAtSessionBoundary = reader.GetInt64(13) != 0,
            IsOpen = reader.GetInt64(14) != 0,
            Confidence = ParseConfidence(reader.GetString(15))
        };
    }

    private static BatteryCycleConfidence ParseConfidence(string value)
        => Enum.TryParse<BatteryCycleConfidence>(value, out var confidence)
            ? confidence
            : BatteryCycleConfidence.Low;

    private async Task EnsureInitializedAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }
    }

    /// <summary>
    /// Serializes a DateTime (assumed UTC) to ISO-8601 string for SQLite storage.
    /// </summary>
    internal static string SerializeTimestamp(DateTime timestamp)
    {
        // Ensure UTC and use the "O" round-trip format
        return timestamp.Kind == DateTimeKind.Utc
            ? timestamp.ToString("O")
            : timestamp.ToUniversalTime().ToString("O");
    }

    /// <summary>
    /// Deserializes an ISO-8601 string from SQLite back to a UTC DateTime.
    /// </summary>
    internal static DateTime DeserializeTimestamp(string value)
    {
        var dt = DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
        return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Close the shared write connection if one was opened. Tests in
        // particular need the file unlocked between cases.
        var conn = _writeConnection;
        _writeConnection = null;
        if (conn is not null)
        {
            try { conn.Dispose(); } catch { /* best effort */ }
        }
        _writeLock.Dispose();
    }
}

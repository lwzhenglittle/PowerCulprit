using System.Data;
using Microsoft.Data.Sqlite;
using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Storage;

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

    // Persistent write connection, lazily opened on first write and reused for
    // every subsequent write. MonitoringService.RunSingleCycleAsync fans out up
    // to six Insert*Async calls via Task.WhenAll every 2 s; without sharing, each
    // call paid for a new SqliteConnection (handle setup, pool lookup, WAL lock).
    // SqliteConnection is not thread-safe, so _writeLock serialises access. Reads
    // still use ephemeral pooled connections so they don't contend with writes.
    private SqliteConnection? _writeConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await CreateSystemPowerSamplesTableAsync(connection, transaction);
        await CreateProcessSamplesTableAsync(connection, transaction);
        await CreateGpuProcessSamplesTableAsync(connection, transaction);
        await CreateHardwareSensorSamplesTableAsync(connection, transaction);
        await CreateAnalysisReportsTableAsync(connection, transaction);
        await CreateSourceStatusTableAsync(connection, transaction);
        await CreateBatteryCycleTablesAsync(connection, transaction);

        await transaction.CommitAsync();
        _initialized = true;
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
        _writeConnection = connection;
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyConnectionPragmasAsync(connection);
        return connection;
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection)
    {
        await using var cmd = new SqliteCommand(
            $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};",
            connection);
        await cmd.ExecuteNonQueryAsync();
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
                is_ac_online    INTEGER NOT NULL,
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
            CREATE TABLE IF NOT EXISTS process_samples (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc   TEXT    NOT NULL,
                pid             INTEGER NOT NULL,
                process_name    TEXT    NOT NULL,
                executable_path TEXT,
                command_line    TEXT,
                parent_pid      INTEGER,
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
                service_name    TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_process_samples_ts
                ON process_samples(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_process_samples_pid
                ON process_samples(pid);
            CREATE INDEX IF NOT EXISTS idx_process_samples_ts_pid
                ON process_samples(timestamp_utc, pid);
            CREATE INDEX IF NOT EXISTS idx_process_samples_ts_name
                ON process_samples(timestamp_utc, process_name);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();

        // Backfill the column on older DBs created before svchost-service attribution.
        // CREATE TABLE IF NOT EXISTS won't add columns to an existing table, so we
        // probe via PRAGMA table_info and ALTER TABLE ADD COLUMN when needed.
        await AddColumnIfMissingAsync(connection, transaction, "process_samples", "service_name", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "process_samples", "process_start_count", "INTEGER");
        await AddColumnIfMissingAsync(connection, transaction, "process_samples", "process_stop_count", "INTEGER");
        await AddColumnIfMissingAsync(connection, transaction, "process_samples", "process_short_lived_count", "INTEGER");
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
            CREATE INDEX IF NOT EXISTS idx_gpu_process_pid
                ON gpu_process_samples(pid);
            CREATE INDEX IF NOT EXISTS idx_gpu_process_ts_pid
                ON gpu_process_samples(timestamp_utc, pid);
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
            CREATE INDEX IF NOT EXISTS idx_hw_sensor_source
                ON hardware_sensor_samples(source);
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

    // ──────────────────────────────────────────────
    //  Batch writes
    // ──────────────────────────────────────────────

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
                is_open                 INTEGER NOT NULL,
                confidence              TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_battery_cycles_display_start
                ON battery_cycles(display_cycle_id, start_utc);
            CREATE INDEX IF NOT EXISTS idx_battery_cycles_start_end
                ON battery_cycles(start_utc, end_utc);
            CREATE INDEX IF NOT EXISTS idx_battery_display_cycles_start_end
                ON battery_display_cycles(start_utc, end_utc);
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
                acParam.Value = sample.IsAcOnline ? 1L : 0L;
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

            const string sql = """
                INSERT INTO process_samples
                    (timestamp_utc, pid, process_name, executable_path, command_line,
                     parent_pid, cpu_percent, working_set_mb, private_memory_mb,
                     thread_count, handle_count, disk_read_bytes_per_second,
                     disk_write_bytes_per_second, network_receive_bytes_per_second,
                     network_send_bytes_per_second, is_foreground_process, service_name,
                     process_start_count, process_stop_count, process_short_lived_count)
                VALUES
                    ($ts, $pid, $pn, $ep, $cl, $pp, $cpu, $ws, $pmem,
                     $tc, $hc, $dr, $dw, $nr, $ns, $fg, $sn, $psc, $pstc, $slc);
                """;

            await using var cmd = new SqliteCommand(sql, connection, transaction);

            // Reuse parameter objects across iterations
            var p_ts  = cmd.Parameters.Add("$ts",   SqliteType.Text);
            var p_pid = cmd.Parameters.Add("$pid",  SqliteType.Integer);
            var p_pn  = cmd.Parameters.Add("$pn",   SqliteType.Text);
            var p_ep  = cmd.Parameters.Add("$ep",   SqliteType.Text);
            var p_cl  = cmd.Parameters.Add("$cl",   SqliteType.Text);
            var p_pp  = cmd.Parameters.Add("$pp",   SqliteType.Integer);
            var p_cpu = cmd.Parameters.Add("$cpu",  SqliteType.Real);
            var p_ws  = cmd.Parameters.Add("$ws",   SqliteType.Real);
            var p_pmem = cmd.Parameters.Add("$pmem", SqliteType.Real);
            var p_tc  = cmd.Parameters.Add("$tc",   SqliteType.Integer);
            var p_hc  = cmd.Parameters.Add("$hc",   SqliteType.Integer);
            var p_dr  = cmd.Parameters.Add("$dr",   SqliteType.Real);
            var p_dw  = cmd.Parameters.Add("$dw",   SqliteType.Real);
            var p_nr  = cmd.Parameters.Add("$nr",   SqliteType.Real);
            var p_ns  = cmd.Parameters.Add("$ns",   SqliteType.Real);
            var p_fg  = cmd.Parameters.Add("$fg",   SqliteType.Integer);
            var p_sn  = cmd.Parameters.Add("$sn",   SqliteType.Text);
            var p_psc = cmd.Parameters.Add("$psc",  SqliteType.Integer);
            var p_pstc = cmd.Parameters.Add("$pstc", SqliteType.Integer);
            var p_slc = cmd.Parameters.Add("$slc",  SqliteType.Integer);

            foreach (var sample in samples)
            {
                p_ts.Value  = SerializeTimestamp(sample.TimestampUtc);
                p_pid.Value = sample.Pid;
                p_pn.Value  = sample.ProcessName;
                p_ep.Value  = (object?)sample.ExecutablePath ?? DBNull.Value;
                p_cl.Value  = (object?)sample.CommandLine ?? DBNull.Value;
                p_pp.Value  = (object?)sample.ParentPid ?? DBNull.Value;
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
                p_sn.Value  = (object?)sample.ServiceName ?? DBNull.Value;
                p_psc.Value = (object?)sample.ProcessStartCount ?? DBNull.Value;
                p_pstc.Value = (object?)sample.ProcessStopCount ?? DBNull.Value;
                p_slc.Value = (object?)sample.ShortLivedProcessCount ?? DBNull.Value;

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
                p_ts.Value = SerializeTimestamp(sample.TimestampUtc);
                p_pid.Value = (object?)sample.Pid ?? DBNull.Value;
                p_pn.Value = (object?)sample.ProcessName ?? DBNull.Value;
                p_en.Value = sample.EngineName;
                p_et.Value = sample.EngineType.ToString();
                p_up.Value = sample.UtilizationPercent;

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
                p_ts.Value  = SerializeTimestamp(sample.TimestampUtc);
                p_src.Value = sample.Source;
                p_dn.Value  = sample.DeviceName;
                p_sn.Value  = sample.SensorName;
                p_mn.Value  = sample.MetricName;
                p_val.Value = sample.Value;
                p_u.Value   = sample.Unit;

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
        IReadOnlyList<SourceStatus> sourceStatuses)
    {
        if (powerSample is null &&
            processSamples.Count == 0 &&
            gpuSamples.Count == 0 &&
            hardwareSamples.Count == 0 &&
            sourceStatuses.Count == 0)
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
        cmd.Parameters.AddWithValue("$ac", sample.IsAcOnline ? 1L : 0L);
        cmd.Parameters.AddWithValue("$bp", (object?)sample.BatteryPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cr", (object?)sample.ChargeRateMilliwatts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rc", (object?)sample.RemainingCapacityMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fc", (object?)sample.FullChargeCapacityMWh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ed", (object?)sample.EstimatedDischargeWatts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pm", (object?)sample.PowerMode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertProcessSamplesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ProcessSample> samples)
    {
        const string sql = """
            INSERT INTO process_samples
                (timestamp_utc, pid, process_name, executable_path, command_line,
                 parent_pid, cpu_percent, working_set_mb, private_memory_mb,
                 thread_count, handle_count, disk_read_bytes_per_second,
                 disk_write_bytes_per_second, network_receive_bytes_per_second,
                 network_send_bytes_per_second, is_foreground_process, service_name)
            VALUES
                ($ts, $pid, $pn, $ep, $cl, $pp, $cpu, $ws, $pmem,
                 $tc, $hc, $dr, $dw, $nr, $ns, $fg, $sn);
            """;

        await using var cmd = new SqliteCommand(sql, connection, transaction);
        var p_ts  = cmd.Parameters.Add("$ts",   SqliteType.Text);
        var p_pid = cmd.Parameters.Add("$pid",  SqliteType.Integer);
        var p_pn  = cmd.Parameters.Add("$pn",   SqliteType.Text);
        var p_ep  = cmd.Parameters.Add("$ep",   SqliteType.Text);
        var p_cl  = cmd.Parameters.Add("$cl",   SqliteType.Text);
        var p_pp  = cmd.Parameters.Add("$pp",   SqliteType.Integer);
        var p_cpu = cmd.Parameters.Add("$cpu",  SqliteType.Real);
        var p_ws  = cmd.Parameters.Add("$ws",   SqliteType.Real);
        var p_pmem = cmd.Parameters.Add("$pmem", SqliteType.Real);
        var p_tc  = cmd.Parameters.Add("$tc",   SqliteType.Integer);
        var p_hc  = cmd.Parameters.Add("$hc",   SqliteType.Integer);
        var p_dr  = cmd.Parameters.Add("$dr",   SqliteType.Real);
        var p_dw  = cmd.Parameters.Add("$dw",   SqliteType.Real);
        var p_nr  = cmd.Parameters.Add("$nr",   SqliteType.Real);
        var p_ns  = cmd.Parameters.Add("$ns",   SqliteType.Real);
        var p_fg  = cmd.Parameters.Add("$fg",   SqliteType.Integer);
        var p_sn  = cmd.Parameters.Add("$sn",   SqliteType.Text);

        foreach (var sample in samples)
        {
            p_ts.Value  = SerializeTimestamp(sample.TimestampUtc);
            p_pid.Value = sample.Pid;
            p_pn.Value  = sample.ProcessName;
            p_ep.Value  = (object?)sample.ExecutablePath ?? DBNull.Value;
            p_cl.Value  = (object?)sample.CommandLine ?? DBNull.Value;
            p_pp.Value  = (object?)sample.ParentPid ?? DBNull.Value;
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
            p_sn.Value  = (object?)sample.ServiceName ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
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
                IsAcOnline              = reader.GetInt64(1) != 0,
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
            SELECT timestamp_utc, pid, process_name, executable_path, command_line,
                   parent_pid, cpu_percent, working_set_mb, private_memory_mb,
                   thread_count, handle_count, disk_read_bytes_per_second,
                   disk_write_bytes_per_second, network_receive_bytes_per_second,
                   network_send_bytes_per_second, is_foreground_process, service_name,
                   process_start_count, process_stop_count, process_short_lived_count
            FROM process_samples
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY timestamp_utc;
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
            SELECT timestamp_utc, pid, process_name, cpu_percent, working_set_mb,
                   disk_read_bytes_per_second, disk_write_bytes_per_second,
                   network_receive_bytes_per_second, network_send_bytes_per_second,
                   is_foreground_process, service_name, process_start_count,
                   process_stop_count, process_short_lived_count
            FROM process_samples
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY timestamp_utc;
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

    /// <summary>
    /// Rebuilds persisted battery cycle caches from retained system power samples.
    /// </summary>
    public async Task<BatteryCycleBuildResult> RebuildBatteryCyclesAsync(int retentionDays = 7)
    {
        await EnsureInitializedAsync();

        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddDays(-retentionDays);
        var powerSamples = await GetSystemPowerSamplesAsync(fromUtc, toUtc);
        var result = BatteryCycleBuilder.Build(powerSamples);

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
                   sample_count, started_at_full_charge, is_open, confidence
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
    /// Deletes data older than the specified number of days from all tables.
    /// Returns the total number of deleted rows.
    /// </summary>
    /// <param name="retentionDays">Number of days of data to keep. Default is 7.</param>
    public async Task<int> CleanupOldDataAsync(int retentionDays = 7)
    {
        await EnsureInitializedAsync();

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var cutoffStr = SerializeTimestamp(cutoff);
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
                    cmd.Parameters.AddWithValue("$cutoff", cutoffStr);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            var tables = new[]
            {
                "system_power_samples",
                "process_samples",
                "gpu_process_samples",
                "hardware_sensor_samples",
                "analysis_reports",
                "source_status"
            };

            foreach (var table in tables)
            {
                var sql = $"DELETE FROM {table} WHERE timestamp_utc < $cutoff;";
                await using var cmd = new SqliteCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("$cutoff", cutoffStr);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
        return totalDeleted;
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
                "process_samples",
                "gpu_process_samples",
                "hardware_sensor_samples",
                "analysis_reports",
                "source_status"
            };

            foreach (var table in tables)
            {
                await using var cmd = new SqliteCommand($"DELETE FROM {table};", connection, transaction);
                totalDeleted += await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
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
        await using var cmd = new SqliteCommand("VACUUM;", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

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
                 sample_count, started_at_full_charge, is_open, confidence)
            VALUES
                ($displayId, $start, $end, $last, $startPercent, $endPercent,
                 $dischargePercent, $startMwh, $endMwh, $dischargeWh,
                 $sampleCount, $fullStart, $isOpen, $confidence);
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
            IsOpen = reader.GetInt64(13) != 0,
            Confidence = ParseConfidence(reader.GetString(14))
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

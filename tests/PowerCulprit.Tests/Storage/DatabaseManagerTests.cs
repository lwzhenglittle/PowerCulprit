using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;
using Microsoft.Data.Sqlite;

namespace PowerCulprit.Tests.Storage;

public class DatabaseManagerTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly PowerCulprit.Storage.DatabaseManager _db;

    public DatabaseManagerTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"powerculprit_test_{Guid.NewGuid():N}.db");
        _db = new PowerCulprit.Storage.DatabaseManager(_testDbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { /* best effort */ }
        }
    }

    // ── Initialization ──────────────────────────

    [Fact]
    public async Task InitializeAsync_CreatesDatabaseFile()
    {
        await _db.InitializeAsync();
        Assert.True(File.Exists(_testDbPath));
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await _db.InitializeAsync();
        await _db.InitializeAsync(); // second call should not throw
        Assert.True(File.Exists(_testDbPath));
    }

    [Fact]
    public async Task InitializeAsync_CreatesAllTables()
    {
        await _db.InitializeAsync();

        // Verify tables exist (no exception)
        // We test this indirectly by inserting and querying each table
        Assert.True(File.Exists(_testDbPath));
    }

    [Fact]
    public async Task InitializeAsync_CreatesAggregateTablesAndMetadata()
    {
        await _db.InitializeAsync();

        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='metadata';"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='process_analysis_aggregates';"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='gpu_analysis_aggregates';"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='hardware_sensor_aggregates';"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='process_instances';"));
        Assert.Equal(9, await ScalarLongAsync("PRAGMA user_version;"));
    }

    [Fact]
    public async Task InitializeAsync_UpgradesSchemaV2WithoutClearingHistory()
    {
        var ts = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
        await using (var connection = new SqliteConnection($"Data Source={_testDbPath}"))
        {
            await connection.OpenAsync();
            await using var cmd = new SqliteCommand("""
                PRAGMA user_version=2;
                CREATE TABLE system_power_samples (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    is_ac_online INTEGER NOT NULL,
                    battery_percent REAL,
                    charge_rate_milliwatts REAL,
                    remaining_capacity_mwh REAL,
                    full_charge_capacity_mwh REAL,
                    estimated_discharge_watts REAL,
                    power_mode TEXT
                );
                INSERT INTO system_power_samples(timestamp_utc, is_ac_online, battery_percent)
                VALUES ('2026-06-23T12:00:00.0000000Z', 0, 88.0);
                """, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        await _db.InitializeAsync();

        Assert.Equal(9, await ScalarLongAsync("PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM system_power_samples;"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='process_analysis_aggregates';"));
        // v7: is_ac_online is now nullable — the legacy NOT NULL column must
        // have been relaxed during migration.
        Assert.Equal(0, await ScalarLongAsync("SELECT COUNT(*) FROM pragma_table_info('system_power_samples') WHERE name='is_ac_online' AND \"notnull\"=1;"));
        var samples = await _db.GetSystemPowerSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(1));
        var sample = Assert.Single(samples);
        Assert.Equal(88.0, sample.BatteryPercent);
    }


    [Fact]
    public async Task InsertAndQuery_SystemPowerSamples_RoundTrip()
    {
        var ts1 = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2026, 6, 23, 12, 0, 2, DateTimeKind.Utc);

        var samples = new[]
        {
            new SystemPowerSample
            {
                TimestampUtc = ts1,
                IsAcOnline = false,
                BatteryPercent = 90.0,
                ChargeRateMilliwatts = -12000.0,
                RemainingCapacityMWh = 52000.0,
                FullChargeCapacityMWh = 56000.0,
                EstimatedDischargeWatts = 12.0,
                PowerMode = "Balanced"
            },
            new SystemPowerSample
            {
                TimestampUtc = ts2,
                IsAcOnline = true,
                BatteryPercent = 95.0,
                ChargeRateMilliwatts = 30000.0,
                RemainingCapacityMWh = 54000.0,
                FullChargeCapacityMWh = 56000.0,
                PowerMode = "HighPerformance"
            }
        };

        await _db.InsertSystemPowerSamplesAsync(samples);

        var result = await _db.GetSystemPowerSamplesAsync(ts1.AddSeconds(-1), ts2.AddSeconds(1));
        Assert.Equal(2, result.Count);

        var first = result.First(s => s.TimestampUtc == ts1);
        Assert.False(first.IsAcOnline ?? false);
        Assert.Equal(90.0, first.BatteryPercent);
        Assert.Equal(-12000.0, first.ChargeRateMilliwatts);
        Assert.Equal(12.0, first.EstimatedDischargeWatts);
        Assert.Equal("Balanced", first.PowerMode);
    }

    [Fact]
    public async Task InsertSystemPowerSamples_EmptyList_DoesNotCrash()
    {
        await _db.InsertSystemPowerSamplesAsync(Array.Empty<SystemPowerSample>());
        // No exception = pass
    }

    [Fact]
    public async Task SystemPowerSamples_NullableFields_Preserved()
    {
        var ts = DateTime.UtcNow;
        var samples = new[]
        {
            new SystemPowerSample
            {
                TimestampUtc = ts,
                IsAcOnline = true
                // All other fields null
            }
        };

        await _db.InsertSystemPowerSamplesAsync(samples);

        var result = await _db.GetSystemPowerSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(1));
        var s = Assert.Single(result);
        Assert.Null(s.BatteryPercent);
        Assert.Null(s.ChargeRateMilliwatts);
        Assert.Null(s.EstimatedDischargeWatts);
        Assert.Null(s.PowerMode);
    }

    // ── ProcessSample round-trip ────────────────

    [Fact]
    public async Task InsertAndQuery_ProcessSamples_RoundTrip()
    {
        var ts = DateTime.UtcNow;
        var samples = new[]
        {
            new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 1234,
                ProcessName = "test.exe",
                ExecutablePath = @"C:\test\test.exe",
                CommandLine = "test.exe --flag",
                ParentPid = 100,
                CpuPercent = 5.5,
                WorkingSetMb = 120.3,
                PrivateMemoryMb = 80.1,
                ThreadCount = 12,
                HandleCount = 340,
                DiskReadBytesPerSecond = 1024.0,
                DiskWriteBytesPerSecond = 512.0,
                ProcessStartCount = 3,
                ProcessStopCount = 2,
                ShortLivedProcessCount = 1,
                IsForegroundProcess = true
            },
            new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 5678,
                ProcessName = "bg.exe",
                CpuPercent = 1.2,
                WorkingSetMb = 15.0,
                IsForegroundProcess = false
            }
        };

        await _db.InsertProcessSamplesAsync(samples);

        var result = await _db.GetProcessSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(1));
        Assert.Equal(2, result.Count);

        var fg = result.First(s => s.Pid == 1234);
        Assert.True(fg.IsForegroundProcess);
        Assert.Equal(5.5, fg.CpuPercent);
        Assert.Equal(@"C:\test\test.exe", fg.ExecutablePath);
        Assert.Equal(3, fg.ProcessStartCount);
        Assert.Equal(2, fg.ProcessStopCount);
        Assert.Equal(1, fg.ShortLivedProcessCount);

        var bg = result.First(s => s.Pid == 5678);
        Assert.False(bg.IsForegroundProcess);
        Assert.Null(bg.ExecutablePath);
        Assert.Null(bg.ProcessStartCount);
        Assert.Null(bg.ProcessStopCount);
        Assert.Null(bg.ShortLivedProcessCount);
    }

    [Fact]
    public async Task InsertProcessSamples_EmptyList_DoesNotCrash()
    {
        await _db.InsertProcessSamplesAsync(Array.Empty<ProcessSample>());
    }

    [Fact]
    public async Task ProcessSamples_QueryRespectsTimeWindow()
    {
        var t1 = new DateTime(2026, 6, 23, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 23, 11, 0, 0, DateTimeKind.Utc);

        await _db.InsertProcessSamplesAsync(new[]
        {
            new ProcessSample { TimestampUtc = t1, Pid = 1, ProcessName = "old.exe" },
            new ProcessSample { TimestampUtc = t2, Pid = 2, ProcessName = "new.exe" }
        });

        // Query only the old range
        var oldResults = await _db.GetProcessSamplesAsync(
            t1.AddMinutes(-5), t1.AddMinutes(5));
        Assert.Single(oldResults);
        Assert.Equal("old.exe", oldResults[0].ProcessName);

        // Query everything
        var allResults = await _db.GetProcessSamplesAsync(
            t1.AddMinutes(-5), t2.AddMinutes(5));
        Assert.Equal(2, allResults.Count);
    }

    // ── GpuProcessSample round-trip ─────────────

    [Fact]
    public async Task InsertAndQuery_GpuProcessSamples_RoundTrip()
    {
        var ts = DateTime.UtcNow;
        var samples = new[]
        {
            new GpuProcessSample
            {
                TimestampUtc = ts,
                Pid = 1234,
                ProcessName = "game.exe",
                EngineName = "eng_3d",
                EngineType = GpuEngineType.ThreeD,
                UtilizationPercent = 78.5
            },
            new GpuProcessSample
            {
                TimestampUtc = ts,
                Pid = null, // PID not resolved
                ProcessName = null,
                EngineName = "eng_copy",
                EngineType = GpuEngineType.Copy,
                UtilizationPercent = 3.2
            }
        };

        await _db.InsertGpuProcessSamplesAsync(samples);

        var result = await _db.GetGpuProcessSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(1));
        Assert.Equal(2, result.Count);

        var game = result.First(s => s.Pid == 1234);
        Assert.Equal("game.exe", game.ProcessName);
        Assert.Equal(GpuEngineType.ThreeD, game.EngineType);
        Assert.Equal(78.5, game.UtilizationPercent);

        var unresolved = result.First(s => s.Pid is null);
        Assert.Null(unresolved.ProcessName);
        Assert.Equal(GpuEngineType.Copy, unresolved.EngineType);
    }

    [Fact]
    public async Task InsertGpuProcessSamples_EmptyList_DoesNotCrash()
    {
        await _db.InsertGpuProcessSamplesAsync(Array.Empty<GpuProcessSample>());
    }

    [Fact]
    public async Task GetProcessSamplesForAnalysis_ReturnsOnlyRequestedWindow()
    {
        var oldTs = new DateTime(2026, 6, 23, 10, 0, 0, DateTimeKind.Utc);
        var newTs = new DateTime(2026, 6, 23, 11, 0, 0, DateTimeKind.Utc);

        await _db.InsertProcessSamplesAsync(new[]
        {
            new ProcessSample
            {
                TimestampUtc = oldTs,
                Pid = 1,
                ProcessName = "old.exe",
                CpuPercent = 90.0
            },
            new ProcessSample
            {
                TimestampUtc = newTs,
                Pid = 2,
                ProcessName = "new.exe",
                CpuPercent = 12.5,
                WorkingSetMb = 42.0,
                DiskReadBytesPerSecond = 1024,
                IsForegroundProcess = true,
                CommandLine = "should not be read"
            }
        });

        var result = await _db.GetProcessSamplesForAnalysisAsync(
            newTs.AddSeconds(-1), newTs.AddSeconds(1));

        var sample = Assert.Single(result);
        Assert.Equal("new.exe", sample.ProcessName);
        Assert.Equal(12.5, sample.CpuPercent);
        Assert.Equal(42.0, sample.WorkingSetMb);
        Assert.Equal(1024, sample.DiskReadBytesPerSecond);
        Assert.True(sample.IsForegroundProcess);
        Assert.Null(sample.CommandLine);
    }

    // ── HardwareSensorSample round-trip ─────────

    [Fact]
    public async Task InsertHardwareSensorSamples_Batch_Works()
    {
        var ts = DateTime.UtcNow;
        var samples = new[]
        {
            new HardwareSensorSample
            {
                TimestampUtc = ts,
                Source = "LibreHardwareMonitor",
                DeviceName = "CPU Package",
                SensorName = "Package",
                MetricName = "Power",
                Value = 15.3,
                Unit = "W"
            },
            new HardwareSensorSample
            {
                TimestampUtc = ts,
                Source = "LibreHardwareMonitor",
                DeviceName = "CPU Package",
                SensorName = "Package",
                MetricName = "Temperature",
                Value = 52.0,
                Unit = "°C"
            }
        };

        await _db.InsertHardwareSensorSamplesAsync(samples);
    }

    [Fact]
    public async Task GetHardwareSensorSamplesAsync_ReturnsSamplesInTimeRange()
    {
        var oldTs = new DateTime(2026, 6, 23, 11, 59, 0, DateTimeKind.Utc);
        var ts = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
        var newTs = ts.AddSeconds(2);

        await _db.InsertHardwareSensorSamplesAsync(new[]
        {
            new HardwareSensorSample
            {
                TimestampUtc = oldTs,
                Source = "LibreHardwareMonitor",
                DeviceName = "Intel Core Ultra X7 358H",
                SensorName = "CPU Package",
                MetricName = "Power",
                Value = 99,
                Unit = "W"
            },
            new HardwareSensorSample
            {
                TimestampUtc = ts,
                Source = "LibreHardwareMonitor",
                DeviceName = "Intel Core Ultra X7 358H",
                SensorName = "CPU Package",
                MetricName = "Power",
                Value = 12.3,
                Unit = "W"
            },
            new HardwareSensorSample
            {
                TimestampUtc = newTs,
                Source = "LibreHardwareMonitor",
                DeviceName = "Intel Core Ultra X7 358H",
                SensorName = "CPU Total",
                MetricName = "Load",
                Value = 42.5,
                Unit = "%"
            }
        });

        var result = await _db.GetHardwareSensorSamplesAsync(ts.AddMilliseconds(-1), newTs.AddMilliseconds(1));

        Assert.Equal(2, result.Count);
        Assert.Equal("CPU Package", result[0].SensorName);
        Assert.Equal(12.3, result[0].Value);
        Assert.Equal("CPU Total", result[1].SensorName);
        Assert.Equal(42.5, result[1].Value);
    }

    // ── SourceStatus round-trip ─────────────────

    [Fact]
    public async Task InsertAndQuery_SourceStatuses()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = ts,
            SourceName = "BatteryAPI",
            IsAvailable = true,
            Status = "Available",
            RequiresAdmin = false
        });
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = ts.AddSeconds(1),
            SourceName = "CPU_Package_Power",
            IsAvailable = false,
            Status = "Unavailable",
            Details = "Requires administrator privileges",
            RequiresAdmin = true
        });

        var result = await _db.GetLatestSourceStatusesAsync();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.SourceName == "BatteryAPI" && s.IsAvailable);
        Assert.Contains(result, s => s.SourceName == "CPU_Package_Power" && !s.IsAvailable);
    }

    [Fact]
    public async Task GetLatestSourceStatuses_ReturnsLatestPerSource()
    {
        var baseTs = DateTime.UtcNow;
        // Insert older status
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = baseTs,
            SourceName = "BatteryAPI",
            IsAvailable = false,
            Status = "Unavailable"
        });
        // Insert newer status for same source
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = baseTs.AddMinutes(1),
            SourceName = "BatteryAPI",
            IsAvailable = true,
            Status = "Available"
        });

        var result = await _db.GetLatestSourceStatusesAsync();
        var battery = Assert.Single(result);
        Assert.True(battery.IsAvailable);
        Assert.Equal("Available", battery.Status);
    }

    [Fact]
    public async Task GetLatestSourceStatuses_ReturnsSingleRowWhenLatestTimestampTies()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = ts,
            SourceName = "WindowsETW",
            IsAvailable = false,
            Status = "Unavailable",
            Details = "first"
        });
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = ts,
            SourceName = "WindowsETW",
            IsAvailable = true,
            Status = "Available",
            Details = "last"
        });

        var result = await _db.GetLatestSourceStatusesAsync();
        var etw = Assert.Single(result, s => s.SourceName == "WindowsETW");
        Assert.True(etw.IsAvailable);
        Assert.Equal("Available", etw.Status);
        Assert.Equal("last", etw.Details);
    }

    [Fact]
    public async Task DeduplicateSourceStatusTimestampTies_RemovesOnlyExactTimestampDuplicates()
    {
        var duplicateTs = DateTime.UtcNow;
        var laterTs = duplicateTs.AddMinutes(1);

        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = duplicateTs,
            SourceName = "WindowsETW",
            IsAvailable = false,
            Status = "Unavailable",
            Details = "duplicate-old"
        });
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = duplicateTs,
            SourceName = "WindowsETW",
            IsAvailable = true,
            Status = "Available",
            Details = "duplicate-new"
        });
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = laterTs,
            SourceName = "WindowsETW",
            IsAvailable = true,
            Status = "Available",
            Details = "normal-history"
        });
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = duplicateTs,
            SourceName = "BatteryAPI",
            IsAvailable = true,
            Status = "Available",
            Details = "other-source"
        });

        var deleted = await _db.DeduplicateSourceStatusTimestampTiesAsync();

        Assert.Equal(1, deleted);
        var result = await _db.GetLatestSourceStatusesAsync();
        Assert.Equal(2, result.Count);
        var etw = Assert.Single(result, s => s.SourceName == "WindowsETW");
        Assert.Equal("normal-history", etw.Details);
        Assert.Contains(result, s => s.SourceName == "BatteryAPI" && s.Details == "other-source");
    }

    [Fact]
    public async Task DeduplicateSourceStatusTimestampTies_IsIdempotent()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = ts,
            SourceName = "WindowsETW",
            IsAvailable = false,
            Status = "Unavailable"
        });
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = ts,
            SourceName = "WindowsETW",
            IsAvailable = true,
            Status = "Available"
        });

        Assert.Equal(1, await _db.DeduplicateSourceStatusTimestampTiesAsync());
        Assert.Equal(0, await _db.DeduplicateSourceStatusTimestampTiesAsync());
    }


    [Fact]
    public async Task InsertAnalysisReport_WritesItems()
    {
        var windowStart = DateTime.UtcNow.AddMinutes(-30);
        var windowEnd = DateTime.UtcNow;

        var items = new[]
        {
            new CulpritReportItem
            {
                ProcessName = "chrome.exe",
                Pid = 1234,
                Score = 85.0,
                Rank = 1,
                AvgCpuPercent = 12.3,
                PowerCorrelation = 0.71,
                Reason = "High CPU + GPU VideoDecode correlation"
            },
            new CulpritReportItem
            {
                ProcessName = "svchost.exe",
                Pid = 500,
                Score = 20.0,
                Rank = 2,
                AvgCpuPercent = 5.0,
                Reason = "Moderate I/O activity"
            }
        };

        await _db.InsertAnalysisReportAsync(windowStart, windowEnd, items);
    }

    [Fact]
    public async Task InsertAnalysisReport_EmptyList_DoesNotCrash()
    {
        await _db.InsertAnalysisReportAsync(
            DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow,
            Array.Empty<CulpritReportItem>());
    }

    // ── Cleanup ─────────────────────────────────

    [Fact]
    public async Task CleanupOldData_RemovesOldRecords()
    {
        var oldTs = DateTime.UtcNow.AddDays(-10);
        var newTs = DateTime.UtcNow;

        // Insert old sample
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            new SystemPowerSample
            {
                TimestampUtc = oldTs,
                IsAcOnline = true,
                BatteryPercent = 100
            }
        });

        // Insert new sample
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            new SystemPowerSample
            {
                TimestampUtc = newTs,
                IsAcOnline = false,
                BatteryPercent = 50
            }
        });

        var deleted = await _db.CleanupOldDataAsync(retentionDays: 7);

        Assert.True(deleted > 0, "Should have deleted at least the old record");

        // Old data should be gone
        var oldResults = await _db.GetSystemPowerSamplesAsync(
            oldTs.AddMinutes(-1), oldTs.AddMinutes(1));
        Assert.Empty(oldResults);

        // New data should remain
        var newResults = await _db.GetSystemPowerSamplesAsync(
            newTs.AddMinutes(-1), newTs.AddMinutes(1));
        Assert.NotEmpty(newResults);
    }

    [Fact]
    public async Task CleanupOldData_NoOldRecords_ReturnsZero()
    {
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            new SystemPowerSample
            {
                TimestampUtc = DateTime.UtcNow,
                IsAcOnline = true,
                BatteryPercent = 80
            }
        });

        var deleted = await _db.CleanupOldDataAsync(retentionDays: 7);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task ClearHistoricalData_RemovesAllPersistedHistory()
    {
        var ts = DateTime.UtcNow;

        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            new SystemPowerSample { TimestampUtc = ts, IsAcOnline = false, BatteryPercent = 80 }
        });
        await _db.InsertProcessSamplesAsync(new[]
        {
            new ProcessSample { TimestampUtc = ts, Pid = 1, ProcessName = "app.exe", CpuPercent = 10 }
        });
        await _db.InsertGpuProcessSamplesAsync(new[]
        {
            new GpuProcessSample
            {
                TimestampUtc = ts,
                Pid = 1,
                ProcessName = "app.exe",
                EngineName = "eng_3d",
                EngineType = GpuEngineType.ThreeD,
                UtilizationPercent = 20
            }
        });
        await _db.InsertSourceStatusAsync(new SourceStatus
        {
            TimestampUtc = ts,
            SourceName = "BatteryAPI",
            IsAvailable = true,
            Status = "Available"
        });
        await _db.RebuildBatteryCyclesAsync();

        var deletedRows = await _db.ClearHistoricalDataAsync();

        Assert.True(deletedRows >= 4);
        Assert.Empty(await _db.GetSystemPowerSamplesAsync(ts.AddMinutes(-1), ts.AddMinutes(1)));
        Assert.Empty(await _db.GetProcessSamplesAsync(ts.AddMinutes(-1), ts.AddMinutes(1)));
        Assert.Empty(await _db.GetGpuProcessSamplesAsync(ts.AddMinutes(-1), ts.AddMinutes(1)));
        Assert.Empty(await _db.GetLatestSourceStatusesAsync());
        Assert.Empty(await _db.GetLatestBatteryDisplayCyclesAsync(10));
    }

    // ── Edge cases ──────────────────────────────

    [Fact]
    public async Task RebuildBatteryCycles_PersistsDisplayAndRawCycles()
    {
        var start = DateTime.UtcNow.AddHours(-2);
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            Power(start, ac: true, percent: 90),
            Power(start.AddMinutes(1), ac: false, percent: 90),
            Power(start.AddMinutes(2), ac: false, percent: 80),
            Power(start.AddMinutes(3), ac: true, percent: 80),
            Power(start.AddMinutes(3).AddSeconds(1), ac: true, percent: 80),
            Power(start.AddMinutes(4), ac: false, percent: 80),
            Power(start.AddMinutes(5), ac: false, percent: 65),
            Power(start.AddMinutes(6), ac: true, percent: 65)
        });

        await _db.RebuildBatteryCyclesAsync();

        var displayCycles = await _db.GetLatestBatteryDisplayCyclesAsync(10);
        Assert.Equal(2, displayCycles.Count);
        var orderedDisplays = displayCycles.OrderBy(c => c.StartUtc).ToList();
        Assert.All(orderedDisplays, display => Assert.Equal(1, display.RawCycleCount));
        Assert.Equal(10, orderedDisplays[0].DischargePercent);
        Assert.Equal(15, orderedDisplays[1].DischargePercent);

        foreach (var display in orderedDisplays)
        {
            var rawCycles = await _db.GetBatteryCyclesForDisplayCycleAsync(display.Id);
            var raw = Assert.Single(rawCycles);
            Assert.Equal(display.Id, raw.DisplayCycleId);
        }
    }

    [Fact]
    public async Task RebuildBatteryCycles_RespectsSessionStartMarkers()
    {
        var start = DateTime.UtcNow.AddHours(-2);
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            Power(start, ac: true, percent: 90),
            Power(start.AddMinutes(1), ac: false, percent: 90),
            Power(start.AddMinutes(2), ac: false, percent: 85),
            Power(start.AddMinutes(3), ac: false, percent: 80),
            Power(start.AddMinutes(4), ac: false, percent: 75)
        });
        await _db.InsertSessionStartMarkerAsync(start.AddMinutes(2).AddSeconds(30));

        await _db.RebuildBatteryCyclesAsync();

        var displayCycles = await _db.GetLatestBatteryDisplayCyclesAsync(10);
        var display = Assert.Single(displayCycles);
        var rawCycles = await _db.GetBatteryCyclesForDisplayCycleAsync(display.Id);
        var raw = Assert.Single(rawCycles);
        Assert.Equal(start.AddMinutes(1), raw.StartUtc, TimeSpan.FromSeconds(1));
        Assert.True(raw.IsOpen);
        Assert.False(raw.StartedAtSessionBoundary);
    }

    [Fact]
    public async Task RebuildBatteryCycles_UsesPowerStateEvents_ToPreserveHighConfidence()
    {
        var start = DateTime.UtcNow.AddHours(-2);
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            Power(start, ac: true, percent: 80),
            Power(start.AddMinutes(1), ac: false, percent: 80),
            // Gap > 10 minutes, but covered by a confirmed sleep interval.
            Power(start.AddMinutes(11).AddSeconds(1), ac: false, percent: 75),
            Power(start.AddMinutes(12), ac: true, percent: 75)
        });
        await _db.InsertPowerStateEventAsync(new PowerStateEvent
        {
            TimestampUtc = start.AddMinutes(2),
            Kind = PowerStateEventKind.Suspend,
            Source = "PowerCulprit",
            Details = "test suspend"
        });
        await _db.InsertPowerStateEventAsync(new PowerStateEvent
        {
            TimestampUtc = start.AddMinutes(10),
            Kind = PowerStateEventKind.Resume,
            Source = "PowerCulprit",
            Details = "test resume"
        });

        await _db.RebuildBatteryCyclesAsync();

        var displayCycles = await _db.GetLatestBatteryDisplayCyclesAsync(10);
        var display = Assert.Single(displayCycles);
        Assert.Equal(BatteryCycleConfidence.High, display.Confidence);

        var rawCycles = await _db.GetBatteryCyclesForDisplayCycleAsync(display.Id);
        Assert.All(rawCycles, c => Assert.Equal(BatteryCycleConfidence.High, c.Confidence));
    }

    [Fact]
    public async Task ClearHistoricalData_ReplacesSessionStartMarkersWithClearBoundary()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertSessionStartMarkerAsync(ts);

        Assert.Single(await _db.GetSessionStartMarkersAsync(ts.AddSeconds(-1), ts.AddSeconds(1)));

        var beforeClear = DateTime.UtcNow;
        await _db.ClearHistoricalDataAsync();
        var afterClear = DateTime.UtcNow;

        var markers = await _db.GetSessionStartMarkersAsync(beforeClear.AddSeconds(-1), afterClear.AddSeconds(1));
        var marker = Assert.Single(markers);
        Assert.InRange(marker, beforeClear.AddSeconds(-1), afterClear.AddSeconds(1));
    }


    [Fact]
    public async Task GetLatestBatteryDisplayCycles_ReturnsOpenCycleFirst()
    {
        var start = DateTime.UtcNow.AddHours(-1);
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            Power(start, ac: true, percent: 95),
            Power(start.AddMinutes(1), ac: false, percent: 95),
            Power(start.AddMinutes(2), ac: false, percent: 92)
        });

        await _db.RebuildBatteryCyclesAsync();

        var display = Assert.Single(await _db.GetLatestBatteryDisplayCyclesAsync(1));
        Assert.True(display.IsOpen);
        Assert.Null(display.EndUtc);
        Assert.Equal(start.AddMinutes(2), display.EffectiveEndUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CleanupOldData_RemovesOldBatteryCycles()
    {
        var old = DateTime.UtcNow.AddDays(-10);
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            Power(old, ac: true, percent: 80),
            Power(old.AddMinutes(1), ac: false, percent: 80),
            Power(old.AddMinutes(2), ac: false, percent: 70),
            Power(old.AddMinutes(3), ac: true, percent: 70)
        });
        await _db.RebuildBatteryCyclesAsync(retentionDays: 30);

        Assert.NotEmpty(await _db.GetLatestBatteryDisplayCyclesAsync(10));

        await _db.CleanupOldDataAsync(retentionDays: 7);

        Assert.Empty(await _db.GetLatestBatteryDisplayCyclesAsync(10));
    }

    private static SystemPowerSample Power(DateTime timestampUtc, bool ac, double percent)
    {
        return new SystemPowerSample
        {
            TimestampUtc = timestampUtc,
            IsAcOnline = ac,
            BatteryPercent = percent,
            RemainingCapacityMWh = percent * 1000.0,
            FullChargeCapacityMWh = 100000.0,
            ChargeRateMilliwatts = ac ? 5000 : -10000
        };
    }

    [Fact]
    public async Task EmptyDatabase_QueryReturnsEmpty()
    {
        var now = DateTime.UtcNow;
        var result = await _db.GetSystemPowerSamplesAsync(
            now.AddDays(-1), now.AddDays(1));
        Assert.Empty(result);
    }

    [Fact]
    public async Task AutoInitializes_OnFirstWrite()
    {
        // Don't call InitializeAsync explicitly — insert should auto-init
        var ts = DateTime.UtcNow;
        await _db.InsertSystemPowerSamplesAsync(new[]
        {
            new SystemPowerSample { TimestampUtc = ts, IsAcOnline = true }
        });

        var result = await _db.GetSystemPowerSamplesAsync(
            ts.AddSeconds(-1), ts.AddSeconds(1));
        Assert.Single(result);
    }

    [Fact]
    public void DefaultDatabasePath_IsUnderLocalAppData()
    {
        var path = PowerCulprit.Storage.DatabaseManager.GetDefaultDatabasePath();
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, path);
        Assert.EndsWith("powerculprit.db", path);
    }

    [Fact]
    public async Task ServiceName_RoundTripsThroughProcessSamples()
    {
        var ts = DateTime.UtcNow;

        await _db.InsertProcessSamplesAsync(new[]
        {
            new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 1001,
                ProcessName = "svchost",
                ServiceName = "Dnscache, NlaSvc",
                CpuPercent = 2.0,
                IsForegroundProcess = false
            },
            new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 1002,
                ProcessName = "chrome",
                // intentionally no ServiceName — must come back as null, not ""
                CpuPercent = 4.0,
                IsForegroundProcess = true
            }
        });

        var full = await _db.GetProcessSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(1));
        var svchost = full.First(p => p.Pid == 1001);
        var chrome  = full.First(p => p.Pid == 1002);
        Assert.Equal("Dnscache, NlaSvc", svchost.ServiceName);
        Assert.Null(chrome.ServiceName);

        // Same trip via the analysis-shaped reader — that's the path the historical
        // view actually uses and it has its own narrower SELECT.
        var analysis = await _db.GetProcessSamplesForAnalysisAsync(
            ts.AddSeconds(-1), ts.AddSeconds(1));
        Assert.Equal("Dnscache, NlaSvc", analysis.First(p => p.Pid == 1001).ServiceName);
        Assert.Null(analysis.First(p => p.Pid == 1002).ServiceName);
    }

    [Fact]
    public async Task ProcessSamples_ReusesProcessIdentityForRepeatedStaticMetadata()
    {
        var start = DateTime.UtcNow;

        await _db.InsertProcessSamplesAsync(new[]
        {
            new ProcessSample
            {
                TimestampUtc = start,
                Pid = 123,
                ProcessName = "app",
                ExecutablePath = @"C:\app.exe",
                CommandLine = "app --flag",
                ParentPid = 1,
                CpuPercent = 1
            },
            new ProcessSample
            {
                TimestampUtc = start.AddSeconds(2),
                Pid = 123,
                ProcessName = "app",
                ExecutablePath = @"C:\app.exe",
                CommandLine = "app --flag",
                ParentPid = 1,
                CpuPercent = 2
            }
        });

        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM process_identities;"));
        Assert.Equal(2, await ScalarLongAsync("SELECT COUNT(*) FROM process_sample_facts;"));
        Assert.Equal(2, await ScalarLongAsync("SELECT COUNT(*) FROM sample_timestamps;"));
    }

    [Fact]
    public async Task ProcessSamples_NormalizesServiceGroupMembers()
    {
        var ts = DateTime.UtcNow;

        await _db.InsertProcessSamplesAsync(new[]
        {
            new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 1001,
                ProcessName = "svchost",
                ServiceName = "NlaSvc, Dnscache, Dnscache",
                CpuPercent = 1
            }
        });

        var sample = Assert.Single(await _db.GetProcessSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(1)));
        Assert.Equal("Dnscache, NlaSvc", sample.ServiceName);
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM service_groups;"));
        Assert.Equal(2, await ScalarLongAsync("SELECT COUNT(*) FROM service_group_members;"));
    }

    [Fact]
    public async Task InitializeAsync_MigratesLegacyWideProcessTableByClearingHistory()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"powerculprit_legacy_{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
            {
                await connection.OpenAsync();
                await using var cmd = new SqliteCommand("""
                    PRAGMA user_version=1;
                    CREATE TABLE process_samples (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp_utc TEXT NOT NULL,
                        pid INTEGER NOT NULL,
                        process_name TEXT NOT NULL
                    );
                    CREATE INDEX idx_process_samples_ts ON process_samples(timestamp_utc);
                    INSERT INTO process_samples(timestamp_utc, pid, process_name)
                    VALUES ('2026-06-23T10:00:00.0000000Z', 10, 'legacy.exe');
                    CREATE TABLE system_power_samples (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp_utc TEXT NOT NULL,
                        is_ac_online INTEGER NOT NULL
                    );
                    INSERT INTO system_power_samples(timestamp_utc, is_ac_online)
                    VALUES ('2026-06-23T10:00:00.0000000Z', 0);
                    """, connection);
                await cmd.ExecuteNonQueryAsync();
            }

            using var legacyDb = new PowerCulprit.Storage.DatabaseManager(legacyPath);
            await legacyDb.InitializeAsync();

            Assert.Equal(9, await ScalarLongAsync(legacyPath, "PRAGMA user_version;"));
            Assert.Equal(0, await ScalarLongAsync(
                legacyPath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='process_samples';"));
            Assert.Equal(1, await ScalarLongAsync(
                legacyPath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='process_sample_facts';"));
            Assert.Equal(0, await ScalarLongAsync(legacyPath, "SELECT COUNT(*) FROM system_power_samples;"));
        }
        finally
        {
            if (File.Exists(legacyPath))
            {
                try { File.Delete(legacyPath); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task WmiActivitySamples_RoundTripAndAggregateByCaller()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertProcessSamplesAsync(new[]
        {
            new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 4242,
                ProcessName = "caller.exe",
                ExecutablePath = @"C:\caller.exe",
                CpuPercent = 1
            }
        });
        await _db.InsertWmiActivitySamplesAsync(new[]
        {
            new WmiActivitySample
            {
                TimestampUtc = ts.AddSeconds(1),
                EventRecordId = 1001,
                ClientProcessId = 4242,
                EventId = 5858,
                User = "machine\\user",
                Operation = "Start IWbemServices::ExecQuery - root\\cimv2 : SELECT * FROM Win32_Process",
                NamespaceName = "root\\cimv2",
                QueryText = "SELECT * FROM Win32_Process",
                ResultCode = "0x0"
            },
            new WmiActivitySample
            {
                TimestampUtc = ts.AddSeconds(2),
                EventRecordId = 1002,
                ClientProcessId = 4242,
                EventId = 5858,
                Operation = "Start IWbemServices::ExecQuery - root\\cimv2 : SELECT * FROM Win32_OperatingSystem",
                NamespaceName = "root\\cimv2",
                QueryText = "SELECT * FROM Win32_OperatingSystem",
                ResultCode = "0x80041032",
                PossibleCause = "Throttling"
            }
        });

        var aggregates = await _db.GetWmiCallerAggregatesAsync(ts.AddSeconds(-1), ts.AddSeconds(10), 10);

        var aggregate = Assert.Single(aggregates);
        Assert.Equal(4242, aggregate.ClientProcessId);
        Assert.Equal("caller.exe", aggregate.ProcessName);
        Assert.Equal(@"C:\caller.exe", aggregate.ExecutablePath);
        Assert.Equal(2, aggregate.CallCount);
        Assert.Equal(1, aggregate.FailureCount);
        Assert.Equal(2, aggregate.UniqueOperationCount);
        Assert.Contains("Win32_OperatingSystem", aggregate.LastOperation);
        Assert.Equal("0x80041032", aggregate.LastResultCode);
        Assert.Equal("Throttling", aggregate.LastPossibleCause);
        Assert.Equal(1002, await _db.GetLatestWmiActivityEventRecordIdAsync());
    }

    [Fact]
    public async Task WmiActivitySamples_DuplicateEventRecordId_IsIgnored()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertWmiActivitySamplesAsync(new[]
        {
            new WmiActivitySample
            {
                TimestampUtc = ts,
                EventRecordId = 2001,
                ClientProcessId = 100,
                EventId = 5858,
                Operation = "first"
            },
            new WmiActivitySample
            {
                TimestampUtc = ts.AddSeconds(1),
                EventRecordId = 2001,
                ClientProcessId = 100,
                EventId = 5858,
                Operation = "duplicate"
            }
        });

        var aggregates = await _db.GetWmiCallerAggregatesAsync(ts.AddSeconds(-1), ts.AddSeconds(5), 10);

        var aggregate = Assert.Single(aggregates);
        Assert.Equal(1, aggregate.CallCount);
        Assert.Equal("first", aggregate.LastOperation);
        Assert.Equal(2001, await _db.GetLatestWmiActivityEventRecordIdAsync());
    }

    [Fact]
    public async Task CleanupOldData_PreservesWmiActivityCursor()
    {
        var ts = DateTime.UtcNow.AddDays(-2);
        await _db.InsertWmiActivitySamplesAsync(new[]
        {
            new WmiActivitySample
            {
                TimestampUtc = ts,
                EventRecordId = 3001,
                ClientProcessId = 100,
                EventId = 5858
            }
        });

        await _db.CleanupOldDataAsync(retentionDays: 7, rawRetention: TimeSpan.Zero);

        Assert.Empty(await _db.GetWmiCallerAggregatesAsync(ts.AddSeconds(-1), ts.AddSeconds(1), 10));
        Assert.Equal(3001, await _db.GetLatestWmiActivityEventRecordIdAsync());
    }

    [Fact]
    public async Task WmiActivitySamples_QueryRespectsTimeWindow()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertWmiActivitySamplesAsync(new[]
        {
            new WmiActivitySample { TimestampUtc = ts.AddMinutes(-10), ClientProcessId = 1, EventId = 5858 },
            new WmiActivitySample { TimestampUtc = ts, ClientProcessId = 2, EventId = 5858 },
            new WmiActivitySample { TimestampUtc = ts.AddMinutes(10), ClientProcessId = 3, EventId = 5858 }
        });

        var aggregates = await _db.GetWmiCallerAggregatesAsync(ts.AddSeconds(-1), ts.AddSeconds(1), 10);

        var aggregate = Assert.Single(aggregates);
        Assert.Equal(2, aggregate.ClientProcessId);
    }

    [Fact]
    public async Task ClearHistoricalData_RemovesWmiActivitySamples()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertWmiActivitySamplesAsync(new[]
        {
            new WmiActivitySample { TimestampUtc = ts, ClientProcessId = 7, EventId = 5858 }
        });

        Assert.Single(await _db.GetWmiCallerAggregatesAsync(ts.AddSeconds(-1), ts.AddSeconds(1), 10));

        await _db.ClearHistoricalDataAsync();

        Assert.Empty(await _db.GetWmiCallerAggregatesAsync(ts.AddSeconds(-1), ts.AddSeconds(1), 10));
    }

    // ── Non-finite sensor values (NaN / ±Infinity) ─────────────
    // SQLite binds double.NaN as NULL, which would hit the NOT NULL constraint
    // on hardware_sensor_samples.value / gpu_process_samples.utilization_percent
    // and roll back the whole monitoring-cycle transaction. The inserts must
    // skip the bad rows and commit everything else.

    [Fact]
    public async Task InsertMonitoringCycle_ProcessLifecycle_RoundTripsStableInstancesAndParent()
    {
        var parentStart = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var childStart = parentStart.AddSeconds(2);
        var childStop = childStart.AddSeconds(4);
        var events = new[]
        {
            new ProcessLifecycleEvent
            {
                Kind = ProcessLifecycleEventKind.Start, TimestampUtc = parentStart,
                StartTimeUtc = parentStart, Pid = 100, ProcessName = "launcher.exe"
            },
            new ProcessLifecycleEvent
            {
                Kind = ProcessLifecycleEventKind.Start, TimestampUtc = childStart,
                StartTimeUtc = childStart, Pid = 200, ProcessName = "worker.exe",
                ParentPid = 100, ParentStartTimeUtc = parentStart, CommandLine = "worker.exe --once"
            },
            new ProcessLifecycleEvent
            {
                Kind = ProcessLifecycleEventKind.Stop, TimestampUtc = childStop,
                StartTimeUtc = childStart, Pid = 200, ProcessName = "worker.exe"
            }
        };

        await _db.InsertMonitoringCycleAsync(
            null,
            Array.Empty<ProcessSample>(),
            Array.Empty<GpuProcessSample>(),
            Array.Empty<HardwareSensorSample>(),
            Array.Empty<SourceStatus>(),
            processLifecycleEvents: events);
        await _db.InsertMonitoringCycleAsync(
            null,
            Array.Empty<ProcessSample>(),
            Array.Empty<GpuProcessSample>(),
            Array.Empty<HardwareSensorSample>(),
            Array.Empty<SourceStatus>(),
            processLifecycleEvents: new[] { events[1] });

        var instances = await _db.GetProcessInstancesAsync(parentStart, childStop.AddSeconds(1));

        Assert.Equal(2, instances.Count);
        var parent = Assert.Single(instances, instance => instance.Pid == 100);
        var child = Assert.Single(instances, instance => instance.Pid == 200);
        Assert.Equal(parent.Id, child.ParentInstanceId);
        Assert.Equal("launcher.exe", child.ParentProcessName);
        Assert.Equal(childStart, child.StartTimeUtc);
        Assert.Equal(childStop, child.StopTimeUtc);
        Assert.True(child.StartObserved);
        Assert.True(child.StopObserved);
        Assert.Equal(TimeSpan.FromSeconds(4), child.Lifetime);
    }

    [Fact]
    public async Task InsertMonitoringCycle_NaNSensorValue_SkipsRowAndCommitsRest()
    {
        var ts = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);

        await _db.InsertMonitoringCycleAsync(
            new SystemPowerSample { TimestampUtc = ts, IsAcOnline = false, BatteryPercent = 90 },
            new[]
            {
                new ProcessSample { TimestampUtc = ts, Pid = 100, ProcessName = "app.exe", CpuPercent = 5.0 }
            },
            Array.Empty<GpuProcessSample>(),
            new[]
            {
                new HardwareSensorSample
                {
                    TimestampUtc = ts, Source = "LibreHardwareMonitor", DeviceName = "Intel Core Ultra X7 358H",
                    SensorName = "CPU Package", MetricName = "Power", Value = 12.3, Unit = "W"
                },
                new HardwareSensorSample
                {
                    TimestampUtc = ts, Source = "LibreHardwareMonitor", DeviceName = "Intel Core Ultra X7 358H",
                    SensorName = "CPU Core", MetricName = "Temperature", Value = double.NaN, Unit = "°C"
                },
                new HardwareSensorSample
                {
                    TimestampUtc = ts, Source = "LibreHardwareMonitor", DeviceName = "Intel Core Ultra X7 358H",
                    SensorName = "CPU Total", MetricName = "Load", Value = 42.5, Unit = "%"
                }
            },
            Array.Empty<SourceStatus>());

        // The NaN row is skipped; the other sensor rows and the rest of the
        // cycle (power + process) must still be committed.
        Assert.Equal(2, await ScalarLongAsync("SELECT COUNT(*) FROM hardware_sensor_samples;"));
        Assert.Equal(0, await ScalarLongAsync("SELECT COUNT(*) FROM hardware_sensor_samples WHERE sensor_name = 'CPU Core';"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM system_power_samples;"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM process_sample_facts;"));
    }

    [Fact]
    public async Task InsertMonitoringCycle_NonFiniteGpuUtilization_SkipsRowsAndCommitsRest()
    {
        var ts = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);

        await _db.InsertMonitoringCycleAsync(
            new SystemPowerSample { TimestampUtc = ts, IsAcOnline = false, BatteryPercent = 90 },
            new[]
            {
                new ProcessSample { TimestampUtc = ts, Pid = 100, ProcessName = "app.exe", CpuPercent = 5.0 }
            },
            new[]
            {
                new GpuProcessSample
                {
                    TimestampUtc = ts, Pid = 100, ProcessName = "app.exe",
                    EngineName = "eng_3d", EngineType = GpuEngineType.ThreeD, UtilizationPercent = 35.0
                },
                new GpuProcessSample
                {
                    TimestampUtc = ts, Pid = 100, ProcessName = "app.exe",
                    EngineName = "eng_compute", EngineType = GpuEngineType.Compute, UtilizationPercent = double.NaN
                },
                new GpuProcessSample
                {
                    TimestampUtc = ts, Pid = 100, ProcessName = "app.exe",
                    EngineName = "eng_copy", EngineType = GpuEngineType.Copy, UtilizationPercent = double.PositiveInfinity
                }
            },
            Array.Empty<HardwareSensorSample>(),
            Array.Empty<SourceStatus>());

        // Only the finite row survives; power and process rows still commit.
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM gpu_process_samples;"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM gpu_process_samples WHERE engine_type = 'ThreeD';"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM system_power_samples;"));
        Assert.Equal(1, await ScalarLongAsync("SELECT COUNT(*) FROM process_sample_facts;"));
    }

    [Fact]
    public async Task InsertHardwareSensorSamplesAsync_NonFiniteValues_AreSkipped()
    {
        var ts = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);

        await _db.InsertHardwareSensorSamplesAsync(new[]
        {
            new HardwareSensorSample
            {
                TimestampUtc = ts, Source = "LibreHardwareMonitor", DeviceName = "Intel Core Ultra X7 358H",
                SensorName = "CPU Package", MetricName = "Power", Value = 12.3, Unit = "W"
            },
            new HardwareSensorSample
            {
                TimestampUtc = ts, Source = "LibreHardwareMonitor", DeviceName = "Intel Core Ultra X7 358H",
                SensorName = "CPU Core", MetricName = "Temperature", Value = double.NaN, Unit = "°C"
            },
            new HardwareSensorSample
            {
                TimestampUtc = ts, Source = "LibreHardwareMonitor", DeviceName = "Intel Core Ultra X7 358H",
                SensorName = "CPU Total", MetricName = "Load", Value = double.NegativeInfinity, Unit = "%"
            }
        });

        var result = await _db.GetHardwareSensorSamplesAsync(ts.AddSeconds(-1), ts.AddSeconds(1));
        var sample = Assert.Single(result);
        Assert.Equal("CPU Package", sample.SensorName);
        Assert.Equal(12.3, sample.Value);
    }

    [Fact]
    public async Task InitializeAsync_CorrectsLegacyLhmEnergyUnitWithoutChangingValue()
    {
        var ts = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        await _db.InsertHardwareSensorSamplesAsync(new[]
        {
            new HardwareSensorSample
            {
                TimestampUtc = ts,
                Source = "LibreHardwareMonitor",
                DeviceName = "Battery",
                SensorName = "Remaining Capacity",
                MetricName = "Energy",
                Value = 99900,
                Unit = "J"
            }
        });
        using var upgraded = new PowerCulprit.Storage.DatabaseManager(_testDbPath);
        await upgraded.InitializeAsync();
        var sample = Assert.Single(await upgraded.GetHardwareSensorSamplesAsync(
            ts.AddSeconds(-1), ts.AddSeconds(1)));

        Assert.Equal(99900, sample.Value);
        Assert.Equal("mWh", sample.Unit);
    }

    [Fact]
    public async Task GetGpuProcessAggregates_VideoActivity_IsPerRowAverageNotSum()
    {
        var ts = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
        var samples = new List<GpuProcessSample>();
        // 5 sampling cycles, each with one 3D row at 50% and one VideoDecode row
        // at 20% — 10 engine rows in total.
        for (var i = 0; i < 5; i++)
        {
            samples.Add(new GpuProcessSample
            {
                TimestampUtc = ts.AddSeconds(i * 2), Pid = 100, ProcessName = "player.exe",
                EngineName = "eng_3d", EngineType = GpuEngineType.ThreeD, UtilizationPercent = 50.0
            });
            samples.Add(new GpuProcessSample
            {
                TimestampUtc = ts.AddSeconds(i * 2), Pid = 100, ProcessName = "player.exe",
                EngineName = "eng_vdec", EngineType = GpuEngineType.VideoDecode, UtilizationPercent = 20.0
            });
        }

        await _db.InsertGpuProcessSamplesAsync(samples);

        var result = await _db.GetGpuProcessAggregatesAsync(new[] { (ts.AddSeconds(-1), ts.AddSeconds(20)) });

        var aggregate = Assert.Single(result);
        // Video sum is 5 × 20 = 100 over 10 engine rows → per-row average 10.
        // Pre-fix the raw sum (100) came back, scaling with the window length.
        Assert.Equal(10.0, aggregate.VideoActivityPercent, 6);
        Assert.Equal(35.0, aggregate.AvgUtilizationPercent, 6); // (5×50 + 5×20) / 10
        Assert.Equal(50.0, aggregate.MaxUtilizationPercent);
    }

    private async Task<long> ScalarLongAsync(string sql)
        => await ScalarLongAsync(_testDbPath, sql);

    private static async Task<long> ScalarLongAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var cmd = new SqliteCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}

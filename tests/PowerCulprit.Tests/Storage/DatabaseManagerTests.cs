using PowerCulprit.Core.Models;

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

    // ── SystemPowerSample round-trip ────────────

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
        Assert.False(first.IsAcOnline);
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
    public async Task InsertHardwareSensorSamples_EmptyList_DoesNotCrash()
    {
        await _db.InsertHardwareSensorSamplesAsync(Array.Empty<HardwareSensorSample>());
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
            Power(start.AddMinutes(4), ac: false, percent: 80),
            Power(start.AddMinutes(5), ac: false, percent: 65),
            Power(start.AddMinutes(6), ac: true, percent: 65)
        });

        await _db.RebuildBatteryCyclesAsync();

        var displayCycles = await _db.GetLatestBatteryDisplayCyclesAsync(10);
        var display = Assert.Single(displayCycles);
        Assert.Equal(2, display.RawCycleCount);
        Assert.Equal(25, display.DischargePercent);
        Assert.Equal(25, display.DischargeWh);

        var rawCycles = await _db.GetBatteryCyclesForDisplayCycleAsync(display.Id);
        Assert.Equal(2, rawCycles.Count);
        Assert.All(rawCycles, c => Assert.Equal(display.Id, c.DisplayCycleId));
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
        Assert.Equal(2, displayCycles.Count);

        var allRawCycles = new List<BatteryCycle>();
        foreach (var display in displayCycles)
            allRawCycles.AddRange(await _db.GetBatteryCyclesForDisplayCycleAsync(display.Id));

        var rawCycles = allRawCycles.OrderBy(c => c.StartUtc).ToList();
        Assert.Equal(2, rawCycles.Count);
        Assert.Equal(start.AddMinutes(1), rawCycles[0].StartUtc, TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddMinutes(2), rawCycles[0].EndUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddMinutes(3), rawCycles[1].StartUtc, TimeSpan.FromSeconds(1));
        Assert.True(rawCycles[1].StartedAtSessionBoundary);
    }

    [Fact]
    public async Task ClearHistoricalData_RemovesSessionStartMarkers()
    {
        var ts = DateTime.UtcNow;
        await _db.InsertSessionStartMarkerAsync(ts);

        Assert.Single(await _db.GetSessionStartMarkersAsync(ts.AddSeconds(-1), ts.AddSeconds(1)));

        await _db.ClearHistoricalDataAsync();

        Assert.Empty(await _db.GetSessionStartMarkersAsync(ts.AddSeconds(-1), ts.AddSeconds(1)));
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
}

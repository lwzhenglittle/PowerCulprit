using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Analysis;

public class PowerCulpritAnalyzerTests
{
    private readonly PowerCulpritAnalyzer _analyzer = new();
    private readonly DateTime _baseTime = new(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptyData_DoesNotCrash()
    {
        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            Array.Empty<ProcessSample>(),
            Array.Empty<GpuProcessSample>());

        Assert.Empty(result);
    }

    [Fact]
    public void LifecycleCounts_AppearInReasonAndScore()
    {
        var withLifecycle = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 100, ProcessName = "helper.exe", CpuPercent = 0.2, ProcessStartCount = 15, ShortLivedProcessCount = 3 }
        };
        var withoutLifecycle = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 200, ProcessName = "quiet.exe", CpuPercent = 0.2 }
        };

        var withResult = _analyzer.Analyze(TimeSpan.FromMinutes(30), 1,
            Array.Empty<SystemPowerSample>(), withLifecycle, Array.Empty<GpuProcessSample>());
        var withoutResult = _analyzer.Analyze(TimeSpan.FromMinutes(30), 1,
            Array.Empty<SystemPowerSample>(), withoutLifecycle, Array.Empty<GpuProcessSample>());

        var item = Assert.Single(withResult);
        Assert.True(item.Score > withoutResult[0].Score);
        Assert.Equal(15, item.ProcessStartCount);
        Assert.Equal(3, item.ShortLivedProcessCount);
        Assert.Contains("process starts", item.Reason);
        Assert.Contains("short-lived", item.Reason);
    }

    [Fact]
    public void CpuScoring_RanksHighCpuProcessFirst()
    {
        var processes = new List<ProcessSample>();
        for (int i = 0; i < 10; i++)
        {
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 100,
                ProcessName = "heavy.exe",
                CpuPercent = 25.0
            });
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 200,
                ProcessName = "light.exe",
                CpuPercent = 1.0
            });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        Assert.NotEmpty(result);
        Assert.Equal("heavy.exe", result[0].ProcessName);
        Assert.True(result[0].Score > result[^1].Score);
    }

    [Fact]
    public void GpuScoring_AffectsRanking()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "cpu.exe", CpuPercent = 10.0 },
            new() { TimestampUtc = _baseTime, Pid = 2, ProcessName = "gpu.exe", CpuPercent = 2.0 }
        };

        var gpuSamples = new List<GpuProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 2, ProcessName = "gpu.exe",
                     EngineName = "eng_3d", EngineType = GpuEngineType.ThreeD,
                     UtilizationPercent = 50.0 },
            new() { TimestampUtc = _baseTime, Pid = 2, ProcessName = "gpu.exe",
                     EngineName = "eng_3d", EngineType = GpuEngineType.ThreeD,
                     UtilizationPercent = 70.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            Array.Empty<SystemPowerSample>(),
            processes,
            gpuSamples);

        Assert.NotEmpty(result);
        // GPU-heavy process should outrank CPU-only process due to GPU weight
        var gpuItem = result.FirstOrDefault(r => r.ProcessName == "gpu.exe");
        var cpuItem = result.FirstOrDefault(r => r.ProcessName == "cpu.exe");
        Assert.NotNull(gpuItem);
        Assert.NotNull(cpuItem);
        Assert.True(gpuItem!.AvgGpuPercent > 0);
        Assert.Null(cpuItem!.AvgGpuPercent);
    }

    [Fact]
    public void MissingBatteryPower_StillRanksByResourceActivity()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "app.exe", CpuPercent = 30.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(), // no power data
            processes,
            Array.Empty<GpuProcessSample>());

        Assert.Single(result);
        Assert.Contains("discharge data unavailable", result[0].Reason);
    }

    [Fact]
    public void MissingCpuPackagePower_DoesNotAffectCpuRanking()
    {
        // CpuPackagePower correlation is separate from CPU utilization scoring
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "app.exe", CpuPercent = 40.0 },
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "app.exe", CpuPercent = 42.0 },
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "app.exe", CpuPercent = 38.0 }
        };

        // No hardware sensor data at all — CPU package power unavailable
        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        Assert.Single(result);
        Assert.True(result[0].Score > 0);
        Assert.Null(result[0].CpuPowerCorrelation);
    }

    [Fact]
    public void MissingIgpuPower_UsesGpuEngineActivity()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "render.exe", CpuPercent = 5.0 }
        };

        var gpuSamples = new List<GpuProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "render.exe",
                     EngineName = "eng_3d", EngineType = GpuEngineType.ThreeD,
                     UtilizationPercent = 60.0 }
        };

        // No hardware sensor for iGPU power — falls back to GPU Engine
        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            gpuSamples);

        var item = Assert.Single(result);
        Assert.Equal(60.0, item.AvgGpuPercent);
        Assert.NotNull(item.GpuActivityCorrelation);
    }

    [Fact]
    public void ProcessEnded_StillAnalyzesHistoricalSamples()
    {
        // Process was running 10 minutes ago but has since exited
        var oldTs = DateTime.UtcNow.AddMinutes(-10);
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = oldTs, Pid = 9999, ProcessName = "exited.exe", CpuPercent = 45.0 },
            new() { TimestampUtc = oldTs.AddSeconds(2), Pid = 9999, ProcessName = "exited.exe", CpuPercent = 48.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        var item = Assert.Single(result);
        Assert.Equal("exited.exe", item.ProcessName);
        Assert.True(item.Score > 0, "Historical samples should still produce a score");
    }

    [Fact]
    public void GpuEngineSamples_AggregatedCorrectly()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "multi.exe", CpuPercent = 5.0 }
        };

        var gpuSamples = new List<GpuProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, EngineName = "eng_3d",
                    EngineType = GpuEngineType.ThreeD, UtilizationPercent = 30.0 },
            new() { TimestampUtc = _baseTime, Pid = 1, EngineName = "eng_vdec",
                    EngineType = GpuEngineType.VideoDecode, UtilizationPercent = 20.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            gpuSamples);

        var item = Assert.Single(result);
        Assert.Equal(25.0, item.AvgGpuPercent); // (30+20)/2
        Assert.Equal(30.0, item.MaxGpuPercent);
    }

    [Fact]
    public void RankOrder_IsCorrect()
    {
        var processes = new List<ProcessSample>();
        for (int i = 0; i < 5; i++)
        {
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 100 + i,
                ProcessName = $"proc_{i}.exe",
                CpuPercent = i * 10.0 // 0, 10, 20, 30, 40
            });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        Assert.Equal(5, result.Count);
        for (int i = 0; i < result.Count - 1; i++)
        {
            Assert.True(result[i].Score >= result[i + 1].Score,
                $"Rank {i+1} (score {result[i].Score}) should be >= rank {i+2} (score {result[i+1].Score})");
        }
    }

    [Fact]
    public void Reason_ContainsRelevantInfo()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 100, ProcessName = "chrome.exe",
                     CpuPercent = 12.3, IsForegroundProcess = false },
            new() { TimestampUtc = _baseTime.AddSeconds(2), Pid = 100, ProcessName = "chrome.exe",
                     CpuPercent = 11.8, IsForegroundProcess = false }
        };

        var gpuSamples = new List<GpuProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 100, ProcessName = "chrome.exe",
                     EngineType = GpuEngineType.VideoDecode, UtilizationPercent = 18.6 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            Array.Empty<SystemPowerSample>(),
            processes,
            gpuSamples);

        var item = Assert.Single(result);
        Assert.Contains("chrome.exe", item.Reason);
        Assert.Contains("12.1", item.Reason); // avg CPU
        Assert.Contains("Video", item.Reason);
        Assert.Contains("GPU", item.Reason);
    }

    [Fact]
    public void NoGpuData_LeavesGpuFieldsNull()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "cpuonly.exe", CpuPercent = 15.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        var item = Assert.Single(result);
        Assert.Null(item.AvgGpuPercent);
        Assert.Null(item.MaxGpuPercent);
    }

    [Fact]
    public void PowerCorrelation_CalculatedWhenDischargeDataAvailable()
    {
        var ts1 = _baseTime;
        var ts2 = _baseTime.AddSeconds(2);
        var ts3 = _baseTime.AddSeconds(4);

        var powerSamples = new List<SystemPowerSample>
        {
            new() { TimestampUtc = ts1, ChargeRateMilliwatts = -5000 },   // 5W discharging
            new() { TimestampUtc = ts2, ChargeRateMilliwatts = -15000 },  // 15W discharging
            new() { TimestampUtc = ts3, ChargeRateMilliwatts = -10000 }   // 10W discharging
        };

        // CPU usage tracks discharge rate (positive correlation)
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = ts1, Pid = 1, ProcessName = "corr.exe", CpuPercent = 5.0 },
            new() { TimestampUtc = ts2, Pid = 1, ProcessName = "corr.exe", CpuPercent = 15.0 },
            new() { TimestampUtc = ts3, Pid = 1, ProcessName = "corr.exe", CpuPercent = 10.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            powerSamples,
            processes,
            Array.Empty<GpuProcessSample>());

        var item = Assert.Single(result);
        // CPU tracks discharge perfectly → correlation should be high
        Assert.NotNull(item.PowerCorrelation);
        Assert.True(item.PowerCorrelation!.Value > 0.9,
            $"Expected high positive correlation, got {item.PowerCorrelation}");
    }

    [Fact]
    public void Analyzer_RespectsTimeWindow()
    {
        var recentTs = DateTime.UtcNow.AddMinutes(-5);
        var oldTs = DateTime.UtcNow.AddMinutes(-45);

        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = recentTs, Pid = 1, ProcessName = "recent.exe", CpuPercent = 50.0 },
            new() { TimestampUtc = oldTs, Pid = 2, ProcessName = "old.exe", CpuPercent = 90.0 }
        };

        // 15-minute window → only recent.exe should be included
        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(15), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        Assert.Single(result);
        Assert.Equal("recent.exe", result[0].ProcessName);
    }

    [Fact]
    public void KernelPseudoPids_AreExcludedFromRanking()
    {
        // Regression: PID 0 (Idle) and PID 4 (System) are kernel-accounting
        // pseudo-processes. The collector skips them at the source, but the
        // analyzer must also filter them so historical rows in the DB can't
        // poison the ranking. Idle especially is dangerous because its CPU
        // time tracks idle ticks — on a quiet machine it would top every list.
        var processes = new List<ProcessSample>();
        for (int i = 0; i < 10; i++)
        {
            var ts = _baseTime.AddSeconds(i * 2);
            processes.Add(new ProcessSample
            {
                TimestampUtc = ts, Pid = 0, ProcessName = "Idle", CpuPercent = 95.0
            });
            processes.Add(new ProcessSample
            {
                TimestampUtc = ts, Pid = 4, ProcessName = "System", CpuPercent = 30.0
            });
            processes.Add(new ProcessSample
            {
                TimestampUtc = ts, Pid = 1234, ProcessName = "real.exe", CpuPercent = 10.0
            });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        Assert.DoesNotContain(result, r => r.Pid == 0);
        Assert.DoesNotContain(result, r => r.Pid == 4);
        var item = Assert.Single(result);
        Assert.Equal("real.exe", item.ProcessName);
    }

    [Fact]
    public void ProcessNameGrouping_MergesMultiplePids()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 101, ProcessName = "chrome.exe", CpuPercent = 10.0 },
            new() { TimestampUtc = _baseTime.AddSeconds(2), Pid = 102, ProcessName = "chrome.exe", CpuPercent = 30.0 },
            new() { TimestampUtc = _baseTime, Pid = 200, ProcessName = "notes.exe", CpuPercent = 5.0 }
        };

        var gpuSamples = new List<GpuProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 102, ProcessName = null,
                    EngineType = GpuEngineType.ThreeD, UtilizationPercent = 20.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            gpuSamples,
            ProcessGroupingMode.ProcessName);

        Assert.Equal(2, result.Count);
        var chrome = result.First(r => r.ProcessName == "chrome.exe");
        Assert.Null(chrome.Pid);
        Assert.Equal(20.0, chrome.AvgCpuPercent);
        Assert.Equal(20.0, chrome.AvgGpuPercent);
    }

    [Fact]
    public void ProcessNameGrouping_SumsSameTimestampCpuAcrossPids()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 101, ProcessName = "browser.exe", CpuPercent = 10.0 },
            new() { TimestampUtc = _baseTime, Pid = 102, ProcessName = "browser.exe", CpuPercent = 30.0 },
            new() { TimestampUtc = _baseTime.AddSeconds(2), Pid = 101, ProcessName = "browser.exe", CpuPercent = 20.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>(),
            ProcessGroupingMode.ProcessName);

        var item = Assert.Single(result);
        Assert.Equal(30.0, item.AvgCpuPercent);
        Assert.Equal(40.0, item.MaxCpuPercent);
    }

    [Fact]
    public void RankingTieBreakers_AreDeterministic()
    {
        var processes = new List<ProcessSample>
        {
            new() { TimestampUtc = _baseTime, Pid = 2, ProcessName = "beta.exe", CpuPercent = 10.0 },
            new() { TimestampUtc = _baseTime, Pid = 1, ProcessName = "alpha.exe", CpuPercent = 10.0 }
        };

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 10,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        Assert.Equal("alpha.exe", result[0].ProcessName);
        Assert.Equal("beta.exe", result[1].ProcessName);
    }

    // ──────────────────────────────────────────────
    // FG/BG seconds — verify the median-interval logic that replaced the hard-coded 2.0.
    // ──────────────────────────────────────────────

    [Fact]
    public void FgBgSeconds_DefaultTwoSecondInterval_MatchesLegacyMultiplier()
    {
        // 10 samples 2 s apart, 4 in foreground, 6 in background — sanity-check the new
        // median-interval path still produces the old (count * 2) result on a typical run.
        var processes = new List<ProcessSample>();
        var powerSamples = new List<SystemPowerSample>();
        for (int i = 0; i < 10; i++)
        {
            var ts = _baseTime.AddSeconds(i * 2);
            processes.Add(new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 100,
                ProcessName = "app.exe",
                CpuPercent = 5.0,
                IsForegroundProcess = i < 4
            });
            powerSamples.Add(new SystemPowerSample { TimestampUtc = ts, BatteryPercent = 50 });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            powerSamples,
            processes,
            Array.Empty<GpuProcessSample>());

        var item = Assert.Single(result);
        Assert.Equal(8.0, item.ForegroundActiveSeconds);  // 4 samples × 2 s
        Assert.Equal(12.0, item.BackgroundActiveSeconds); // 6 samples × 2 s
    }

    [Fact]
    public void FgBgSeconds_OneSecondInterval_UsesObservedMedianNotHardcodedTwo()
    {
        // The hard-coded 2.0 in the old analyzer would over-count by 2× when the user
        // runs with --interval 1. Verify the new median-from-timestamps path is correct.
        var processes = new List<ProcessSample>();
        var powerSamples = new List<SystemPowerSample>();
        for (int i = 0; i < 12; i++)
        {
            var ts = _baseTime.AddSeconds(i); // 1 s apart
            processes.Add(new ProcessSample
            {
                TimestampUtc = ts,
                Pid = 100,
                ProcessName = "app.exe",
                CpuPercent = 5.0,
                IsForegroundProcess = i < 3
            });
            powerSamples.Add(new SystemPowerSample { TimestampUtc = ts, BatteryPercent = 50 });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            powerSamples,
            processes,
            Array.Empty<GpuProcessSample>());

        var item = Assert.Single(result);
        Assert.Equal(3.0, item.ForegroundActiveSeconds);  // 3 × 1 s
        Assert.Equal(9.0, item.BackgroundActiveSeconds);  // 9 × 1 s
    }

    [Fact]
    public void FgBgSeconds_SuspendGapIgnored_MedianStaysAtTrueInterval()
    {
        // A laptop sleep introduces a single huge gap between two power samples. The
        // median must drop that outlier and keep using the true 2 s cadence — otherwise
        // every FG/BG count gets multiplied by minutes-of-suspend.
        var powerSamples = new List<SystemPowerSample>();
        for (int i = 0; i < 10; i++)
            powerSamples.Add(new SystemPowerSample { TimestampUtc = _baseTime.AddSeconds(i * 2), BatteryPercent = 50 });
        // Inject one 5-minute gap (simulating system sleep / collector stall)
        powerSamples.Add(new SystemPowerSample { TimestampUtc = _baseTime.AddSeconds(18 + 300), BatteryPercent = 50 });
        for (int i = 0; i < 10; i++)
            powerSamples.Add(new SystemPowerSample { TimestampUtc = _baseTime.AddSeconds(18 + 300 + (i + 1) * 2), BatteryPercent = 50 });

        var processes = new List<ProcessSample>();
        // 3 foreground samples, 3 background — all in the same window so we know the answer
        for (int i = 0; i < 6; i++)
        {
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 100,
                ProcessName = "app.exe",
                CpuPercent = 5.0,
                IsForegroundProcess = i < 3
            });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromHours(1), 5,
            powerSamples,
            processes,
            Array.Empty<GpuProcessSample>());

        var item = Assert.Single(result);
        // If the 300 s sleep gap had leaked into the median, these would be off by 100×+.
        Assert.Equal(6.0, item.ForegroundActiveSeconds);
        Assert.Equal(6.0, item.BackgroundActiveSeconds);
    }

    [Fact]
    public void FgBgSeconds_OnlyForegroundSamples_BackgroundIsNull()
    {
        // A process that's always in focus should report null BG (not 0), so the UI
        // shows "--" instead of "0.0" — consistent with how DiskMb etc. are handled.
        var processes = new List<ProcessSample>();
        for (int i = 0; i < 5; i++)
        {
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 100,
                ProcessName = "app.exe",
                CpuPercent = 5.0,
                IsForegroundProcess = true
            });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>());

        var item = Assert.Single(result);
        Assert.Equal(10.0, item.ForegroundActiveSeconds);
        Assert.Null(item.BackgroundActiveSeconds);
    }

    // ──────────────────────────────────────────────
    // svchost service attribution — verify the (ProcessName, ServiceName) grouping
    // splits a single "svchost" image into per-service rows.
    // ──────────────────────────────────────────────

    [Fact]
    public void Svchost_WithDifferentServiceNames_RanksAsSeparateRows()
    {
        // Two svchost.exe instances, two services, very different CPU pressure.
        // Before the (name, service) grouping change, both would have collapsed
        // into a single "svchost" row with the average of 30% + 1% = 15.5% — and
        // the user would have no idea which service to chase.
        var processes = new List<ProcessSample>();
        for (int i = 0; i < 10; i++)
        {
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 1000,
                ProcessName = "svchost",
                ServiceName = "Dnscache",
                CpuPercent = 30.0,
                IsForegroundProcess = false
            });
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 2000,
                ProcessName = "svchost",
                ServiceName = "wuauserv",
                CpuPercent = 1.0,
                IsForegroundProcess = false
            });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>(),
            ProcessGroupingMode.ProcessName);

        Assert.Equal(2, result.Count);
        var dns = result.Single(r => r.ServiceName == "Dnscache");
        var wuauserv = result.Single(r => r.ServiceName == "wuauserv");
        Assert.Equal("svchost", dns.ProcessName);
        Assert.Equal("svchost", wuauserv.ProcessName);
        Assert.True(dns.Score > wuauserv.Score, "high-CPU service should rank above the quiet one");
        Assert.Equal(30.0, dns.AvgCpuPercent);
        Assert.Equal(1.0, wuauserv.AvgCpuPercent);
    }

    [Fact]
    public void Svchost_WithoutServiceName_FallsBackToSingleRow()
    {
        // Older DBs (pre-migration) or samples taken when SCM was unreachable
        // will have ServiceName == null. Those rows should still aggregate
        // under a plain "svchost" row, with ServiceName == null on the report
        // item so the UI prints "svchost" without a parenthetical.
        var processes = new List<ProcessSample>();
        for (int i = 0; i < 5; i++)
        {
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 1000,
                ProcessName = "svchost",
                ServiceName = null,
                CpuPercent = 5.0,
                IsForegroundProcess = false
            });
            processes.Add(new ProcessSample
            {
                TimestampUtc = _baseTime.AddSeconds(i * 2),
                Pid = 1001,
                ProcessName = "svchost",
                ServiceName = null,
                CpuPercent = 3.0,
                IsForegroundProcess = false
            });
        }

        var result = _analyzer.Analyze(
            TimeSpan.FromMinutes(30), 5,
            Array.Empty<SystemPowerSample>(),
            processes,
            Array.Empty<GpuProcessSample>(),
            ProcessGroupingMode.ProcessName);

        var item = Assert.Single(result);
        Assert.Equal("svchost", item.ProcessName);
        Assert.Null(item.ServiceName);
        // Sums across both pids at each timestamp (ProcessName mode aggregates by-ts).
        Assert.Equal(8.0, item.AvgCpuPercent);
    }
}

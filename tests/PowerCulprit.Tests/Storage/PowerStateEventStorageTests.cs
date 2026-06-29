using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Storage;

public class PowerStateEventStorageTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly PowerCulprit.Storage.DatabaseManager _db;

    public PowerStateEventStorageTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"powerculprit_pse_test_{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task InsertAndQuery_RoundTrips()
    {
        await _db.InitializeAsync();

        var suspendEvent = new PowerStateEvent
        {
            TimestampUtc = new DateTime(2026, 5, 1, 22, 0, 0, DateTimeKind.Utc),
            Kind = PowerStateEventKind.Suspend,
            Source = "PowerCulprit",
            Details = "PBT_APMSUSPEND"
        };

        var resumeEvent = new PowerStateEvent
        {
            TimestampUtc = new DateTime(2026, 5, 1, 22, 5, 0, DateTimeKind.Utc),
            Kind = PowerStateEventKind.Resume,
            Source = "PowerCulprit",
            Details = "PBT_APMRESUMESUSPEND"
        };

        await _db.InsertPowerStateEventAsync(suspendEvent);
        await _db.InsertPowerStateEventAsync(resumeEvent);

        var results = await _db.GetPowerStateEventsAsync(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, e => e.Kind == PowerStateEventKind.Suspend);
        Assert.Contains(results, e => e.Kind == PowerStateEventKind.Resume);
    }

    [Fact]
    public async Task Query_FiltersByTimeRange()
    {
        await _db.InitializeAsync();

        await _db.InsertPowerStateEventAsync(new PowerStateEvent
        {
            TimestampUtc = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            Kind = PowerStateEventKind.Suspend
        });

        await _db.InsertPowerStateEventAsync(new PowerStateEvent
        {
            TimestampUtc = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc),
            Kind = PowerStateEventKind.Resume
        });

        // Only the first event should be in range.
        var results = await _db.GetPowerStateEventsAsync(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(results);
        Assert.Equal(PowerStateEventKind.Suspend, results[0].Kind);
    }

    [Fact]
    public async Task Cleanup_RemovesOldEvents()
    {
        await _db.InitializeAsync();

        var oldEvent = new PowerStateEvent
        {
            TimestampUtc = DateTime.UtcNow.AddDays(-10),
            Kind = PowerStateEventKind.Suspend
        };

        var recentEvent = new PowerStateEvent
        {
            TimestampUtc = DateTime.UtcNow.AddHours(-1),
            Kind = PowerStateEventKind.Resume
        };

        await _db.InsertPowerStateEventAsync(oldEvent);
        await _db.InsertPowerStateEventAsync(recentEvent);

        // Cleanup with 7-day retention should delete the 10-day-old event.
        await _db.CleanupOldDataAsync(retentionDays: 7);

        var results = await _db.GetPowerStateEventsAsync(
            DateTime.UtcNow.AddDays(-14),
            DateTime.UtcNow.AddDays(1));

        Assert.Single(results);
        Assert.Equal(PowerStateEventKind.Resume, results[0].Kind);
    }

    [Fact]
    public async Task ClearHistory_RemovesAllEvents()
    {
        await _db.InitializeAsync();

        await _db.InsertPowerStateEventAsync(new PowerStateEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Kind = PowerStateEventKind.Suspend
        });

        await _db.InsertPowerStateEventAsync(new PowerStateEvent
        {
            TimestampUtc = DateTime.UtcNow.AddMinutes(1),
            Kind = PowerStateEventKind.Resume
        });

        await _db.ClearHistoricalDataAsync();

        var results = await _db.GetPowerStateEventsAsync(
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddDays(1));

        Assert.Empty(results);
    }

    [Fact]
    public async Task ResumeAutomatic_StoredCorrectly()
    {
        await _db.InitializeAsync();

        await _db.InsertPowerStateEventAsync(new PowerStateEvent
        {
            TimestampUtc = new DateTime(2026, 5, 1, 2, 0, 0, DateTimeKind.Utc),
            Kind = PowerStateEventKind.ResumeAutomatic,
            Details = "PBT_APMRESUMEAUTOMATIC"
        });

        var results = await _db.GetPowerStateEventsAsync(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(results);
        Assert.Equal(PowerStateEventKind.ResumeAutomatic, results[0].Kind);
        Assert.Equal("PBT_APMRESUMEAUTOMATIC", results[0].Details);
    }
}

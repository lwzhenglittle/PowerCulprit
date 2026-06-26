using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>No-op ETW collector used by tests or fallback composition.</summary>
public sealed class NoOpWindowsEtwActivityCollector : IWindowsEtwActivityCollector
{
    private SourceStatus _status = new()
    {
        TimestampUtc = DateTime.UtcNow,
        SourceName = "WindowsETW",
        IsAvailable = false,
        Status = "Disabled",
        Details = "ETW collector is disabled",
        RequiresAdmin = false
    };

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _status = _status with { TimestampUtc = DateTime.UtcNow };
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _status = _status with { TimestampUtc = DateTime.UtcNow };
        return Task.CompletedTask;
    }

    public EtwProcessActivitySnapshot SnapshotAndReset(DateTime nowUtc) => EtwProcessActivitySnapshot.Empty with
    {
        TimestampUtc = nowUtc
    };

    public void RecordPolledProcess(int pid)
    {
    }

    public SourceStatus GetStatus() => _status with { TimestampUtc = DateTime.UtcNow };
}

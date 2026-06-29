using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

public sealed class NoOpWmiActivityCollector : IWmiActivityCollector
{
    public void SetLastRecordId(long recordId)
    {
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StopAsync()
        => Task.CompletedTask;

    public WmiActivitySnapshot SnapshotAndReset(DateTime nowUtc)
        => WmiActivitySnapshot.Empty with { TimestampUtc = nowUtc };

    public SourceStatus GetStatus()
        => new()
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = WmiActivityCollector.SourceName,
            IsAvailable = false,
            Status = "Disabled",
            Details = "WMI Activity collection is disabled",
            RequiresAdmin = false
        };
}

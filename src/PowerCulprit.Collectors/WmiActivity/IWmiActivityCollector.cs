using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

public interface IWmiActivityCollector
{
    void SetLastRecordId(long recordId);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    WmiActivitySnapshot SnapshotAndReset(DateTime nowUtc);

    SourceStatus GetStatus();
}

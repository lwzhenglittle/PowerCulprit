using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Abstraction for optional Windows ETW process/network activity collection.
/// Implementations must degrade to empty snapshots when ETW is unavailable.
/// </summary>
public interface IWindowsEtwActivityCollector
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    EtwProcessActivitySnapshot SnapshotAndReset(DateTime nowUtc);

    void RecordPolledProcess(int pid);

    SourceStatus GetStatus();
}

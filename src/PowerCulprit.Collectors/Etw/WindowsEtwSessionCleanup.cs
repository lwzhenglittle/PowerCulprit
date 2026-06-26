using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace PowerCulprit.Collectors;

internal sealed record EtwSessionCleanupResult(
    int Found,
    int Stopped,
    int Failed,
    IReadOnlyList<string> FailedSessionNames);

internal interface IEtwSessionController
{
    IReadOnlyList<string> GetActiveSessionNames();

    void StopSession(string sessionName);
}

internal sealed class TraceEventSessionController : IEtwSessionController
{
    public IReadOnlyList<string> GetActiveSessionNames()
        => TraceEventSession.GetActiveSessionNames();

    public void StopSession(string sessionName)
    {
        using var session = TraceEventSession.GetActiveSession(sessionName);
        session.Stop();
    }
}

internal static partial class WindowsEtwSessionCleanup
{
    public static EtwSessionCleanupResult StopPreviousPowerCulpritSessions(
        ILogger logger)
        => StopPreviousPowerCulpritSessions(new TraceEventSessionController(), logger);

    internal static EtwSessionCleanupResult StopPreviousPowerCulpritSessions(
        IEtwSessionController controller,
        ILogger logger)
    {
        IReadOnlyList<string> sessionNames;
        try
        {
            sessionNames = controller.GetActiveSessionNames();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate ETW sessions before startup");
            return new EtwSessionCleanupResult(0, 0, 1, Array.Empty<string>());
        }

        var found = 0;
        var stopped = 0;
        var failed = new List<string>();

        foreach (var sessionName in sessionNames)
        {
            if (!IsPowerCulpritSessionName(sessionName))
                continue;

            found++;
            try
            {
                controller.StopSession(sessionName);
                stopped++;
                logger.LogInformation("Stopped previous PowerCulprit ETW session {SessionName}", sessionName);
            }
            catch (Exception ex)
            {
                failed.Add(sessionName);
                logger.LogWarning(ex, "Failed to stop previous PowerCulprit ETW session {SessionName}", sessionName);
            }
        }

        return new EtwSessionCleanupResult(found, stopped, failed.Count, failed);
    }

    internal static bool IsPowerCulpritSessionName(string sessionName)
        => PowerCulpritEtwSessionNameRegex().IsMatch(sessionName) ||
           PowerCulpritEtwProbeSessionNameRegex().IsMatch(sessionName);

    [GeneratedRegex("^PowerCulprit-ETW-\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerCulpritEtwSessionNameRegex();

    [GeneratedRegex("^PowerCulprit-ETW-Probe-\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerCulpritEtwProbeSessionNameRegex();
}

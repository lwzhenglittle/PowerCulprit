using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>Merges ETW activity windows into the existing process polling samples.</summary>
public static class ProcessEtwMerger
{
    private static readonly TimeSpan MinimumValidInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumValidInterval = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<ProcessSample> Merge(
        IReadOnlyList<ProcessSample> processSamples,
        EtwProcessActivitySnapshot etwSnapshot)
    {
        if (processSamples.Count == 0 || etwSnapshot.Activities.Count == 0)
            return processSamples;

        var interval = etwSnapshot.Interval;
        var canComputeRates = interval >= MinimumValidInterval && interval <= MaximumValidInterval;
        var seconds = interval.TotalSeconds;
        var byPid = etwSnapshot.Activities
            .Where(a => a.Pid > 0)
            .GroupBy(a => a.Pid)
            .ToDictionary(g => g.Key, MergeActivities);
        foreach (var sample in processSamples)
        {
            if (sample.Pid > 0 && byPid.TryGetValue(sample.Pid, out var activity))
                byPid[sample.Pid] = activity with { WasObservedInPoll = true };
        }
        var shortLivedByName = etwSnapshot.Activities
            .Where(a => a.IsShortLived && !string.IsNullOrWhiteSpace(a.Metadata?.ProcessName))
            .GroupBy(a => a.Metadata!.ProcessName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var changed = false;
        var merged = new ProcessSample[processSamples.Count];
        for (var i = 0; i < processSamples.Count; i++)
        {
            var sample = processSamples[i];
            byPid.TryGetValue(sample.Pid, out var activity);

            double? receiveRate = sample.NetworkReceiveBytesPerSecond;
            double? sendRate = sample.NetworkSendBytesPerSecond;
            int? processStartCount = null;
            int? processStopCount = null;
            if (activity is not null)
            {
                if (canComputeRates && activity.HasNetworkActivity)
                {
                    if (activity.NetworkReceiveBytes > 0)
                        receiveRate = activity.NetworkReceiveBytes / seconds;
                    if (activity.NetworkSendBytes > 0)
                        sendRate = activity.NetworkSendBytes / seconds;
                }

                processStartCount = activity.ProcessStartCount > 0 ? checked((int)Math.Min(activity.ProcessStartCount, int.MaxValue)) : null;
                processStopCount = activity.ProcessStopCount > 0 ? checked((int)Math.Min(activity.ProcessStopCount, int.MaxValue)) : null;
            }

            var metadata = activity?.Metadata;
            var updated = sample with
            {
                NetworkReceiveBytesPerSecond = receiveRate,
                NetworkSendBytesPerSecond = sendRate,
                ProcessStartCount = processStartCount,
                ProcessStopCount = processStopCount,
                ShortLivedProcessCount = shortLivedByName.TryGetValue(sample.ProcessName, out var shortLivedCount) && shortLivedCount > 0 ? shortLivedCount : null,
                ParentPid = sample.ParentPid ?? metadata?.ParentPid
            };

            // ETW metadata can be stale after PID reuse. Only fill low-risk display
            // fields when the polled sample did not already have them.
            if (metadata is not null)
            {
                updated = updated with
                {
                    ProcessName = string.IsNullOrWhiteSpace(sample.ProcessName)
                        ? metadata.ProcessName ?? sample.ProcessName
                        : sample.ProcessName,
                    ExecutablePath = sample.ExecutablePath ?? metadata.ImagePath,
                    CommandLine = sample.CommandLine ?? metadata.CommandLine
                };
            }

            merged[i] = updated;
            changed |= !ReferenceEquals(updated, sample) && !Equals(updated, sample);
        }

        return changed ? merged : processSamples;
    }

    private static EtwProcessActivity MergeActivities(IEnumerable<EtwProcessActivity> activities)
    {
        EtwProcessMetadata? metadata = null;
        long receive = 0;
        long send = 0;
        long starts = 0;
        long stops = 0;
        var pid = 0;

        foreach (var activity in activities)
        {
            pid = activity.Pid;
            receive += activity.NetworkReceiveBytes;
            send += activity.NetworkSendBytes;
            starts += activity.ProcessStartCount;
            stops += activity.ProcessStopCount;
            metadata ??= activity.Metadata;
        }

        return new EtwProcessActivity
        {
            Pid = pid,
            NetworkReceiveBytes = receive,
            NetworkSendBytes = send,
            ProcessStartCount = starts,
            ProcessStopCount = stops,
            WasObservedInPoll = activities.Any(a => a.WasObservedInPoll),
            Metadata = metadata
        };
    }
}

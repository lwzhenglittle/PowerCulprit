namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Subtracts excluded time intervals from source intervals, returning
/// the remaining non-excluded segments. Used to drop sleep/gap intervals
/// from analysis windows.
/// </summary>
public static class IntervalSubtractor
{
    /// <summary>
    /// Minimum duration for a result interval; anything shorter is dropped.
    /// </summary>
    public static readonly TimeSpan MinResultDuration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Subtract <paramref name="excluded"/> intervals from <paramref name="source"/>
    /// intervals. Each source interval is split by any overlapping excluded intervals,
    /// and tiny left-over segments shorter than <see cref="MinResultDuration"/> are
    /// dropped.
    /// </summary>
    public static IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> Exclude(
        IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> source,
        IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> excluded)
    {
        if (excluded.Count == 0)
            return new List<(DateTime, DateTime)>(source);

        var sortedExcluded = excluded
            .OrderBy(e => e.FromUtc)
            .ToList();

        var result = new List<(DateTime FromUtc, DateTime ToUtc)>();

        foreach (var (srcFrom, srcTo) in source)
        {
            result.AddRange(ExcludeSingle(srcFrom, srcTo, sortedExcluded));
        }

        return result;
    }

    private static IEnumerable<(DateTime FromUtc, DateTime ToUtc)> ExcludeSingle(
        DateTime srcFrom,
        DateTime srcTo,
        List<(DateTime FromUtc, DateTime ToUtc)> sortedExcluded)
    {
        var cursor = srcFrom;

        foreach (var (exFrom, exTo) in sortedExcluded)
        {
            if (exTo <= cursor)
                continue; // Exclusion is entirely before the current cursor.

            if (exFrom >= srcTo)
                break; // Remaining exclusions are after this source interval.

            // There is overlap — emit the segment before the exclusion.
            if (exFrom > cursor)
            {
                var segmentEnd = exFrom < srcTo ? exFrom : srcTo;
                if (segmentEnd - cursor >= MinResultDuration)
                    yield return (cursor, segmentEnd);
            }

            // Advance cursor past this exclusion (clamped to source end).
            if (exTo > cursor)
                cursor = exTo < srcTo ? exTo : srcTo;

            if (cursor >= srcTo)
                yield break;
        }

        // Emit any remaining tail.
        if (cursor < srcTo && srcTo - cursor >= MinResultDuration)
            yield return (cursor, srcTo);
    }
}

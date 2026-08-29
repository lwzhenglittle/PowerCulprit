namespace PowerCulprit.Core.Analysis;

public readonly record struct TimeSeriesPoint(DateTime TimestampUtc, double Value);

public enum TimeSeriesReductionMode
{
    MinMax,
    LargestTriangleThreeBuckets
}

/// <summary>
/// Reduces a timestamp-ordered series to a strict point budget while retaining
/// either local extrema or the visually significant shape of the series.
/// </summary>
public static class TimeSeriesReducer
{
    public static IReadOnlyList<TimeSeriesPoint> Reduce(
        IReadOnlyList<TimeSeriesPoint> points,
        int maxPoints,
        TimeSeriesReductionMode mode)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (maxPoints <= 0 || points.Count == 0)
            return Array.Empty<TimeSeriesPoint>();
        if (points.Count <= maxPoints)
            return points;
        if (maxPoints == 1)
            return [points[0]];
        if (maxPoints == 2)
            return [points[0], points[^1]];

        return mode switch
        {
            TimeSeriesReductionMode.MinMax when maxPoints >= 6 => ReduceMinMax(points, maxPoints),
            _ => ReduceLttb(points, maxPoints)
        };
    }

    private static IReadOnlyList<TimeSeriesPoint> ReduceMinMax(
        IReadOnlyList<TimeSeriesPoint> points,
        int maxPoints)
    {
        var bucketCount = Math.Max(1, (maxPoints - 2) / 4);
        var interiorCount = points.Count - 2;
        var result = new List<TimeSeriesPoint>(Math.Min(maxPoints, 2 + bucketCount * 4))
        {
            points[0]
        };

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = 1 + (int)((long)interiorCount * bucket / bucketCount);
            var end = 1 + (int)((long)interiorCount * (bucket + 1) / bucketCount);
            if (start >= end)
                continue;

            var minIndex = start;
            var maxIndex = start;
            for (var index = start + 1; index < end; index++)
            {
                if (points[index].Value < points[minIndex].Value)
                    minIndex = index;
                if (points[index].Value > points[maxIndex].Value)
                    maxIndex = index;
            }

            Span<int> selected = [start, minIndex, maxIndex, end - 1];
            selected.Sort();
            for (var i = 0; i < selected.Length; i++)
            {
                if (i == 0 || selected[i] != selected[i - 1])
                    AddDistinct(result, points[selected[i]]);
            }
        }

        AddDistinct(result, points[^1]);
        return result;
    }

    private static IReadOnlyList<TimeSeriesPoint> ReduceLttb(
        IReadOnlyList<TimeSeriesPoint> points,
        int maxPoints)
    {
        var result = new List<TimeSeriesPoint>(maxPoints) { points[0] };
        var every = (points.Count - 2d) / (maxPoints - 2);
        var firstTimestamp = points[0].TimestampUtc;
        var selectedIndex = 0;

        for (var bucket = 0; bucket < maxPoints - 2; bucket++)
        {
            var averageStart = Math.Min(
                points.Count,
                (int)Math.Floor((bucket + 1) * every) + 1);
            var averageEnd = Math.Min(
                points.Count,
                (int)Math.Floor((bucket + 2) * every) + 1);

            double averageX;
            double averageY;
            if (averageStart < averageEnd)
            {
                averageX = 0;
                averageY = 0;
                for (var index = averageStart; index < averageEnd; index++)
                {
                    averageX += (points[index].TimestampUtc - firstTimestamp).Ticks;
                    averageY += points[index].Value;
                }

                var averageCount = averageEnd - averageStart;
                averageX /= averageCount;
                averageY /= averageCount;
            }
            else
            {
                averageX = (points[^1].TimestampUtc - firstTimestamp).Ticks;
                averageY = points[^1].Value;
            }

            var rangeStart = Math.Min(
                points.Count - 1,
                (int)Math.Floor(bucket * every) + 1);
            var rangeEnd = Math.Min(
                points.Count - 1,
                (int)Math.Floor((bucket + 1) * every) + 1);
            if (rangeEnd <= rangeStart)
                rangeEnd = Math.Min(points.Count - 1, rangeStart + 1);

            var selected = rangeStart;
            var maxArea = double.NegativeInfinity;
            var anchorX = (points[selectedIndex].TimestampUtc - firstTimestamp).Ticks;
            var anchorY = points[selectedIndex].Value;
            for (var index = rangeStart; index < rangeEnd; index++)
            {
                var x = (points[index].TimestampUtc - firstTimestamp).Ticks;
                var area = Math.Abs(
                    (anchorX - averageX) * (points[index].Value - anchorY) -
                    (anchorX - x) * (averageY - anchorY));
                if (area > maxArea)
                {
                    maxArea = area;
                    selected = index;
                }
            }

            result.Add(points[selected]);
            selectedIndex = selected;
        }

        result.Add(points[^1]);
        return result;
    }

    private static void AddDistinct(List<TimeSeriesPoint> points, TimeSeriesPoint point)
    {
        if (points.Count == 0 || points[^1] != point)
            points.Add(point);
    }
}

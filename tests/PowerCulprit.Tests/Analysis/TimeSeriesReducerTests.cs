using PowerCulprit.Core.Analysis;

namespace PowerCulprit.Tests.Analysis;

public sealed class TimeSeriesReducerTests
{
    [Theory]
    [InlineData(TimeSeriesReductionMode.MinMax)]
    [InlineData(TimeSeriesReductionMode.LargestTriangleThreeBuckets)]
    public void Reduce_AlwaysHonorsStrictBudget(TimeSeriesReductionMode mode)
    {
        var points = CreateSeries(10_000, i => Math.Sin(i / 13d));

        var reduced = TimeSeriesReducer.Reduce(points, 257, mode);

        Assert.InRange(reduced.Count, 2, 257);
        Assert.Equal(points[0], reduced[0]);
        Assert.Equal(points[^1], reduced[^1]);
        AssertStrictlyOrdered(reduced);
    }

    [Fact]
    public void MinMax_PreservesInteriorExtrema()
    {
        var points = CreateSeries(1_000, i => i switch
        {
            321 => -500,
            654 => 900,
            _ => Math.Sin(i / 20d)
        });

        var reduced = TimeSeriesReducer.Reduce(points, 66, TimeSeriesReductionMode.MinMax);

        Assert.Contains(reduced, point => point.Value == -500);
        Assert.Contains(reduced, point => point.Value == 900);
        Assert.True(reduced.Count <= 66);
    }

    [Fact]
    public void Lttb_ReturnsRequestedCountAndRetainsSpikeShape()
    {
        var points = CreateSeries(1_000, i => i == 500 ? 1_000 : 0);

        var reduced = TimeSeriesReducer.Reduce(points, 100, TimeSeriesReductionMode.LargestTriangleThreeBuckets);

        Assert.Equal(100, reduced.Count);
        Assert.Contains(reduced, point => point.Value == 1_000);
        AssertStrictlyOrdered(reduced);
    }

    [Fact]
    public void Reduce_DoesNotAllocateWhenSeriesAlreadyFits()
    {
        var points = CreateSeries(10, i => i);

        var reduced = TimeSeriesReducer.Reduce(points, 10, TimeSeriesReductionMode.MinMax);

        Assert.Same(points, reduced);
    }

    private static List<TimeSeriesPoint> CreateSeries(int count, Func<int, double> valueFactory)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(i => new TimeSeriesPoint(start.AddSeconds(i), valueFactory(i)))
            .ToList();
    }

    private static void AssertStrictlyOrdered(IReadOnlyList<TimeSeriesPoint> points)
    {
        for (var i = 1; i < points.Count; i++)
            Assert.True(points[i].TimestampUtc > points[i - 1].TimestampUtc);
    }
}

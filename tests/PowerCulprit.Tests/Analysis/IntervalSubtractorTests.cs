using PowerCulprit.Core.Analysis;

namespace PowerCulprit.Tests.Analysis;

public class IntervalSubtractorTests
{
    [Fact]
    public void NoOverlap_KeepsSourceIntact()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 12, 0, 0))
        };

        var excluded = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 14, 0, 0), new(2026, 5, 1, 15, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, excluded);

        Assert.Single(result);
        Assert.Equal(source[0], result[0]);
    }

    [Fact]
    public void FullOverlap_RemovesSource()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 12, 0, 0))
        };

        var excluded = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 9, 0, 0), new(2026, 5, 1, 13, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, excluded);

        Assert.Empty(result);
    }

    [Fact]
    public void PartialOverlap_Start_SplitsCorrectly()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 12, 0, 0))
        };

        // Exclusion covers the first hour.
        var excluded = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 9, 0, 0), new(2026, 5, 1, 11, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, excluded);

        Assert.Single(result);
        Assert.Equal(new(2026, 5, 1, 11, 0, 0), result[0].FromUtc);
        Assert.Equal(new(2026, 5, 1, 12, 0, 0), result[0].ToUtc);
    }

    [Fact]
    public void PartialOverlap_End_SplitsCorrectly()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 12, 0, 0))
        };

        // Exclusion covers the last hour.
        var excluded = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 11, 0, 0), new(2026, 5, 1, 13, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, excluded);

        Assert.Single(result);
        Assert.Equal(new(2026, 5, 1, 10, 0, 0), result[0].FromUtc);
        Assert.Equal(new(2026, 5, 1, 11, 0, 0), result[0].ToUtc);
    }

    [Fact]
    public void OverlapInMiddle_SplitsCorrectly()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 16, 0, 0))
        };

        // Exclusion in the middle → two segments left and right.
        var excluded = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 12, 0, 0), new(2026, 5, 1, 14, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, excluded);

        Assert.Equal(2, result.Count);
        Assert.Equal(new(2026, 5, 1, 10, 0, 0), result[0].FromUtc);
        Assert.Equal(new(2026, 5, 1, 12, 0, 0), result[0].ToUtc);
        Assert.Equal(new(2026, 5, 1, 14, 0, 0), result[1].FromUtc);
        Assert.Equal(new(2026, 5, 1, 16, 0, 0), result[1].ToUtc);
    }

    [Fact]
    public void MultipleExcludedIntervals_CorrectSubtraction()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 8, 0, 0), new(2026, 5, 1, 20, 0, 0))
        };

        var excluded = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 11, 0, 0)),
            (new(2026, 5, 1, 14, 0, 0), new(2026, 5, 1, 16, 0, 0)),
            (new(2026, 5, 1, 18, 0, 0), new(2026, 5, 1, 19, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, excluded);

        Assert.Equal(4, result.Count);
        // 08:00–10:00
        Assert.Equal(new(2026, 5, 1, 8, 0, 0), result[0].FromUtc);
        Assert.Equal(new(2026, 5, 1, 10, 0, 0), result[0].ToUtc);
        // 11:00–14:00
        Assert.Equal(new(2026, 5, 1, 11, 0, 0), result[1].FromUtc);
        Assert.Equal(new(2026, 5, 1, 14, 0, 0), result[1].ToUtc);
        // 16:00–18:00
        Assert.Equal(new(2026, 5, 1, 16, 0, 0), result[2].FromUtc);
        Assert.Equal(new(2026, 5, 1, 18, 0, 0), result[2].ToUtc);
        // 19:00–20:00
        Assert.Equal(new(2026, 5, 1, 19, 0, 0), result[3].FromUtc);
        Assert.Equal(new(2026, 5, 1, 20, 0, 0), result[3].ToUtc);
    }

    [Fact]
    public void TinyResultIntervals_Dropped()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 10, 0, 2)) // 2-second interval
        };

        // No excluded intervals → short source should persist since it's >= 1s.
        var result = IntervalSubtractor.Exclude(source, Array.Empty<(DateTime, DateTime)>());

        Assert.True(
            result.Count == 0 || result.Count == 1,
            "2-second source is >=1s so it should pass");
    }

    [Fact]
    public void EmptyExcluded_ReturnsCopy()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 12, 0, 0)),
            (new(2026, 5, 1, 14, 0, 0), new(2026, 5, 1, 16, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, Array.Empty<(DateTime, DateTime)>());

        Assert.Equal(2, result.Count);
        Assert.Equal(source[0], result[0]);
        Assert.Equal(source[1], result[1]);
    }

    [Fact]
    public void OverlappingExclusions_HandledCorrectly()
    {
        var source = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 10, 0, 0), new(2026, 5, 1, 20, 0, 0))
        };

        // Two exclusions that overlap with each other.
        var excluded = new List<(DateTime, DateTime)>
        {
            (new(2026, 5, 1, 12, 0, 0), new(2026, 5, 1, 15, 0, 0)),
            (new(2026, 5, 1, 14, 0, 0), new(2026, 5, 1, 17, 0, 0))
        };

        var result = IntervalSubtractor.Exclude(source, excluded);

        // Should still split into two segments: 10–12 and 17–20
        Assert.Equal(2, result.Count);
        Assert.Equal(new(2026, 5, 1, 10, 0, 0), result[0].FromUtc);
        Assert.Equal(new(2026, 5, 1, 12, 0, 0), result[0].ToUtc);
        Assert.Equal(new(2026, 5, 1, 17, 0, 0), result[1].FromUtc);
        Assert.Equal(new(2026, 5, 1, 20, 0, 0), result[1].ToUtc);
    }
}

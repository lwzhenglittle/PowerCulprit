namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Statistical helpers for the analyzer.
/// </summary>
internal static class CorrelationHelper
{
    /// <summary>
    /// Computes Pearson correlation coefficient between two sequences.
    /// Returns null if the sequences have fewer than 3 elements or zero variance.
    /// </summary>
    /// <remarks>
    /// Accepts <see cref="IReadOnlyList{T}"/> so callers can pass <see cref="List{T}"/>
    /// directly without a <c>ToArray()</c> copy. The means are computed in the same
    /// pass as the variances/covariance using the shift trick (subtracting the first
    /// element as a numerically-stable origin), so we only iterate the inputs once.
    /// </remarks>
    public static double? Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 3 || n != y.Count)
            return null;

        // One-pass Welford-style mean and covariance. Shifting by the first sample
        // keeps the running sums small and avoids the catastrophic cancellation that
        // a naïve sum-of-squares-of-raw-values would hit on inputs with a large mean.
        var x0 = x[0];
        var y0 = y[0];
        double sumDx = 0, sumDy = 0;
        double sumDxDx = 0, sumDyDy = 0, sumDxDy = 0;

        for (int i = 0; i < n; i++)
        {
            var dx = x[i] - x0;
            var dy = y[i] - y0;
            sumDx += dx;
            sumDy += dy;
            sumDxDx += dx * dx;
            sumDyDy += dy * dy;
            sumDxDy += dx * dy;
        }

        var invN = 1.0 / n;
        // var = E[d^2] - (E[d])^2; same identity for the cross term yields cov.
        var varX = sumDxDx - sumDx * sumDx * invN;
        var varY = sumDyDy - sumDy * sumDy * invN;
        var cov  = sumDxDy - sumDx * sumDy * invN;

        if (varX < 1e-10 || varY < 1e-10)
            return null; // zero variance

        var r = cov / Math.Sqrt(varX * varY);
        return Math.Round(r, 4);
    }
}

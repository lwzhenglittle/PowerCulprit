using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class LibreHardwareMonitorCollectorTests
{
    private static readonly DateTime Now = new(2026, 07, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldRetryInit_AfterFailedInit_AllowsRetryOnceBackoffElapsed()
    {
        var retryAt = Now.AddSeconds(-1);

        Assert.True(LibreHardwareMonitorCollector.ShouldRetryInit(
            initialized: true, isAvailable: false, now: Now, nextRetryUtc: retryAt));
    }

    [Fact]
    public void ShouldRetryInit_WithinBackoff_DoesNotRetry()
    {
        var retryAt = Now.AddSeconds(30);

        Assert.False(LibreHardwareMonitorCollector.ShouldRetryInit(
            initialized: true, isAvailable: false, now: Now, nextRetryUtc: retryAt));
    }

    [Fact]
    public void ShouldRetryInit_AfterSuccessfulInit_DoesNotRetry()
    {
        Assert.False(LibreHardwareMonitorCollector.ShouldRetryInit(
            initialized: true, isAvailable: true, now: Now, nextRetryUtc: DateTime.MinValue));
    }

    [Fact]
    public void ShouldRetryInit_NeverInitialized_RetriesImmediately()
    {
        Assert.True(LibreHardwareMonitorCollector.ShouldRetryInit(
            initialized: false, isAvailable: false, now: Now, nextRetryUtc: DateTime.MinValue));
    }
}

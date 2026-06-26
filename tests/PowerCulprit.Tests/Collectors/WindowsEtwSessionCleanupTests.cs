using Microsoft.Extensions.Logging.Abstractions;
using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class WindowsEtwSessionCleanupTests
{
    [Theory]
    [InlineData("PowerCulprit-ETW-1234", true)]
    [InlineData("PowerCulprit-ETW-Probe-1234", true)]
    [InlineData("powerculprit-etw-1234", true)]
    [InlineData("NT Kernel Logger", false)]
    [InlineData("WPR_initiated_WprApp_WPR System Collector", false)]
    [InlineData("OtherTool-ETW-1234", false)]
    [InlineData("PowerCulprit-ETW-", false)]
    [InlineData("PowerCulprit-ETW-abc", false)]
    public void IsPowerCulpritSessionName_MatchesOnlyOwnedSessions(string sessionName, bool expected)
    {
        Assert.Equal(expected, WindowsEtwSessionCleanup.IsPowerCulpritSessionName(sessionName));
    }

    [Fact]
    public void StopPreviousPowerCulpritSessions_StopsOnlyOwnedSessions()
    {
        var controller = new FakeEtwSessionController(
            "PowerCulprit-ETW-1234",
            "PowerCulprit-ETW-Probe-5678",
            "NT Kernel Logger",
            "OtherTool-ETW-1234");

        var result = WindowsEtwSessionCleanup.StopPreviousPowerCulpritSessions(
            controller,
            NullLogger.Instance);

        Assert.Equal(2, result.Found);
        Assert.Equal(2, result.Stopped);
        Assert.Equal(0, result.Failed);
        Assert.Equal(new[] { "PowerCulprit-ETW-1234", "PowerCulprit-ETW-Probe-5678" }, controller.StoppedSessions);
    }

    [Fact]
    public void StopPreviousPowerCulpritSessions_ContinuesAfterStopFailure()
    {
        var controller = new FakeEtwSessionController(
            "PowerCulprit-ETW-1234",
            "PowerCulprit-ETW-5678");
        controller.FailOnStop.Add("PowerCulprit-ETW-1234");

        var result = WindowsEtwSessionCleanup.StopPreviousPowerCulpritSessions(
            controller,
            NullLogger.Instance);

        Assert.Equal(2, result.Found);
        Assert.Equal(1, result.Stopped);
        Assert.Equal(1, result.Failed);
        Assert.Equal(new[] { "PowerCulprit-ETW-1234" }, result.FailedSessionNames);
        Assert.Equal(new[] { "PowerCulprit-ETW-1234", "PowerCulprit-ETW-5678" }, controller.StoppedSessions);
    }

    [Fact]
    public void StopPreviousPowerCulpritSessions_HandlesEnumerationFailure()
    {
        var controller = new FakeEtwSessionController { ThrowOnEnumerate = true };

        var result = WindowsEtwSessionCleanup.StopPreviousPowerCulpritSessions(
            controller,
            NullLogger.Instance);

        Assert.Equal(0, result.Found);
        Assert.Equal(0, result.Stopped);
        Assert.Equal(1, result.Failed);
    }

    private sealed class FakeEtwSessionController : IEtwSessionController
    {
        private readonly IReadOnlyList<string> _sessionNames;

        public FakeEtwSessionController(params string[] sessionNames)
        {
            _sessionNames = sessionNames;
        }

        public bool ThrowOnEnumerate { get; init; }

        public HashSet<string> FailOnStop { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> StoppedSessions { get; } = new();

        public IReadOnlyList<string> GetActiveSessionNames()
        {
            if (ThrowOnEnumerate)
                throw new InvalidOperationException("enumeration failed");

            return _sessionNames;
        }

        public void StopSession(string sessionName)
        {
            StoppedSessions.Add(sessionName);
            if (FailOnStop.Contains(sessionName))
                throw new InvalidOperationException("stop failed");
        }
    }
}

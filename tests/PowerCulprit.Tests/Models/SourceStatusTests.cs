using PowerCulprit.Core.Models;

namespace PowerCulprit.Tests.Models;

public class SourceStatusTests
{
    [Fact]
    public void Constructor_DefaultsToUnavailable()
    {
        var s = new SourceStatus();

        Assert.False(s.IsAvailable);
        Assert.Equal("Unavailable", s.Status);
        Assert.Null(s.Details);
        Assert.Null(s.RequiresAdmin);
    }

    [Theory]
    [InlineData("Available")]
    [InlineData("Unavailable")]
    [InlineData("Partial")]
    [InlineData("Requires admin")]
    [InlineData("Disabled")]
    public void StatusLabel_AcceptsAllDefinedValues(string status)
    {
        var s = new SourceStatus
        {
            SourceName = "TestSource",
            Status = status,
            IsAvailable = status == "Available" || status == "Partial"
        };

        Assert.Equal(status, s.Status);
    }
}

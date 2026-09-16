using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class WindowsTaskSchedulerTests
{
    [Fact]
    public void ReadingMissingTaskReturnsNull()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scheduler = new WindowsTaskScheduler();
        Assert.Null(scheduler.ReadXml($"AgentSignaler-Test-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void DeletingMissingTaskSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scheduler = new WindowsTaskScheduler();
        scheduler.Delete($"AgentSignaler-Test-{Guid.NewGuid():N}");
    }

    [Fact]
    public void LocalFailureDescriptionRetainsUnderlyingMessage()
    {
        var description = RemoteFailure.DescribeLocalFailure(new UnauthorizedAccessException("Registry access denied."));

        Assert.Contains("Registry access denied.", description);
        Assert.Contains("startup registration", description);
    }
}

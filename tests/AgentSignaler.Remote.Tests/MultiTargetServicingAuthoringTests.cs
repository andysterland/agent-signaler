using System.Xml.Linq;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class MultiTargetServicingAuthoringTests
{
    [Fact]
    public void UpgradeStopsOldClientImmediatelyUsingInstalledMatchingHelperBeforeRemoval()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "installers", "AgentSignaler.Remote", "Package.wxs");
            if (File.Exists(candidate)) { path = candidate; break; }
            directory = directory.Parent;
        }
        Assert.NotNull(path);
        var document = XDocument.Load(path);
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        var action = Assert.Single(document.Descendants(ns + "CustomAction"),
            element => (string?)element.Attribute("Id") == "StopRemoteClientForUpdate");
        Assert.Equal("immediate", (string?)action.Attribute("Execute"));
        Assert.Equal("check", (string?)action.Attribute("Return"));
        Assert.Equal("yes", (string?)action.Attribute("Impersonate"));
        Assert.Equal("INSTALLFOLDER", (string?)action.Attribute("Directory"));
        Assert.Equal("\"[INSTALLFOLDER]AgentSignaler.Relay.exe\" --stop-client-for-update",
            (string?)action.Attribute("ExeCommand"));
        var stopSequence = Assert.Single(document.Descendants(ns + "Custom"),
            element => (string?)element.Attribute("Action") == "StopRemoteClientForUpdate");
        Assert.Equal("RemoveExistingProducts", (string?)stopSequence.Attribute("Before"));
        Assert.Equal("REMOTECLIENTEXISTS AND NOT REMOVE=\"ALL\"", (string?)stopSequence.Attribute("Condition"));
        Assert.Equal("afterInstallInitialize",
            (string?)Assert.Single(document.Descendants(ns + "MajorUpgrade")).Attribute("Schedule"));
        var remove = Assert.Single(document.Descendants(ns + "Custom"),
            element => (string?)element.Attribute("Action") == "RemoveRemoteIntegration");
        Assert.Equal("RemoveFiles", (string?)remove.Attribute("Before"));
        Assert.Equal("REMOVE=\"ALL\" AND NOT UPGRADINGPRODUCTCODE", (string?)remove.Attribute("Condition"));
    }
}

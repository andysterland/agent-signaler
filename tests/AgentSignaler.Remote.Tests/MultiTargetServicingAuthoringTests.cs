using System.Xml.Linq;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class MultiTargetServicingAuthoringTests
{
    [Theory]
    [InlineData("Remote")]
    [InlineData("Dashboard")]
    public void InstallerOwnsOnlyApplicationPayloadAndShortcutsNeverHostTranscriptInputs(string product)
    {
        var root = FindRepositoryRoot();
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        var package = XDocument.Load(Path.Combine(root, "installers", $"AgentSignaler.{product}", "Package.wxs"));
        Assert.Equal("perUser", (string?)Assert.Single(package.Descendants(ns + "Package")).Attribute("Scope"));
        var directories = package.Descendants(ns + "Directory").ToDictionary(
            element => (string)element.Attribute("Id")!, element => (string?)element.Attribute("Name"));
        Assert.Equal(4, directories.Count);
        Assert.Equal("Programs", directories["ProgramsFolder"]);
        Assert.Equal("AgentSignaler", directories["AgentSignalerFolder"]);
        Assert.Equal(product, directories["INSTALLFOLDER"]);
        Assert.Equal("Agent Signaler", directories["ApplicationProgramsFolder"]);
        Assert.Empty(package.Descendants(ns + "RemoveFile"));
        Assert.Empty(package.Descendants(ns + "File"));
        Assert.Empty(package.Descendants(ns + "Environment"));
        Assert.Empty(package.Descendants(ns + "ServiceInstall"));
        Assert.All(package.Descendants(ns + "RemoveFolder"), element =>
            Assert.Equal("uninstall", (string?)element.Attribute("On")));
        Assert.All(package.Descendants(ns + "RegistryValue"), element =>
        {
            Assert.Equal("HKCU", (string?)element.Attribute("Root"));
            Assert.Equal($"Software\\AgentSignaler\\Installer\\{product}", (string?)element.Attribute("Key"));
        });
        var project = XDocument.Load(Path.Combine(root, "installers", $"AgentSignaler.{product}",
            $"AgentSignaler.{product}.wixproj"));
        Assert.Equal("$(PublishDir)", (string?)Assert.Single(project.Descendants("HarvestDirectory")).Attribute("Include"));
        Assert.Equal("INSTALLFOLDER", Assert.Single(project.Descendants("DirectoryRefId")).Value);
        Assert.Equal("$(MSBuildThisFileDirectory)..\\..\\artifacts\\publish\\" + product.ToLowerInvariant(),
            Assert.Single(project.Descendants("PublishDir")).Value);

        var transform = XDocument.Load(Path.Combine(root, "installers", "PerUserHarvest.xslt"));
        Assert.Empty(transform.Descendants(ns + "RemoveFile"));
        Assert.All(transform.Descendants(ns + "RemoveFolder"), element =>
            Assert.Equal("uninstall", (string?)element.Attribute("On")));
    }

    [Theory]
    [InlineData("Remote")]
    [InlineData("Dashboard")]
    public void TranscriptChangesAddNoInstallerCaptureMigrationOrHostCleanupCommand(string product)
    {
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "installers",
            $"AgentSignaler.{product}", "Package.wxs"));
        var executable = product == "Remote" ? "Relay" : "Dashboard";
        var commands = new HashSet<string>(StringComparer.Ordinal)
        {
            $"\"[INSTALLFOLDER]AgentSignaler.{executable}.exe\" --rollback-uninstall-integration --transaction-id \"[IntegrationTransaction]\"",
            $"\"[INSTALLFOLDER]AgentSignaler.{executable}.exe\" --uninstall-integration --transaction-id \"[IntegrationTransaction]\""
        };
        if (product == "Remote")
        {
            commands.Add("\"[INSTALLFOLDER]AgentSignaler.Relay.exe\" --stop-client-for-update");
            foreach (var command in new[] { "migrate", "rollback", "commit" })
                commands.Add($"\"[INSTALLFOLDER]AgentSignaler.Relay.exe\" --{command}-legacy-heartbeat --transaction-id \"[IntegrationTransaction]\"");
        }
        var actions = document.Descendants(ns + "CustomAction").ToArray();
        Assert.Equal(commands.Count, actions.Length);
        Assert.All(actions, element =>
        {
            Assert.Equal("INSTALLFOLDER", (string?)element.Attribute("Directory"));
            Assert.Equal("yes", (string?)element.Attribute("Impersonate"));
            Assert.Equal("check", (string?)element.Attribute("Return"));
            Assert.Contains((string)element.Attribute("ExeCommand")!, commands);
        });
        Assert.NotNull(Assert.Single(document.Descendants(ns + "MajorUpgrade")).Attribute("DowngradeErrorMessage"));
    }

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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentSignaler.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}

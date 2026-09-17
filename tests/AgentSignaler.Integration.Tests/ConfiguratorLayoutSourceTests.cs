using System.Xml.Linq;

namespace AgentSignaler.Integration.Tests;

public sealed class ConfiguratorLayoutSourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void StartClientIsImmediatelyBeforeApplyInSharedBottomActionGroup()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var start = document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "StartClientButton");
        var apply = document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "ApplyButton");
        var group = Assert.IsType<XElement>(start.Parent);
        Assert.Same(group, apply.Parent);
        Assert.Equal("StackPanel", group.Name.LocalName);
        Assert.Equal("Horizontal", (string?)group.Attribute("Orientation"));
        Assert.Equal("Right", (string?)group.Attribute("HorizontalAlignment"));
        Assert.Equal(new[] { "StartClientButton", "ApplyButton" },
            group.Elements().Select(element => (string?)element.Attribute(Xaml + "Name")));
        Assert.Equal("3", (string?)group.Parent!.Attribute("Grid.Row"));
        Assert.Equal("StartClient_Click", (string?)start.Attribute("Click"));
        Assert.Equal("Apply_Click", (string?)apply.Attribute("Click"));
    }

    [Fact]
    public void StartupCheckboxStagesPreferenceForConfirmedApplyWithoutImmediateSideEffects()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var checkbox = document.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "StartClientAtSignInCheckBox");
        Assert.Equal("CheckBox", checkbox.Name.LocalName);
        Assert.Equal("StartupChanged", (string?)checkbox.Attribute("Checked"));
        Assert.Equal("StartupChanged", (string?)checkbox.Attribute("Unchecked"));
        Assert.NotEqual("True", (string?)checkbox.Attribute("IsChecked"));
        var source = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        Assert.Contains("StartClientAtSignInCheckBox.IsChecked = startup.Read(IntegrationStartup.Name(ConfigPath)) is not null;", source);
        Assert.Contains("startClientAtSignIn: StartClientAtSignInCheckBox.IsChecked == true", source);
        Assert.Contains("private void StartupChanged(object sender, RoutedEventArgs e) => InvalidatePreview();", source);
        Assert.Contains("StartClientAtSignInCheckBox.IsEnabled =", source);
        Assert.Contains("if (!preserveSelection || !startupPreferenceLoaded) LoadStartupPreference();", source);
        Assert.Contains("ApplyButton.IsEnabled = state.CanApply && startupPreferenceLoaded;", source);
        Assert.Contains("if (!await ConfirmAsync(\"Apply settings and selected hook integrations?\", approved.Preview, \"Apply settings\")) return;", source);
        Assert.DoesNotContain("startup.Replace(", source);
    }

    private static string SourcePath(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "src", "AgentSignaler.Configurator", name);
    }
}

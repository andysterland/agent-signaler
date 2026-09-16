using System.Xml.Linq;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class MachineCardAppearanceTests
{
    [Theory]
    [InlineData(AgentState.Executing, "Build Agent")]
    [InlineData(AgentState.Waiting, "Docs Review")]
    [InlineData(AgentState.Succeeded, "Test Runner")]
    [InlineData(AgentState.Failed, "API Refactor")]
    [InlineData(AgentState.Idle, "Local Sandbox")]
    [InlineData(AgentState.Offline, "Release Check")]
    public void EveryStateMatchesReferenceSvgColorsAndGlyph(AgentState state, string machineName)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var svg = XDocument.Load(Path.Combine(root.FullName, "docs", "images", "dashboard-overview.svg"));
        XNamespace ns = "http://www.w3.org/2000/svg";
        var name = Assert.Single(svg.Descendants(ns + "text"), text => text.Value == machineName);
        var card = name.Parent!;
        var rectangle = Assert.Single(card.Elements(ns + "rect"));
        var connection = Assert.Single(card.Elements(ns + "circle"));
        var glyph = Assert.Single(card.Elements(ns + "text"), text => (string?)text.Attribute("font-size") == "54");
        var appearance = MachineCardAppearance.For(state);

        Assert.Equal(glyph.Value, appearance.Glyph);
        Assert.Equal((string?)rectangle.Attribute("fill"), Hex(appearance.Background));
        Assert.Equal((string?)rectangle.Attribute("stroke"), Hex(appearance.Border));
        Assert.Equal((string?)glyph.Attribute("fill"), Hex(appearance.Icon));
        Assert.Equal((string?)name.Attribute("fill"), Hex(appearance.Text));
        Assert.Equal((string?)connection.Attribute("fill"), Hex(appearance.Connection));
        Assert.Equal(TextColor("98"), Hex(appearance.Status));
        Assert.Equal(TextColor("128"), Hex(appearance.Activity));
        Assert.Equal(TextColor("176"), Hex(appearance.Mapping));
        Assert.Equal(TextColor("202"), Hex(appearance.Footer));

        var compact = XDocument.Load(Path.Combine(root.FullName, "docs", "images", "compact-view.svg"));
        var tile = Assert.Single(compact.Descendants(ns + "g"),
            group => (string?)group.Attribute("id") == state.ToString().ToLowerInvariant());
        var tileBackground = Assert.Single(tile.Elements(ns + "rect"));
        var tileGlyph = Assert.Single(tile.Elements(ns + "text"));
        Assert.Equal(appearance.Glyph, tileGlyph.Value);
        Assert.Equal(Hex(appearance.Background), (string?)tileBackground.Attribute("fill"));
        Assert.Equal(Hex(appearance.Border), (string?)tileBackground.Attribute("stroke"));
        Assert.Equal(Hex(appearance.Icon), (string?)tileGlyph.Attribute("fill"));
        Assert.Empty(tile.Elements(ns + "circle"));

        string? TextColor(string y) =>
            (string?)Assert.Single(card.Elements(ns + "text"), text => (string?)text.Attribute("y") == y).Attribute("fill");
    }

    [Fact]
    public void UnknownStateKeepsOfflineAppearance()
    {
        Assert.Equal(MachineCardAppearance.For(AgentState.Offline), MachineCardAppearance.For((AgentState)999));
    }

    private static string Hex(uint color) => $"#{color:x6}";
}

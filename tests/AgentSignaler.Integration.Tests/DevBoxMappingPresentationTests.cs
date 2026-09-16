using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class DevBoxMappingPresentationTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void DevBoxCatalogSaveRequiresAvailableSelectionAndBothGatesIdle(
        bool machineBusy, bool catalogBusy, bool selectedAvailable, bool expected) =>
        Assert.Equal(expected, DevBoxMappingPresentation.CanSaveMapping(machineBusy, catalogBusy, selectedAvailable));

    [Fact]
    public void DevBoxCatalogPickerUsesNameProjectThenEndpointOrdinalIgnoreCaseOrder()
    {
        var items = new[]
        {
            Item("zulu", "aaa", "aaa"), Item("Alpha", "zzz", "aaa"),
            Item("alpha", "bbb", "zzz"), Item("alpha", "bbb", "aaa")
        };
        var expected = new[] { items[3], items[2], items[1], items[0] };
        Assert.Equal(expected, DevBoxMappingPresentation.SortItems(items));
        Assert.Equal(expected, DevBoxMappingPresentation.SortItems(items.Reverse()));
    }

    [Fact]
    public void DevBoxCatalogPickerWithoutSavedMappingNeverSelectsFirstOrNameMatchedEntry()
    {
        var picker = DevBoxMappingPresentation.CreatePicker(Snapshot([Item()]), null);
        Assert.Single(picker.Options);
        Assert.Equal(-1, picker.SelectedIndex);
        Assert.Equal("Dev Box: Not mapped", DevBoxMappingPresentation.TileText(null));
    }

    [Fact]
    public void DevBoxCatalogIdentityMatchesNormalizedEndpointProjectAndNameOnly()
    {
        var item = Item();
        var mapping = Mapping() with
        {
            DevCenterEndpoint = new("https://CENTER.WESTUS.devcenter.azure.com:443"),
            ProjectName = "PROJECT",
            DevBoxName = "BOX"
        };
        Assert.True(DevBoxMappingPresentation.MatchesIdentity(item, mapping));
        Assert.False(DevBoxMappingPresentation.MatchesIdentity(item, mapping with { ProjectName = "other" }));
        Assert.False(DevBoxMappingPresentation.MatchesIdentity(item, mapping with { DevBoxName = "other" }));
        Assert.False(DevBoxMappingPresentation.MatchesIdentity(item, mapping with { DevCenterEndpoint = new("https://other.westus.devcenter.azure.com/") }));
    }

    [Fact]
    public void DevBoxCatalogPickerSelectsOnlyTheSavedIdentityAmongSameNamedBoxes()
    {
        var sameName = Item() with { DevCenterEndpoint = new("https://other.westus.devcenter.azure.com/") };
        var matching = Item();
        var picker = DevBoxMappingPresentation.CreatePicker(Snapshot([sameName, matching]), Mapping());
        Assert.Equal(2, picker.Options.Count);
        Assert.Same(matching, picker.Options[picker.SelectedIndex].DevBox);
        Assert.True(picker.Options[picker.SelectedIndex].IsAvailable);
    }

    [Fact]
    public void DevBoxCatalogPickerRetainsUnavailableMappingWithoutSelectingAReplacement()
    {
        var mapping = Mapping();
        var picker = DevBoxMappingPresentation.CreatePicker(Snapshot([Item("other")]), mapping);
        Assert.Equal(2, picker.Options.Count);
        var selected = picker.Options[picker.SelectedIndex];
        Assert.False(selected.IsAvailable);
        Assert.Null(selected.DevBox);
        Assert.Same(mapping, selected.SavedMapping);
        Assert.Equal("Unavailable: box", selected.DisplayName);
        Assert.Equal("Dev Box: box", DevBoxMappingPresentation.TileText(mapping));
    }

    [Fact]
    public void DevBoxCatalogPickerBeforeRefreshIsEmptyUnlessItHasAnUnavailableSavedMapping()
    {
        var empty = DevBoxMappingPresentation.CreatePicker(null, null);
        Assert.Empty(empty.Options);
        Assert.Equal(-1, empty.SelectedIndex);
        var saved = DevBoxMappingPresentation.CreatePicker(null, Mapping());
        Assert.False(Assert.Single(saved.Options).IsAvailable);
        Assert.Equal(0, saved.SelectedIndex);
    }

    [Fact]
    public void DevBoxCatalogPickerDisplayWithholdsAccountAndConnectionUris()
    {
        var snapshot = Snapshot([Item()]);
        var mapping = Mapping() with
        {
            LastKnownConnectionUri = "ms-cloudpc:connect?private",
            ConnectionUriRetrievedAtUtc = DateTimeOffset.UtcNow
        };
        var available = DevBoxMappingPresentation.CreatePicker(snapshot, mapping);
        var unavailable = DevBoxMappingPresentation.CreatePicker(null, mapping);
        foreach (var text in new[] { available.Options[0].ToString(), unavailable.Options[0].ToString(), available.ToString() })
        {
            Assert.DoesNotContain("https:", text);
            Assert.DoesNotContain("ms-cloudpc:", text);
            Assert.DoesNotContain(mapping.AzureAccountUpn, text);
            Assert.DoesNotContain(mapping.AzureTenantId.ToString(), text);
        }
    }

    private static DevBoxCatalogItem Item(string name = "box", string project = "project", string center = "center") =>
        new(new($"https://{center}.westus.devcenter.azure.com/"), project, "pool", name,
            "Running", "Succeeded", null, null, null, null);

    private static WindowsAppConnection Mapping() =>
        new(new("https://center.westus.devcenter.azure.com/"), "project", "box", "user@example.com",
            Guid.NewGuid(), null, null);

    private static DevBoxCatalogSnapshot Snapshot(IReadOnlyList<DevBoxCatalogItem> items) =>
        new(items, "user@example.com", Guid.NewGuid(), DateTimeOffset.UtcNow);
}

using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class DevBoxCatalogTests
{
    [Fact]
    public void DiscoveredCentersRemainSnapshotOnlyAndToStringWithholdsEndpoints()
    {
        var snapshot = new DevBoxCatalogSnapshot([], "user@example.com", Guid.NewGuid(), DateTimeOffset.UtcNow)
        {
            DevCenterEndpoints = DevCenterEndpoints.Normalize([new("https://center.westus.devcenter.azure.com/")])
        };
        Assert.Single(snapshot.DevCenterEndpoints);
        Assert.Empty(snapshot.Items);
        Assert.DoesNotContain("center.westus", snapshot.ToString());
        Assert.DoesNotContain("user@example.com", snapshot.ToString());
    }

    [Fact]
    public void EndpointNormalizationUsesIdnHostAndRemovesExplicitDefaultPort()
    {
        var endpoints = DevCenterEndpoints.Normalize([new("https://BÜCHER.WESTUS.devcenter.azure.com:443")]);
        Assert.Equal("https://xn--bcher-kva.westus.devcenter.azure.com/", Assert.Single(endpoints).AbsoluteUri);
    }

    [Fact]
    public void EndpointNormalizationReturnsIndependentReadOnlyCollection()
    {
        var input = new List<Uri> { new("https://center.westus.devcenter.azure.com") };
        var endpoints = DevCenterEndpoints.Normalize(input);
        input.Clear();
        Assert.Single(endpoints);
        Assert.Throws<NotSupportedException>(() => ((IList<Uri>)endpoints).Clear());
    }

    [Fact]
    public void EndpointEnumerationStopsAtLimit()
    {
        var visited = 0;
        IEnumerable<Uri> Endpoints()
        {
            while (true)
            {
                visited++;
                yield return new($"https://center{visited}.westus.devcenter.azure.com/");
            }
        }
        Assert.Throws<ArgumentException>(() => DevCenterEndpoints.Normalize(Endpoints()));
        Assert.Equal(DevCenterEndpoints.MaximumCount + 1, visited);
    }

    [Fact]
    public void EndpointLimitIsInclusiveAndEmptyListIsAllowed()
    {
        Assert.Empty(DevCenterEndpoints.Normalize([]));
        Assert.Equal(DevCenterEndpoints.MaximumCount, DevCenterEndpoints.Normalize(
            Enumerable.Range(0, DevCenterEndpoints.MaximumCount)
                .Select(index => new Uri($"https://center{index}.westus.devcenter.azure.com/"))).Count);
    }

    [Fact]
    public void EndpointValidationAndNormalizationRejectNullAndDuplicates()
    {
        Assert.Throws<ArgumentNullException>(() => DevCenterEndpoints.Normalize(null!));
        Assert.Throws<ArgumentException>(() => DevCenterEndpoints.Normalize([null!]));
        Assert.Throws<ArgumentException>(() => DevCenterEndpoints.Normalize([
            new("https://center.westus.devcenter.azure.com/"), new("https://CENTER.WESTUS.devcenter.azure.com:443")]));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("Dev-Box_01.name")]
    public void SharedResourceNameValidatorAcceptsMappingNames(string name) =>
        WindowsAppConnection.ValidateResourceName(name);

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("box/name")]
    [InlineData("box\n")]
    public void SharedResourceNameValidatorRejectsInvalidNames(string? name) =>
        Assert.Throws<ArgumentException>(() => WindowsAppConnection.ValidateResourceName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("user")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@example.com\n")]
    [InlineData("user@@example.com")]
    public void SharedUpnValidatorRejectsInvalidAccounts(string? upn) =>
        Assert.Throws<ArgumentException>(() => WindowsAppConnection.ValidateUpn(upn));

    [Fact]
    public void CatalogRecordsWithholdIdentityFromToString()
    {
        var item = new DevBoxCatalogItem(new("https://center.westus.devcenter.azure.com/"),
            "project", "pool", "box", "Running", "Succeeded", "Windows", 8, 32, Guid.NewGuid());
        var tenant = Guid.NewGuid();
        var snapshot = new DevBoxCatalogSnapshot([item], "user@example.com", tenant, DateTimeOffset.UtcNow);
        var selection = new DevBoxMappingSelection(item, snapshot.AzureAccountUpn, tenant);
        foreach (var text in new[] { item.ToString(), snapshot.ToString(), selection.ToString() })
        {
            Assert.DoesNotContain("https:", text);
            Assert.DoesNotContain(item.DevCenterEndpoint.Host, text);
            Assert.DoesNotContain(snapshot.AzureAccountUpn, text);
            Assert.DoesNotContain(tenant.ToString(), text);
        }
    }
}

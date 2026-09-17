using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal static class DevBoxDiscoveryTarget
{
    public static void Validate(Guid? subscriptionId, string? devCenterName)
    {
        if (subscriptionId == Guid.Empty) throw new ArgumentException("Supply a nonempty subscription GUID.", nameof(subscriptionId));
        if (subscriptionId is not null && devCenterName is not null)
            throw new ArgumentException("Specify a subscription GUID or a Dev Center name, not both.");
        if (devCenterName is not null) WindowsAppConnection.ValidateResourceName(devCenterName, nameof(devCenterName));
    }
}

internal sealed record DevBoxCatalogItem(
    Uri DevCenterEndpoint,
    string ProjectName,
    string PoolName,
    string DevBoxName,
    string PowerState,
    string ProvisioningState,
    string? OperatingSystem,
    int? VCpus,
    int? MemoryGb,
    Guid? UniqueId)
{
    public override string ToString() => "DevBoxCatalogItem { details withheld }";
}

internal sealed record DevBoxCatalogSnapshot(
    IReadOnlyList<DevBoxCatalogItem> Items,
    string AzureAccountUpn,
    Guid AzureTenantId,
    DateTimeOffset RetrievedAtUtc)
{
    public IReadOnlyList<Uri> DevCenterEndpoints { get; init; } = [];
    public IReadOnlyList<DevBoxSubscriptionFailure> SubscriptionFailures { get; init; } = [];

    public override string ToString() => "DevBoxCatalogSnapshot { details withheld }";
}

internal sealed record DevBoxSubscriptionFailure(Guid SubscriptionId, WindowsAppFailure Failure)
{
    public string Message => new DevBoxCatalogException(Failure, DevBoxCatalogStage.DevCenters).Message;
    public override string ToString() => $"Subscription discovery failed: {Failure}";
}

internal sealed record DevBoxMappingSelection(
    DevBoxCatalogItem DevBox,
    string AzureAccountUpn,
    Guid AzureTenantId)
{
    public override string ToString() => "DevBoxMappingSelection { details withheld }";
}

internal static class DevCenterEndpoints
{
    internal const int MaximumCount = 20;

    internal static IReadOnlyList<Uri> Normalize(IEnumerable<Uri> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var result = new List<Uri>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            if (result.Count == MaximumCount)
                throw new ArgumentException($"At most {MaximumCount} Dev Center endpoints are supported.", nameof(endpoints));
            WindowsAppConnection.ValidateEndpoint(endpoint);
            var normalized = new Uri($"https://{endpoint.IdnHost.ToLowerInvariant()}/");
            if (!identities.Add(normalized.AbsoluteUri))
                throw new ArgumentException("Dev Center endpoints must not contain duplicates.", nameof(endpoints));
            result.Add(normalized);
        }
        return result.Count == 0 ? Array.Empty<Uri>() : result.AsReadOnly();
    }

}

internal sealed record DevBoxMappingOption(DevBoxCatalogItem? DevBox, WindowsAppConnection? SavedMapping)
{
    internal bool IsAvailable => DevBox is not null;
    internal string DisplayName => DevBox is { } item
        ? $"{item.DevBoxName} | {item.ProjectName} | {item.DevCenterEndpoint.IdnHost}"
        : $"Unavailable: {SavedMapping!.DevBoxName}";

    public override string ToString() => DisplayName;
}

internal sealed record DevBoxMappingPicker(IReadOnlyList<DevBoxMappingOption> Options, int SelectedIndex)
{
    public override string ToString() => "DevBoxMappingPicker { details withheld }";
}

internal static class DevBoxMappingPresentation
{
    internal static bool CanSaveMapping(bool machineBusy, bool catalogBusy, bool selectedAvailable) =>
        !machineBusy && !catalogBusy && selectedAvailable;

    internal static bool MatchesIdentity(DevBoxCatalogItem item, WindowsAppConnection mapping) =>
        MatchesIdentity(item, mapping.DevCenterEndpoint, mapping.ProjectName, mapping.DevBoxName);

    internal static bool MatchesIdentity(DevBoxCatalogItem item, Uri endpoint, string projectName, string devBoxName) =>
        DevCenterEndpoints.Normalize([item.DevCenterEndpoint])[0] == DevCenterEndpoints.Normalize([endpoint])[0] &&
        StringComparer.OrdinalIgnoreCase.Equals(item.ProjectName, projectName) &&
        StringComparer.OrdinalIgnoreCase.Equals(item.DevBoxName, devBoxName);

    internal static IReadOnlyList<DevBoxCatalogItem> SortItems(IEnumerable<DevBoxCatalogItem> items) =>
        Array.AsReadOnly(items
            .OrderBy(item => item.DevBoxName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DevCenterEndpoint.IdnHost, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DevBoxName, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectName, StringComparer.Ordinal)
            .ToArray());

    internal static DevBoxMappingPicker CreatePicker(DevBoxCatalogSnapshot? snapshot, WindowsAppConnection? mapping)
    {
        var options = SortItems(snapshot?.Items ?? [])
            .Select(item => new DevBoxMappingOption(item, null)).ToList();
        var selectedIndex = mapping is null ? -1 : options.FindIndex(option => MatchesIdentity(option.DevBox!, mapping));
        if (mapping is not null && selectedIndex < 0)
        {
            selectedIndex = options.Count;
            options.Add(new(null, mapping));
        }
        return new(options.AsReadOnly(), selectedIndex);
    }

    internal static string TileText(WindowsAppConnection? mapping) =>
        mapping is null ? "Dev Box: Not mapped" : $"Dev Box: {mapping.DevBoxName}";
}

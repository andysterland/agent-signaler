using System.Text.Json;
using AgentSignaler.Service;
using static AgentSignaler.Dashboard.AzureCliResponse;

namespace AgentSignaler.Dashboard;

internal interface IDevBoxCatalogService
{
    Task<DevBoxCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken,
        Action<DevBoxCatalogProgress>? reportProgress = null, Guid? subscriptionId = null, string? devCenterName = null);
}

internal enum DevBoxCatalogStage { Account, Subscriptions, DevCenters, DevBoxes }

internal sealed record DevBoxCatalogProgress(DevBoxCatalogStage Stage)
{
    public AzureAccount? Account { get; init; }
    public DateTimeOffset? AccountCheckedAtUtc { get; init; }
    public int? SubscriptionCount { get; init; }
    public int? DevCenterCount { get; init; }
    public int SubscriptionsCompleted { get; init; }
    public int DevCentersCompleted { get; init; }
    public Guid? CurrentSubscription { get; init; }
    public string? CurrentDevCenterHost { get; init; }
    public int? CurrentPage { get; init; }
    public int PagesRead { get; init; }
    public int? DevBoxCount { get; init; }
    public IReadOnlyList<DevBoxSubscriptionFailure> SubscriptionFailures { get; init; } = [];

    public string StageText => Stage switch
    {
        DevBoxCatalogStage.Account => "Azure account validation",
        DevBoxCatalogStage.Subscriptions => "Azure subscription discovery",
        DevBoxCatalogStage.DevCenters => "Dev Center resource discovery",
        _ => "Assigned Dev Box discovery"
    };

    public override string ToString() => StageText;
}

internal sealed class DevBoxCatalogException : Exception
{
    public DevBoxCatalogException(WindowsAppFailure failure, string? endpointHost = null)
        : this(failure, DevBoxCatalogStage.DevBoxes, endpointHost) { }

    public DevBoxCatalogException(WindowsAppFailure failure, DevBoxCatalogStage stage, string? endpointHost = null)
        : base(stage == DevBoxCatalogStage.DevBoxes ? MessageFor(failure, SafeHost(endpointHost)) : DiscoveryMessage(failure, stage))
    {
        Failure = failure;
        Stage = stage;
        EndpointHost = stage == DevBoxCatalogStage.DevBoxes ? SafeHost(endpointHost) : null;
    }

    public WindowsAppFailure Failure { get; }
    public DevBoxCatalogStage Stage { get; }
    public string? EndpointHost { get; }
    public string Status => Failure switch
    {
        WindowsAppFailure.InvalidMapping => "Discovery unavailable",
        WindowsAppFailure.SignInRequired => "Sign-in required",
        WindowsAppFailure.TimedOut => "Refresh timed out",
        WindowsAppFailure.DiscoveryLimitExceeded => "Discovery limit exceeded",
        WindowsAppFailure.MalformedResponse or WindowsAppFailure.UnsafeUri => "Unsafe or malformed response",
        _ => "Refresh unavailable"
    };

    private static string DiscoveryMessage(WindowsAppFailure failure, DevBoxCatalogStage stage)
    {
        var scope = stage switch
        {
            DevBoxCatalogStage.Account => "Azure account validation",
            DevBoxCatalogStage.Subscriptions => "Azure subscription discovery",
            _ => "Dev Center resource discovery"
        };
        return $"{scope}: " + (failure switch
        {
            WindowsAppFailure.SignInRequired => "Sign in with Azure CLI and retry.",
            WindowsAppFailure.CliUnsupported => new WindowsAppConnectionException(failure).Message,
            WindowsAppFailure.TimedOut => "The request timed out. Retry the search.",
            WindowsAppFailure.DiscoveryLimitExceeded =>
                $"Azure CLI returned more than {DevBoxCatalogService.MaximumSubscriptionRecords} subscription records. Discovery stopped without truncating the results.",
            WindowsAppFailure.MalformedResponse or WindowsAppFailure.UnsafeUri => "Unsafe, malformed, or excessive discovery metadata was returned.",
            WindowsAppFailure.DevBoxUnavailable or WindowsAppFailure.ApiUnavailable when stage == DevBoxCatalogStage.DevCenters =>
                "Dev Centers could not be listed. Check network access and Azure Resource Manager permission to read Dev Centers, then retry.",
            WindowsAppFailure.DevBoxUnavailable or WindowsAppFailure.ApiUnavailable => "Resources could not be listed or access was denied. Check access and retry.",
            _ => "The Azure CLI account context is unavailable or unsupported. Check the active user, tenant, enabled subscription, and public Azure cloud."
        });
    }

    private static string MessageFor(WindowsAppFailure failure, string? host)
    {
        var message = failure switch
        {
            WindowsAppFailure.InvalidMapping => "Discovery returned invalid Dev Center metadata. Refresh the Dev Box list and retry.",
            WindowsAppFailure.DevBoxUnavailable => "The Dev Center is unavailable or access was denied.",
            WindowsAppFailure.UnsafeUri => "The Dev Center returned an unsafe catalog URI.",
            _ => new WindowsAppConnectionException(failure).Message
        };
        return host is null ? message : $"Dev Center {host}: {message}";
    }

    private static string? SafeHost(string? host)
    {
        if (host is null || !Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var endpoint))
            return null;
        try
        {
            WindowsAppConnection.ValidateEndpoint(endpoint);
            return endpoint.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) ? endpoint.IdnHost : null;
        }
        catch (ArgumentException) { return null; }
    }
}

internal sealed class DevBoxCatalogService(IAzureCliProcess process, TimeProvider? timeProvider = null) : IDevBoxCatalogService
{
    internal const int MaximumEndpoints = DevCenterEndpoints.MaximumCount;
    internal const int MaximumPagesPerEndpoint = 20;
    internal const int MaximumItemsPerEndpoint = 250;
    internal const int MaximumSubscriptionRecords = 1000;
    internal const int MaximumSubscriptions = MaximumSubscriptionRecords;
    internal const int MaximumArmPagesPerSubscription = 20;
    internal const int MaximumCentersPerSubscription = 250;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<DevBoxCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken,
        Action<DevBoxCatalogProgress>? reportProgress = null, Guid? subscriptionId = null, string? devCenterName = null)
    {
        DevBoxDiscoveryTarget.Validate(subscriptionId, devCenterName);
        string? endpointHost = null;
        var stage = DevBoxCatalogStage.Account;
        var progress = new DevBoxCatalogProgress(stage);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke(progress);
            var account = await AzureAccount.GetAsync(process, cancellationToken).ConfigureAwait(false);
            stage = devCenterName is null ? DevBoxCatalogStage.Subscriptions : DevBoxCatalogStage.DevBoxes;
            progress = progress with { Stage = stage, Account = account, AccountCheckedAtUtc = timeProvider.GetUtcNow() };
            reportProgress?.Invoke(progress);
            if (devCenterName is not null)
                return await DiscoverNamedDevBoxesAsync(devCenterName, account, progress, cancellationToken, reportProgress).ConfigureAwait(false);
            IReadOnlyList<Guid> subscriptions = subscriptionId is { } selected
                ? new[] { selected }
                : await DiscoverSubscriptionsAsync(account, cancellationToken).ConfigureAwait(false);
            stage = DevBoxCatalogStage.DevCenters;
            progress = progress with { Stage = stage, SubscriptionCount = subscriptions.Count };
            reportProgress?.Invoke(progress);
            var normalized = await DiscoverEndpointsAsync(subscriptions, cancellationToken, progress, update =>
            {
                progress = update;
                reportProgress?.Invoke(progress);
            }).ConfigureAwait(false);
            stage = DevBoxCatalogStage.DevBoxes;
            progress = progress with
            {
                Stage = stage, DevCenterCount = normalized.Count, DevBoxCount = 0,
                CurrentSubscription = null, CurrentPage = null
            };
            reportProgress?.Invoke(progress);
            var items = new List<DevBoxCatalogItem>();
            foreach (var endpoint in normalized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                endpointHost = endpoint.IdnHost;
                var command = AzureCliCommand.ListDevBoxes(endpoint);
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var count = 0;
                for (var page = 0; ; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (page >= MaximumPagesPerEndpoint) throw Malformed();
                    progress = progress with { CurrentDevCenterHost = endpointHost, CurrentPage = page + 1 };
                    reportProgress?.Invoke(progress);
                    var result = await process.RunAsync(command, command.Timeout, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckResult(result, account: false);
                    using var document = ParseJson(result.StandardOutput);
                    var root = document.RootElement;
                    var values = RequiredProperty(root, "value");
                    if (values.ValueKind != JsonValueKind.Array ||
                        values.GetArrayLength() > MaximumItemsPerEndpoint - count)
                        throw Malformed();
                    foreach (var value in values.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var item = ParseItem(endpoint, value);
                        if (!identities.Add($"{item.ProjectName}/{item.DevBoxName}")) throw Malformed();
                        items.Add(item);
                        count++;
                    }
                    progress = progress with { PagesRead = progress.PagesRead + 1, DevBoxCount = items.Count };
                    reportProgress?.Invoke(progress);
                    var next = OptionalProperty(root, "nextLink");
                    if (next is null || next.Value.ValueKind == JsonValueKind.Null) break;
                    if (!Uri.TryCreate(StringValue(next.Value), UriKind.Absolute, out var nextLink))
                        throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
                    command = AzureCliCommand.ListDevBoxesPage(endpoint, nextLink);
                    if (!visited.Add(nextLink.AbsoluteUri)) throw Malformed();
                }
                progress = progress with
                {
                    DevCentersCompleted = progress.DevCentersCompleted + 1,
                    CurrentDevCenterHost = null, CurrentPage = null
                };
                reportProgress?.Invoke(progress);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(DevBoxMappingPresentation.SortItems(items), account.Upn, account.Tenant, timeProvider.GetUtcNow())
            {
                DevCenterEndpoints = normalized,
                SubscriptionFailures = progress.SubscriptionFailures
            };
        }
        catch (WindowsAppConnectionException error)
        {
            throw new DevBoxCatalogException(error.Failure, stage, endpointHost);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("The Dev Box catalog refresh was cancelled.", cancellationToken);
        }
    }

    private async Task<DevBoxCatalogSnapshot> DiscoverNamedDevBoxesAsync(string devCenterName, AzureAccount account,
        DevBoxCatalogProgress progress, CancellationToken token, Action<DevBoxCatalogProgress>? reportProgress)
    {
        token.ThrowIfCancellationRequested();
        var command = AzureCliCommand.ListDevBoxesByName(devCenterName);
        var result = await process.RunAsync(command, command.Timeout, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        CheckResult(result, account: false);
        using var document = ParseJson(result.StandardOutput);
        var values = document.RootElement;
        // The CLI aggregates its pages into a JSON array, rather than a REST value/nextLink envelope.
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > MaximumItemsPerEndpoint) throw Malformed();
        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<DevBoxCatalogItem>();
        foreach (var value in values.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(RequiredString(value, "uri"), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
                throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
            var endpoint = new Uri(uri.GetLeftPart(UriPartial.Authority));
            try { WindowsAppConnection.ValidateEndpoint(endpoint); }
            catch (ArgumentException) { throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri); }
            AzureCliCommand.ValidateCatalogUri(endpoint, uri);
            var item = ParseItem(endpoint, value);
            if (!identities.Add($"{endpoint.IdnHost}/{item.ProjectName}/{item.DevBoxName}")) throw Malformed();
            endpoints.TryAdd(endpoint.AbsoluteUri, endpoint);
            if (endpoints.Count > MaximumEndpoints) throw Malformed();
            items.Add(item);
        }
        token.ThrowIfCancellationRequested();
        var normalized = DevCenterEndpoints.Normalize(endpoints.Values.OrderBy(endpoint => endpoint.IdnHost, StringComparer.OrdinalIgnoreCase));
        reportProgress?.Invoke(progress with
        {
            DevBoxCount = items.Count, DevCenterCount = normalized.Count,
            DevCentersCompleted = normalized.Count, PagesRead = 1
        });
        return new(DevBoxMappingPresentation.SortItems(items), account.Upn, account.Tenant, timeProvider.GetUtcNow())
        {
            DevCenterEndpoints = normalized
        };
    }

    private async Task<IReadOnlyList<Guid>> DiscoverSubscriptionsAsync(AzureAccount account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var command = AzureCliCommand.AccountList();
        var result = await process.RunAsync(command, command.Timeout, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        CheckResult(result, account: true);
        using var document = ParseJson(result.StandardOutput);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw Malformed();
        if (root.GetArrayLength() > MaximumSubscriptionRecords)
            throw new WindowsAppConnectionException(WindowsAppFailure.DiscoveryLimitExceeded);
        var subscriptions = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var value in root.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParse(RequiredString(value, "id"), out var id) || id == Guid.Empty ||
                !Guid.TryParse(RequiredString(value, "tenantId"), out var tenant) || tenant == Guid.Empty) throw Malformed();
            var state = RequiredString(value, "state");
            var cloud = RequiredString(value, "cloudName");
            var user = RequiredProperty(value, "user");
            var name = RequiredString(user, "name");
            var type = RequiredString(user, "type");
            var eligible = tenant == account.Tenant && state.Equals("Enabled", StringComparison.OrdinalIgnoreCase) &&
                cloud.Equals("AzureCloud", StringComparison.Ordinal) && type == "user" &&
                name.Equals(account.Upn, StringComparison.OrdinalIgnoreCase);
            if (!eligible)
            {
                if (id == account.Subscription) throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable);
                continue;
            }
            if (!seen.Add(id)) throw Malformed();
            if (subscriptions.Count == MaximumSubscriptions)
                throw new WindowsAppConnectionException(WindowsAppFailure.DiscoveryLimitExceeded);
            subscriptions.Add(id);
        }
        if (!seen.Contains(account.Subscription)) throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable);
        return subscriptions.AsReadOnly();
    }

    private async Task<IReadOnlyList<Uri>> DiscoverEndpointsAsync(IReadOnlyList<Guid> subscriptions, CancellationToken token,
        DevBoxCatalogProgress progress, Action<DevBoxCatalogProgress> reportProgress)
    {
        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<DevBoxSubscriptionFailure>();
        foreach (var subscription in subscriptions)
        {
            token.ThrowIfCancellationRequested();
            var discovered = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var command = AzureCliCommand.ListDevCenters(subscription);
                var visited = new HashSet<string>(StringComparer.Ordinal) { AzureCliCommand.DevCentersUri(subscription).AbsoluteUri };
                var count = 0;
                for (var page = 0; ; page++)
                {
                    token.ThrowIfCancellationRequested();
                    if (page >= MaximumArmPagesPerSubscription) throw Malformed();
                    progress = progress with { CurrentSubscription = subscription, CurrentPage = page + 1 };
                    reportProgress(progress);
                    var result = await process.RunAsync(command, command.Timeout, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    CheckResult(result, account: false);
                    using var document = ParseJson(result.StandardOutput);
                    var root = document.RootElement;
                    var values = RequiredProperty(root, "value");
                    if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > MaximumCentersPerSubscription - count)
                        throw Malformed();
                    foreach (var value in values.EnumerateArray())
                    {
                        token.ThrowIfCancellationRequested();
                        var endpoint = ParseDevCenter(value, subscription, identities);
                        discovered.TryAdd(endpoint.AbsoluteUri, endpoint);
                        if (discovered.Count > MaximumEndpoints) throw Malformed();
                        count++;
                    }
                    progress = progress with { PagesRead = progress.PagesRead + 1 };
                    reportProgress(progress);
                    var next = OptionalProperty(root, "nextLink");
                    if (next is null || next.Value.ValueKind == JsonValueKind.Null) break;
                    if (!Uri.TryCreate(StringValue(next.Value), UriKind.Absolute, out var nextLink))
                        throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
                    command = AzureCliCommand.ListDevCentersPage(subscription, nextLink);
                    if (!visited.Add(nextLink.AbsoluteUri)) throw Malformed();
                }
            }
            catch (WindowsAppConnectionException error)
            {
                token.ThrowIfCancellationRequested();
                failures.Add(new(subscription, error.Failure));
                // Do not publish endpoints from an incomplete or invalid subscription response.
                discovered.Clear();
            }
            foreach (var endpoint in discovered.Values)
                endpoints.TryAdd(endpoint.AbsoluteUri, endpoint);
            if (endpoints.Count > MaximumEndpoints) throw Malformed();
            progress = progress with
            {
                SubscriptionsCompleted = progress.SubscriptionsCompleted + 1,
                CurrentSubscription = null, CurrentPage = null, DevCenterCount = endpoints.Count,
                SubscriptionFailures = Array.AsReadOnly(failures.ToArray())
            };
            reportProgress(progress);
        }
        token.ThrowIfCancellationRequested();
        if (failures.Count != 0 && failures.Count == subscriptions.Count)
            throw new WindowsAppConnectionException(failures[0].Failure);
        return DevCenterEndpoints.Normalize(endpoints.Values.OrderBy(endpoint => endpoint.IdnHost, StringComparer.OrdinalIgnoreCase));
    }

    private static Uri ParseDevCenter(JsonElement value, Guid subscription, HashSet<string> identities)
    {
        var id = RequiredString(value, "id");
        var segments = id.Split('/');
        if (segments.Length != 9 || segments[0] != "" ||
            !segments[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(segments[2], "D", out var resourceSubscription) || resourceSubscription != subscription ||
            !segments[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) ||
            !SafeArmName(segments[4]) || !segments[5].Equals("providers", StringComparison.OrdinalIgnoreCase) ||
            !segments[6].Equals("Microsoft.DevCenter", StringComparison.OrdinalIgnoreCase) ||
            !segments[7].Equals("devcenters", StringComparison.OrdinalIgnoreCase) || !SafeArmName(segments[8]) ||
            !identities.Add(id)) throw Malformed();
        var name = OptionalProperty(value, "name");
        var type = OptionalProperty(value, "type");
        if ((name is not null && !StringValue(name.Value).Equals(segments[8], StringComparison.OrdinalIgnoreCase)) ||
            (type is not null && !StringValue(type.Value).Equals("Microsoft.DevCenter/devcenters", StringComparison.OrdinalIgnoreCase)))
            throw Malformed();
        var uri = RequiredString(RequiredProperty(value, "properties"), "devCenterUri");
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var endpoint))
            throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
        AzureCliCommand.ValidateCatalogUri(endpoint, endpoint);
        var pathStart = uri.IndexOf('/', uri.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart >= 0 && uri[pathStart..] != "/")
            throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
        try { return DevCenterEndpoints.Normalize([endpoint])[0]; }
        catch (ArgumentException) { throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri); }
    }

    private static bool SafeArmName(string name) =>
        name.Length is > 0 and <= 90 && name is not ("." or "..") &&
        name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '(' or ')');

    private static DevBoxCatalogItem ParseItem(Uri endpoint, JsonElement value)
    {
        var project = ResourceName(value, "projectName");
        var name = ResourceName(value, "name");
        var pool = ResourceName(value, "poolName");
        var uriValue = OptionalProperty(value, "uri");
        if (uriValue is not null)
        {
            if (!Uri.TryCreate(StringValue(uriValue.Value), UriKind.Absolute, out var uri))
                throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
            ValidateItemUri(endpoint, uri, project, name);
        }
        int? cpus = null, memory = null;
        var hardware = OptionalProperty(value, "hardwareProfile");
        if (hardware is not null)
        {
            cpus = OptionalPositiveInt(hardware.Value, "vCPUs");
            memory = OptionalPositiveInt(hardware.Value, "memoryGB");
        }
        Guid? uniqueId = null;
        var id = OptionalProperty(value, "uniqueId");
        if (id is not null)
        {
            if (!Guid.TryParse(StringValue(id.Value), out var parsed) || parsed == Guid.Empty) throw Malformed();
            uniqueId = parsed;
        }
        return new(endpoint, project, pool, name,
            OptionalText(value, "powerState") ?? "Unknown",
            OptionalText(value, "provisioningState") ?? "Unknown",
            OptionalText(value, "osType"), cpus, memory, uniqueId);
    }

    private static string ResourceName(JsonElement value, string property)
    {
        var name = RequiredString(value, property);
        try { WindowsAppConnection.ValidateResourceName(name, property); }
        catch (ArgumentException) { throw Malformed(); }
        return name;
    }

    private static string? OptionalText(JsonElement value, string property)
    {
        var element = OptionalProperty(value, property);
        if (element is null) return null;
        var text = StringValue(element.Value);
        if (text.Length is < 1 or > 128 || string.IsNullOrWhiteSpace(text) ||
            text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '-' or '_' or '.')))
            throw Malformed();
        return text;
    }

    private static int? OptionalPositiveInt(JsonElement value, string property)
    {
        var element = OptionalProperty(value, property);
        if (element is null) return null;
        if (element.Value.ValueKind != JsonValueKind.Number ||
            !element.Value.TryGetInt32(out var number) || number <= 0)
            throw Malformed();
        return number;
    }

    private static void ValidateItemUri(Uri endpoint, Uri uri, string project, string name)
    {
        AzureCliCommand.ValidateCatalogUri(endpoint, uri);
        // Inspect original path segments too: Uri canonicalization can erase traversal segments.
        var original = uri.OriginalString;
        var pathStart = original.IndexOf('/', original.IndexOf("://", StringComparison.Ordinal) + 3);
        var segments = pathStart < 0 ? [] : original[(pathStart + 1)..].Split('/');
        if (uri.Query.Length != 0 || segments.Length != 6 ||
            segments[0] != "projects" || segments[2] != "users" || segments[4] != "devboxes" ||
            !Uri.UnescapeDataString(segments[1]).Equals(project, StringComparison.OrdinalIgnoreCase) ||
            !Uri.UnescapeDataString(segments[5]).Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (segments[3] != "me" && (!Guid.TryParse(segments[3], out var user) || user == Guid.Empty)))
            throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
    }
}

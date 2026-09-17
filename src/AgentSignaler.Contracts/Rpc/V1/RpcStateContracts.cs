using System.Text.Json.Serialization;

namespace AgentSignaler.Contracts.Rpc.V1;

/// <summary>Runtime lifecycle and initial component outcomes, not an aggregate snapshot.</summary>
public sealed record RpcSystemState(string Lifecycle, string Stage, bool InitialAttemptCompleted,
    bool StorageAvailable, bool ReceiverAvailable, bool SharingAvailable, bool ControllerDrainIncomplete);

/// <summary>Nonvisual operational settings. Unknown persisted fields and UI preferences are not exposed.</summary>
public sealed record RpcOperationalSettings(int Port, int RpcPort, string ConnectionMode,
    string? DevTunnelCliPath, string? AzureCliPath, string? DevBoxSubscriptionId, string? DevCenterName,
    bool AutoStartSharing, bool ReceiveDetailedConversations);

/// <summary>Saved and effective settings and explicit restart requirements.</summary>
public sealed record RpcSettingsState(RpcOperationalSettings Saved, RpcOperationalSettings Effective,
    bool RpcPortOverridden, IReadOnlyList<string> RestartRequired, bool Recovered);

/// <summary>Receiver status; firewall metadata never asserts effective firewall authorization.</summary>
public sealed record RpcReceiverState(bool Running, int Port, string ConnectionMode,
    bool ReceiveDetailedConversations, string LocalReportUrl, string FirewallProvisioning,
    int? InstalledReceiverPort, bool FirewallPortMismatch, string FirewallInstruction);

/// <summary>Sharing status with only a verified public report URL.</summary>
public sealed record RpcSharingState(string State, string Message, string? ReportUrl, bool PublicEndpointVerified);

/// <summary>An explicit source identity for a sanitized agent session.</summary>
public sealed record RpcSource(string Kind, string ScopeId, string Version);

/// <summary>Effective session state with original UTC state-transition timestamps.</summary>
public sealed record RpcSession(string SessionId, RpcSource? Source, string State, string UnderlyingState,
    string? ResultState, DateTimeOffset? ResultUntilUtc, bool AwaitingUserInput, DateTimeOffset UpdatedAtUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }
}

/// <summary>Mapping identity; cached connection URIs and credential material are deliberately absent.</summary>
public sealed record RpcMapping(string DevCenterEndpoint, string ProjectName, string DevBoxName,
    string AzureAccountUpn, string AzureTenantId, DateTimeOffset? ConnectionUriRetrievedAtUtc,
    bool HasLastKnownConnection);

/// <summary>Bounded machine summary. Sessions and notes are read through separate paginated methods.</summary>
public sealed record RpcMachine(string MachineId, string MachineName, string? DisplayName, string Client,
    string ClientVersion, string State, string? LatestEvent, DateTimeOffset LastContactUtc,
    int SessionCount, int NoteLength, string PresenceMode, int? HeartbeatIntervalSeconds, string Generation,
    string Sequence, bool ExplicitOffline, DateTimeOffset OfflineDeadlineUtc, DateTimeOffset? LatestEventUtc,
    RpcMapping? Mapping);

/// <summary>Sanitized diagnostic outcome and the exact captured tested executable path.</summary>
public sealed record RpcPrerequisite(string Id, string? TestedPath, string State, bool? Passed, string? Summary);

/// <summary>Current prerequisite checks, with no raw command streams.</summary>
public sealed record RpcPrerequisiteState(IReadOnlyList<RpcPrerequisite> Checks);

/// <summary>One Dev Box catalog entry containing only documented metadata.</summary>
public sealed record RpcDevBox(string DevCenterEndpoint, string ProjectName, string PoolName,
    string DevBoxName, string PowerState, string ProvisioningState, string? OperatingSystem,
    int? VCpus, int? MemoryGb, string? UniqueId);

/// <summary>Sanitized discovery progress, independent of command output.</summary>
public sealed record RpcCatalogProgress(string Stage, int SubscriptionsCompleted, int DevCentersCompleted,
    int PagesRead, int? SubscriptionCount, int? DevCenterCount, int? DevBoxCount);

/// <summary>One current catalog page with stable ordering and explicit continuation.</summary>
public sealed record RpcCatalogState(IReadOnlyList<RpcDevBox> Items, int Offset, int Limit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? NextOffset,
    int TotalCount, bool IsBusy, string Status, string Message, RpcCatalogProgress? Progress,
    string? AzureAccountUpn, string? AzureTenantId, DateTimeOffset? RetrievedAtUtc,
    string? TargetSubscriptionId, string? TargetDevCenterName);

/// <summary>Current per-machine Windows App state without native connection data or window titles.</summary>
public sealed record RpcWindowsAppState(string MachineId, RpcMapping? Mapping, string Status,
    string Message, string? Operation, bool IsBusy, bool CanOpenLastKnown);

/// <summary>A sanitized runtime problem notification.</summary>
public sealed record RpcProblem(string HostInstanceId, int Code, RpcErrorData Data);

/// <summary>A receiver connection-test notification without peer identity.</summary>
public sealed record RpcConnectionTest(string HostInstanceId);

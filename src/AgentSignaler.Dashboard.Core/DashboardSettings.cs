using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSignaler.Dashboard;

internal sealed record DashboardSettings
{
    internal const int MaximumFileBytes = 1024 * 1024;
    private static readonly object writer = new();
    public int Port { get; init; } = 51820;
    public int RpcPort { get; init; } = 51821;
    public bool Compact { get; init; } = true;
    public bool ShowCompactViewWhenMinimized { get; init; } = true;
    public string Theme { get; init; } = "System";
    public DashboardConnectionMode ConnectionMode { get; init; } = DashboardConnectionMode.DevTunnel;
    public string? DevTunnelCliPath { get; init; }
    public string? AzureCliPath { get; init; }
    public Guid? DevBoxSubscriptionId { get; init; }
    public string? DevCenterName { get; init; }
    public bool AutoStartSharing { get; init; } = true;
    public bool ReceiveDetailedConversations { get; init; } = true;
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
    [JsonIgnore]
    public bool ShouldStartSharing => ConnectionMode == DashboardConnectionMode.DevTunnel && AutoStartSharing;
    internal static DashboardSettings RecoveryDefaults => new() { AutoStartSharing = false, ReceiveDetailedConversations = false };
    public static string DataDirectory => DashboardResourceLease.ValidatePath(
        Environment.GetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR") is { Length: > 0 } path
            ? path : DashboardResourceLease.DefaultDirectory);
    internal static string InstanceSuffix => "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(DashboardResourceLease.ResolveExistingDirectory(DataDirectory).ToUpperInvariant())));
    public static string FilePath => Path.Combine(DataDirectory, "dashboard-settings.json");
    public static string DatabasePath => Path.Combine(DataDirectory, "dashboard.db");

    public static DashboardSettings Load() => Load(DataDirectory);
    internal static DashboardSettings Load(string directory, bool rejectInvalidPorts = false)
    {
        var path = Path.Combine(directory, "dashboard-settings.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Settings exceed the file limit.");
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                if (bytes.Length + read > MaximumFileBytes) throw new InvalidDataException("Settings exceed the file limit.");
                bytes.Write(buffer, 0, read);
            }
            var json = new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            return FromJson(json.TrimStart('\uFEFF'), rejectInvalidPorts);
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    internal static DashboardSettings FromJson(string json, bool rejectInvalidPorts = false)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes) throw new InvalidDataException("Settings exceed the file limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateJson(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid settings.");
        if (rejectInvalidPorts)
            foreach (var name in new[] { "Port", "RpcPort" })
                if (document.RootElement.TryGetProperty(name, out var port) &&
                    (port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out var value) || value is < 1024 or > 65535))
                    throw new ArgumentException("Invalid configured listener port.");
        var settings = document.RootElement.Deserialize<DashboardSettings>() ?? throw new InvalidDataException("Empty settings.");
        Validate(settings);
        return settings with { ExtensionData = settings.ExtensionData is null ? null :
            new SettingsExtensionData(settings.ExtensionData.ToDictionary(p => p.Key, p => p.Value.Clone())) };
    }

    internal sealed class SettingsExtensionData(IDictionary<string, JsonElement> values) : ReadOnlyDictionary<string, JsonElement>(values)
    {
        public override bool Equals(object? obj) => obj is IDictionary<string, JsonElement> other &&
            Count == other.Count && this.All(pair => other.TryGetValue(pair.Key, out var value) &&
                JsonElement.DeepEquals(pair.Value, value));
        public override int GetHashCode() => Count;
    }

    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || names.Count > 128) throw new InvalidDataException("Invalid settings properties.");
                ValidateJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
    }

    internal static void Validate(DashboardSettings settings)
    {
        if (settings.Port is < 1024 or > 65535 || settings.RpcPort is < 1024 or > 65535 ||
            settings.Theme is not ("System" or "Light" or "Dark") || !Enum.IsDefined(settings.ConnectionMode) ||
            settings.AzureCliPath is { Length: > 32768 } || settings.DevTunnelCliPath is { Length: > 32768 } ||
            (settings.DevTunnelCliPath is { Length: > 0 } path &&
                (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))))
            throw new InvalidDataException("Invalid settings.");
        try { _ = AzureCliInstallation.ResolvePath(settings.AzureCliPath); }
        catch (ArgumentException) { throw new InvalidDataException("The Azure CLI path must be an absolute path to az.exe or az.cmd."); }
        try { DevBoxDiscoveryTarget.Validate(settings.DevBoxSubscriptionId, settings.DevCenterName); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid Dev Box discovery settings."); }
    }

    internal DashboardSettings WithDiscoveryTarget(string? subscriptionId, string? devCenterName)
    {
        Guid? subscription = null;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            if (!Guid.TryParse(subscriptionId.Trim(), out var parsed) || parsed == Guid.Empty)
                throw new ArgumentException("Enter a valid, nonempty subscription GUID.", nameof(subscriptionId));
            subscription = parsed;
        }
        var name = string.IsNullOrWhiteSpace(devCenterName) ? null : devCenterName.Trim();
        DevBoxDiscoveryTarget.Validate(subscription, name);
        return this with { DevBoxSubscriptionId = subscription, DevCenterName = name };
    }

    public void Save() => Save(DataDirectory);
    internal void Save(string directory)
    {
        Validate(this);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Settings exceed the file limit.");
        _ = FromJson(Encoding.UTF8.GetString(bytes));
        lock (writer)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "dashboard-settings.json");
            var pending = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(pending, path, overwrite: true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }
}

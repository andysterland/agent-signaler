using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;

namespace AgentSignaler.Configurator;

internal sealed record ConfiguratorSettings(string? LastSuccessfulDashboardUrl,
    RemoteConnectionMode SelectedConnectionMode = RemoteConnectionMode.Direct,
    string? LastSuccessfulDnsRecordName = null)
{
    internal const string SuggestedDnsRecordName = DiscoveryProtocol.DefaultRecordName;
    private static ConfiguratorSettings Read(string path)
    {
        var bytes = AtomicFile.ReadBounded(path, 8192);
        using var json = JsonDocument.Parse(bytes);
        if (IsRemovedMode(json.RootElement))
            return new(json.RootElement.TryGetProperty("lastSuccessfulDashboardUrl", out var url)
                && url.ValueKind == JsonValueKind.String ? url.GetString() : null);
        return JsonSerializer.Deserialize<ConfiguratorSettings>(bytes, Protocol.Json)
            ?? throw new InvalidDataException("Missing Configurator settings.");
    }

    private static bool IsRemovedMode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("selectedConnectionMode", out var mode) &&
        (mode.ValueKind == JsonValueKind.Number && mode.TryGetInt32(out var value) && value == 1 ||
         mode.ValueKind == JsonValueKind.String && mode.GetString() == "privateAzureDiscovery");

    internal static bool HasRemovedMode(string path)
    {
        if (!File.Exists(path)) return false;
        using var json = JsonDocument.Parse(AtomicFile.ReadBounded(path, 8192));
        return IsRemovedMode(json.RootElement);
    }
    internal static string FilePath => Path.Combine(RemotePaths.DefaultDirectory, "configurator-settings.json");

    internal static string RelayLocation(string path)
    {
        path = path.Trim();
        if (!Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } directory)
            throw new InvalidDataException("Enter an absolute path to AgentSignaler.Relay.exe.");
        return RemotePaths.ValidateRelayInstallation(directory, path);
    }

    internal static int HeartbeatSeconds(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes is < 1 or > 60 || minutes != Math.Truncate(minutes))
            throw new InvalidDataException("Heartbeat interval must be a whole number of minutes from 1 to 60.");
        return checked((int)minutes * 60);
    }

    internal static string? LoadUrl(string path, Guid machineId)
    {
        if (!File.Exists(path)) return null;
        var settings = Read(path);
        if (string.IsNullOrWhiteSpace(settings.LastSuccessfulDashboardUrl))
        {
            if (settings.SelectedConnectionMode == RemoteConnectionMode.DnsTxtDiscovery || HasRemovedMode(path)) return null;
            throw new InvalidDataException("Missing saved Dashboard URL.");
        }
        return new RemoteConfiguration { MachineId = machineId }
            .WithDashboardUrl(settings.LastSuccessfulDashboardUrl!).BaseUri.GetLeftPart(UriPartial.Authority);
    }

    internal static RemoteConnectionMode LoadMode(string path)
    {
        if (!File.Exists(path)) return RemoteConnectionMode.Direct;
        var settings = Read(path);
        // Discontinued numeric value 1 is never reinterpreted as DNS or used to read credentials.
        if ((int)settings.SelectedConnectionMode == 1) return RemoteConnectionMode.Direct;
        if (!Enum.IsDefined(settings.SelectedConnectionMode)) throw new InvalidDataException("Unsupported saved connection mode.");
        return settings.SelectedConnectionMode;
    }

    internal static string LoadDnsRecordName(string path)
    {
        if (!File.Exists(path)) return SuggestedDnsRecordName;
        var settings = Read(path);
        return settings.LastSuccessfulDnsRecordName is { } name
            ? ValidateDnsRecordName(name) : SuggestedDnsRecordName;
    }

    internal static string ValidateDnsRecordName(string name)
    {
        try
        {
            return DiscoveryProtocol.NormalizeRecordName(name.Trim());
        }
        catch (DiscoveryException)
        {
            throw new InvalidDataException("Enter a DNS hostname, such as signaler.cablesorter.io, without a URL scheme, path or credentials.");
        }
    }

    internal static void SaveUrl(string path, RemoteConfiguration configuration)
    {
        configuration.Validate();
        var previous = File.Exists(path)
            ? Read(path)
            : null;
        var settings = new ConfiguratorSettings(configuration.ConnectionMode == RemoteConnectionMode.Direct
            ? configuration.BaseUri.AbsoluteUri : previous?.LastSuccessfulDashboardUrl, configuration.ConnectionMode,
            configuration.IsDiscovery ? configuration.DnsRecordName : previous?.LastSuccessfulDnsRecordName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Protocol.Json);
        if (bytes.Length > 8192) throw new InvalidDataException("Saved dashboard URL is too long.");
        AtomicFile.Write(path, bytes);
    }
}

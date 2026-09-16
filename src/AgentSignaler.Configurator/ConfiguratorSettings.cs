using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;

namespace AgentSignaler.Configurator;

internal sealed record ConfiguratorSettings(string LastSuccessfulDashboardUrl)
{
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
        var settings = JsonSerializer.Deserialize<ConfiguratorSettings>(AtomicFile.ReadBounded(path, 8192), Protocol.Json)
            ?? throw new InvalidDataException("Missing Configurator settings.");
        return new RemoteConfiguration { MachineId = machineId }
            .WithDashboardUrl(settings.LastSuccessfulDashboardUrl).BaseUri.GetLeftPart(UriPartial.Authority);
    }

    internal static void SaveUrl(string path, RemoteConfiguration configuration)
    {
        var settings = new ConfiguratorSettings(configuration.BaseUri.AbsoluteUri);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Protocol.Json);
        if (bytes.Length > 8192) throw new InvalidDataException("Saved dashboard URL is too long.");
        AtomicFile.Write(path, bytes);
    }
}

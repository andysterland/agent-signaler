using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;

namespace AgentSignaler.Remote;

public enum RemoteConnectionMode { Direct, PrivateAzureDiscovery }

[JsonConverter(typeof(RemoteConfigurationConverter))]
public sealed record RemoteConfiguration
{
    public int Version { get; init; } = 1;
    public string Host { get; init; } = "";
    public int Port { get; init; } = Protocol.DefaultPort;
    public string? DashboardBaseUrl { get; init; }
    public RemoteConnectionMode ConnectionMode { get; init; }
    public DiscoveryCredentialReference? Discovery { get; init; }
    public Guid DiscoveryId { get; init; }
    public string? DiscoverySigningPublicKey { get; init; }
    public long DiscoveryCredentialRevision { get; init; }
    public Guid MachineId { get; init; }
    public string MachineName { get; init; } = Environment.MachineName;
    public string ClientVersion { get; init; } = "unknown";
    public string? RelayPath { get; init; }
    public int HeartbeatIntervalSeconds { get; init; } = PresenceProtocol.DefaultHeartbeatIntervalSeconds;
    public IReadOnlyList<IntegrationTarget> Integrations { get; init; } = [];
    public bool DetailedReportingEnabled { get; init; } = true;

    public override string ToString() => $"Remote configuration v{Version} ({ConnectionMode})";

    [JsonIgnore]
    public bool IsDetailedReportingEnabled => Version >= 5 && DetailedReportingEnabled;

    [JsonIgnore]
    public bool IsDiscovery => Version == 6 && ConnectionMode == RemoteConnectionMode.PrivateAzureDiscovery;

    [JsonIgnore]
    public Uri BaseUri
    {
        get
        {
            Validate();
            if (ConnectionMode == RemoteConnectionMode.PrivateAzureDiscovery)
                throw new InvalidOperationException("Private discovery requires explicit endpoint resolution.");
            return Version == 1 ? new UriBuilder(Uri.UriSchemeHttp, Host, Port).Uri :
                ParseDashboardUrl(DashboardBaseUrl!);
        }
    }

    [JsonIgnore]
    public Uri Endpoint => new(BaseUri, "/api/v1/status");

    [JsonIgnore]
    public Uri HealthEndpoint => new(BaseUri, "/health");

    [JsonIgnore]
    public Uri PresenceHealthEndpoint => new(BaseUri, "/api/v2/health");

    public RemoteConfiguration WithDashboardUrl(string url)
    {
        var endpoint = ParseDashboardUrl(url);
        var config = this with
        {
            Version = Version >= 3 ? Version : 2, DashboardBaseUrl = endpoint.AbsoluteUri, Host = "", Port = Protocol.DefaultPort,
            ConnectionMode = RemoteConnectionMode.Direct, Discovery = null, DiscoveryId = Guid.Empty,
            DiscoverySigningPublicKey = null, DiscoveryCredentialRevision = 0
        };
        config.Validate();
        return config;
    }

    public RemoteConfiguration ToVersion2() => Version >= 6 ? this : WithDashboardUrl(BaseUri.AbsoluteUri);
    public RemoteConfiguration ToVersion3() => Version >= 6 ? this : WithDashboardUrl(BaseUri.AbsoluteUri) with { Version = Math.Max(Version, 3) };
    public RemoteConfiguration ToVersion4() => Version >= 6 ? this : WithDashboardUrl(BaseUri.AbsoluteUri) with { Version = Math.Max(Version, 4) };
    public RemoteConfiguration ToVersion5() => Version >= 6
        ? throw new InvalidDataException("Version 6 cannot be downgraded. Explicitly convert to a verified direct connection first.")
        : WithDashboardUrl(BaseUri.AbsoluteUri) with { Version = 5 };

    public RemoteConfiguration WithDiscovery(DiscoveryCredentialReference credential, Guid discoveryId,
        string signingPublicKey, long credentialRevision)
    {
        var config = this with { Version = 6, Host = "", Port = Protocol.DefaultPort, DashboardBaseUrl = null,
            ConnectionMode = RemoteConnectionMode.PrivateAzureDiscovery, Discovery = credential,
            DiscoveryId = discoveryId, DiscoverySigningPublicKey = signingPublicKey,
            DiscoveryCredentialRevision = credentialRevision };
        config.Validate();
        return config;
    }

    public RemoteConfiguration ToVersion6()
    {
        Validate();
        return Version >= 6 ? this : WithDashboardUrl(BaseUri.AbsoluteUri) with { Version = 6 };
    }

    private static Uri ParseDashboardUrl(string url)
    {
        var text = url?.Trim() ?? "";
        var authorityStart = text.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = text.IndexOf('/', Math.Min(authorityStart, text.Length));
        if (url is null || url.Any(char.IsControl) || text.IndexOfAny(['\\', '?', '#', '@']) >= 0 ||
            !Uri.TryCreate(text, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || string.IsNullOrEmpty(endpoint.Host) ||
            endpoint.Port < (endpoint.Scheme == Uri.UriSchemeHttps ? 1 : 1024) || endpoint.Port > 65535 ||
            (pathStart >= 0 && text[pathStart..] != "/") || endpoint.AbsolutePath != "/" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidDataException("Enter a root HTTPS URL (default port 443 or explicit port 1-65535), " +
                "or a trusted LAN HTTP URL with port 1024-65535. Paths, credentials, queries and fragments are not allowed.");
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/");
    }

    public void Validate()
    {
        if (Version is not (1 or 2 or 3 or 4 or 5 or 6) || MachineId == Guid.Empty)
            throw new InvalidDataException("Invalid configuration version or identity.");
        if (!Enum.IsDefined(ConnectionMode) || (Version < 6 && (ConnectionMode != RemoteConnectionMode.Direct || Discovery is not null ||
            DiscoveryId != Guid.Empty || DiscoverySigningPublicKey is not null || DiscoveryCredentialRevision != 0)))
            throw new InvalidDataException("Connection modes require configuration version 6.");
        if (Version == 1 && (DashboardBaseUrl is not null || Port is < 1024 or > 65535 ||
            string.IsNullOrWhiteSpace(Host) || Host != Host.Trim() ||
            Host.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0 ||
            Host.Any(char.IsControl) ||
            Uri.CheckHostName(Host.Trim('[', ']')) == UriHostNameType.Unknown))
            throw new InvalidDataException("Invalid endpoint or identity.");
        if (Version >= 2)
        {
            if (Host != "" || Port != Protocol.DefaultPort)
                throw new InvalidDataException("Modern configuration does not accept legacy Host or Port fields.");
            if (ConnectionMode == RemoteConnectionMode.PrivateAzureDiscovery)
            {
                if (DashboardBaseUrl is not null || Discovery is null)
                    throw new InvalidDataException("Discovery requires a protected credential reference and no direct URL.");
                if (Discovery.Id is null || Discovery.Id.Length != 32 || !Guid.TryParseExact(Discovery.Id, "N", out var slot) || slot == Guid.Empty || Discovery.Id != slot.ToString("N") ||
                    Discovery.CiphertextHash is null || Discovery.CiphertextHash.Length != 64 ||
                    Discovery.CiphertextHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || DiscoveryId == Guid.Empty ||
                    DiscoveryCredentialRevision <= 0 || string.IsNullOrEmpty(DiscoverySigningPublicKey) || DiscoverySigningPublicKey.Length > 256)
                    throw new InvalidDataException("Invalid protected discovery reference or trust metadata.");
                try
                {
                    using var key = System.Security.Cryptography.ECDsa.Create();
                    var keyBytes = Convert.FromBase64String(DiscoverySigningPublicKey);
                    key.ImportSubjectPublicKeyInfo(keyBytes, out var consumed);
                    if (consumed != keyBytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                        throw new InvalidDataException("Invalid discovery verification key.");
                }
                catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
                { throw new InvalidDataException("Invalid discovery verification key."); }
            }
            else if (Discovery is not null || DiscoveryId != Guid.Empty || DiscoverySigningPublicKey is not null || DiscoveryCredentialRevision != 0 || DashboardBaseUrl is null ||
                ParseDashboardUrl(DashboardBaseUrl).AbsoluteUri != DashboardBaseUrl)
                throw new InvalidDataException("Direct connections require only a canonical DashboardBaseUrl.");
        }
        if (!ValidText(MachineName, 128) || !ValidText(ClientVersion, 64))
            throw new InvalidDataException("Invalid machine metadata.");
        if (!PresenceProtocol.IsValidHeartbeatInterval(HeartbeatIntervalSeconds) ||
            (Version < 3 && HeartbeatIntervalSeconds != PresenceProtocol.DefaultHeartbeatIntervalSeconds))
            throw new InvalidDataException("Heartbeat interval must be a whole number of minutes from 1 to 60; custom intervals require version 3.");
        if (RelayPath is not null) RemotePaths.ValidateRelayPath(RelayPath);
        if (Integrations is null || Integrations.Count > 64 || (Version < 4 && Integrations.Count != 0) ||
            Integrations.Any(t => t is null || !ValidText(t.Id, 128) || !ValidText(t.ScopeId, 128) ||
                t.Kind is not ("copilot-cli" or "visual-studio" or "vscode") ||
                !Path.IsPathFullyQualified(t.HookDirectory) ||
                (t.SettingsPath is not null && !Path.IsPathFullyQualified(t.SettingsPath))) ||
            Integrations.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != Integrations.Count)
            throw new InvalidDataException("Invalid version 4 integration collection.");
        foreach (var target in Integrations)
        {
            target.Validate();
            if (target.Reason is null || target.Provenance is null ||
                target.Reason.Length > 2048 || target.Provenance.Length > 256)
                throw new InvalidDataException("Integration diagnostics exceed configuration limits.");
        }
    }

    internal static bool ValidText(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    public static RemoteConfiguration Load(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Absolute config path required.");
        var bytes = AtomicFile.ReadBounded(path, 262144);
        var config = JsonSerializer.Deserialize<RemoteConfiguration>(bytes, Protocol.Json)
            ?? throw new InvalidDataException("Missing configuration.");
        config.Validate();
        return config;
    }
}

public sealed class RemoteConfigurationConverter : JsonConverter<RemoteConfiguration>
{
    public override RemoteConfiguration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Configuration must be a JSON object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (field.Name.ToLowerInvariant() is not ("version" or "host" or "port" or "dashboardbaseurl" or
                "machineid" or "machinename" or "clientversion" or "relaypath" or "heartbeatintervalseconds" or "integrations" or
                "detailedreportingenabled" or "connectionmode" or "discovery" or "discoveryid" or "discoverysigningpublickey" or "discoverycredentialrevision"))
                throw new InvalidDataException("Unknown configuration field.");
            if (!fields.TryAdd(field.Name, field.Value))
                throw new InvalidDataException("Duplicate configuration fields are not allowed.");
        }
        var version = fields.TryGetValue("version", out var v) ? v.GetInt32() : 1;
        if (version is not (1 or 2 or 3 or 4 or 5 or 6) ||
            (version == 1 && fields.ContainsKey("dashboardBaseUrl")) ||
            (version >= 2 && (fields.ContainsKey("host") || fields.ContainsKey("port"))) ||
            (version < 3 && fields.ContainsKey("heartbeatIntervalSeconds")) ||
            (version < 4 && fields.ContainsKey("integrations")) ||
            (version < 5 && fields.ContainsKey("detailedReportingEnabled")) ||
            (version < 6 && (fields.ContainsKey("connectionMode") || fields.ContainsKey("discovery") ||
                fields.ContainsKey("discoveryId") || fields.ContainsKey("discoverySigningPublicKey") || fields.ContainsKey("discoveryCredentialRevision"))) ||
            (version == 6 && !fields.ContainsKey("connectionMode")))
            throw new InvalidDataException("Unsupported or mixed configuration schema.");
        if (fields.TryGetValue("detailedReportingEnabled", out var preference) &&
            preference.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("DetailedReportingEnabled must be a boolean.");
        var config = new RemoteConfiguration
        {
            Version = version,
            Host = fields.TryGetValue("host", out var host) ? host.GetString()! : "",
            Port = fields.TryGetValue("port", out var port) ? port.GetInt32() : Protocol.DefaultPort,
            DashboardBaseUrl = fields.TryGetValue("dashboardBaseUrl", out var url) ? url.GetString() : null,
            ConnectionMode = fields.TryGetValue("connectionMode", out var mode) ? mode.GetString() switch
            {
                "direct" => RemoteConnectionMode.Direct,
                "privateAzureDiscovery" => RemoteConnectionMode.PrivateAzureDiscovery,
                _ => throw new InvalidDataException("Unsupported connection mode.")
            } : RemoteConnectionMode.Direct,
            Discovery = fields.TryGetValue("discovery", out var discovery)
                ? ReadDiscoveryReference(discovery) : null,
            DiscoveryId = fields.TryGetValue("discoveryId", out var discoveryId) ? discoveryId.GetGuid() : Guid.Empty,
            DiscoverySigningPublicKey = fields.TryGetValue("discoverySigningPublicKey", out var publicKey) ? publicKey.GetString() : null,
            DiscoveryCredentialRevision = fields.TryGetValue("discoveryCredentialRevision", out var credentialRevision) ? credentialRevision.GetInt64() : 0,
            MachineId = fields.TryGetValue("machineId", out var id) ? id.GetGuid() : Guid.Empty,
            MachineName = fields.TryGetValue("machineName", out var name) ? name.GetString()! : Environment.MachineName,
            ClientVersion = fields.TryGetValue("clientVersion", out var client) ? client.GetString()! : "unknown",
            RelayPath = fields.TryGetValue("relayPath", out var relay) ? relay.GetString() : null,
            HeartbeatIntervalSeconds = fields.TryGetValue("heartbeatIntervalSeconds", out var interval) ? interval.GetInt32() : 300,
            DetailedReportingEnabled = !fields.TryGetValue("detailedReportingEnabled", out var details) || details.GetBoolean(),
            Integrations = fields.TryGetValue("integrations", out var integrations)
                ? integrations.Deserialize<IntegrationTarget[]>(options) ?? throw new InvalidDataException("Missing integrations.") : []
        };
        if (version == 6 && (config.IsDiscovery
            ? fields.ContainsKey("dashboardBaseUrl") || !fields.ContainsKey("discovery") || !fields.ContainsKey("discoveryId") ||
                !fields.ContainsKey("discoverySigningPublicKey") || !fields.ContainsKey("discoveryCredentialRevision")
            : fields.ContainsKey("discovery") || fields.ContainsKey("discoveryId") || fields.ContainsKey("discoverySigningPublicKey") || fields.ContainsKey("discoveryCredentialRevision")))
            throw new InvalidDataException("Mixed direct and discovery configuration fields.");
        config.Validate();
        return config;
    }

    private static DiscoveryCredentialReference ReadDiscoveryReference(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid protected discovery reference.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in element.EnumerateObject())
            if (field.Name.ToLowerInvariant() is not ("id" or "ciphertexthash") || !fields.TryAdd(field.Name, field.Value))
                throw new InvalidDataException("Invalid or duplicate protected discovery reference fields.");
        if (!fields.TryGetValue("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !fields.TryGetValue("ciphertextHash", out var hash) || hash.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing protected discovery reference fields.");
        return new(id.GetString()!, hash.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, RemoteConfiguration value, JsonSerializerOptions options)
    {
        value.Validate();
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version);
        if (value.Version == 1)
        {
            writer.WriteString("host", value.Host);
            writer.WriteNumber("port", value.Port);
        }
        else if (value.ConnectionMode == RemoteConnectionMode.Direct) writer.WriteString("dashboardBaseUrl", value.DashboardBaseUrl);
        if (value.Version >= 6)
        {
            writer.WriteString("connectionMode", value.ConnectionMode == RemoteConnectionMode.Direct ? "direct" : "privateAzureDiscovery");
            if (value.Discovery is not null)
            {
                writer.WritePropertyName("discovery");
                JsonSerializer.Serialize(writer, value.Discovery, options);
                writer.WriteString("discoveryId", value.DiscoveryId);
                writer.WriteString("discoverySigningPublicKey", value.DiscoverySigningPublicKey);
                writer.WriteNumber("discoveryCredentialRevision", value.DiscoveryCredentialRevision);
            }
        }
        writer.WriteString("machineId", value.MachineId);
        writer.WriteString("machineName", value.MachineName);
        writer.WriteString("clientVersion", value.ClientVersion);
        if (value.RelayPath is not null) writer.WriteString("relayPath", value.RelayPath);
        if (value.Version >= 3) writer.WriteNumber("heartbeatIntervalSeconds", value.HeartbeatIntervalSeconds);
        if (value.Version >= 4)
        {
            writer.WritePropertyName("integrations");
            JsonSerializer.Serialize(writer, value.Integrations, options);
        }
        if (value.Version >= 5) writer.WriteBoolean("detailedReportingEnabled", value.DetailedReportingEnabled);
        writer.WriteEndObject();
    }
}

public static class RemotePaths
{
    internal static void ValidateRelayPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) ||
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            !string.Equals(Path.GetFileName(path), "AgentSignaler.Relay.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RelayPath must be an absolute path to AgentSignaler.Relay.exe.");
    }

    public static string ValidateRelayInstallation(string applicationDirectory, string? configuredRelayPath)
    {
        var expectedPath = Path.Combine(applicationDirectory, "AgentSignaler.Relay.exe");
        ValidateRelayPath(expectedPath);
        expectedPath = Path.GetFullPath(expectedPath);
        if (configuredRelayPath is not null)
        {
            ValidateRelayPath(configuredRelayPath);
            if (!string.Equals(Path.GetFullPath(configuredRelayPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Configured relay location does not match this installation. " +
                    $"Set relayPath to '{expectedPath}' in remote.json before previewing changes.");
        }
        if (!File.Exists(expectedPath))
            throw new InvalidDataException($"Relay executable is missing: '{expectedPath}'. " +
                "Repair the selected Agent Signaler Remote installation or choose an existing relay location.");
        return expectedPath;
    }

    public static string DefaultDirectory => ResolveDirectory(Environment.GetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR"));
    public static string ResolveDirectory(string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSignaler");
        if (!Path.IsPathFullyQualified(overridePath))
            throw new InvalidDataException("AGENT_SIGNALER_DATA_DIR must be an absolute path.");
        return Path.GetFullPath(overridePath);
    }
    public static string DefaultConfig => Path.Combine(DefaultDirectory, "remote.json");
    public static string Identity(string directory) => Path.Combine(directory, "machine-id");
    public static string State(string config) => Path.Combine(Path.GetDirectoryName(config)!, "sessions.json");
    public static string Log(string config) => Path.Combine(Path.GetDirectoryName(config)!, "relay.log");
}

public static class AtomicFile
{
    public static byte[] ReadBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > maxBytes) throw new InvalidDataException("File too large.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(pending, path, true);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    public static FileStream Acquire(string path, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed < timeout) { Thread.Sleep(10); }
        }
    }
}

public static class MachineIdentity
{
    public static Guid GetOrCreate(string directory)
    {
        var path = RemotePaths.Identity(directory);
        using var held = AtomicFile.Acquire(path + ".lock", TimeSpan.FromSeconds(1));
        if (File.Exists(path))
        {
            var text = System.Text.Encoding.UTF8.GetString(AtomicFile.ReadBounded(path, 128));
            if (Guid.TryParse(text, out var existing) && existing != Guid.Empty) return existing;
            throw new InvalidDataException("Identity file is invalid; restore it or explicitly purge identity.");
        }
        var id = Guid.NewGuid();
        AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(id.ToString("D")));
        return id;
    }
}

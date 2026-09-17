using System.Text.Json.Serialization;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

/// <summary>Volatile, local IPC metadata. Never serialize into HTTP, settings, or diagnostics.</summary>
public sealed record LocalTranscriptReference(
    SourceDescriptor Source,
    string SessionId,
    DateTimeOffset StopTimestampUtc,
    string CaptureId,
    long SettingsRevision,
    string Path)
{
    [JsonIgnore]
    public bool IsValid => Source is { IsValid: true } &&
        ValidIdentity(SessionId, 128) && ValidIdentity(CaptureId, 128) &&
        StopTimestampUtc != default && StopTimestampUtc.Offset == TimeSpan.Zero &&
        SettingsRevision >= 0 && !string.IsNullOrWhiteSpace(Path) &&
        Path.Length <= 512 && !Path.Any(char.IsControl);

    internal static bool ValidIdentity(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    public override string ToString() => nameof(LocalTranscriptReference);
}

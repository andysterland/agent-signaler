using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

/// <summary>Reads only the local Copilot workspace name, never a hook-supplied path or transcript.</summary>
public static class SessionDisplayNameReader
{
    public const int MaximumMetadataBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Uses only an exact verified configured source/scope. Legacy configurations without
    /// integrations and unmatched sources return null; there is no ambient home-directory fallback.
    /// Local disk reads are byte-bounded and synchronous, with cancellation checked between reads.
    /// </summary>
    public static string? Read(RemoteConfiguration configuration, SourceDescriptor? source,
        string sessionId, DiagnosticLog log, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is not { IsValid: true, Kind: "copilot-cli" or "visual-studio" }) return null;
        try
        {
            if (configuration.Integrations.Count > 64) throw new InvalidDataException();
            string? home = null;
            foreach (var target in configuration.Integrations)
            {
                if (target.Kind != source.Kind || target.ScopeId != source.ScopeId || !target.CanInstall) continue;
                target.Validate();
                var hooks = IntegrationScope.CanonicalDirectory(target.HookDirectory);
                if (!string.Equals(Path.GetFileName(hooks), "hooks", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                var candidate = Path.GetDirectoryName(hooks)!;
                if (home is not null && !string.Equals(home, candidate, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                home = candidate;
            }
            if (home is null) return null;
            if (!Guid.TryParseExact(sessionId, "D", out var session) || session == Guid.Empty)
                throw new InvalidDataException();
            var root = Path.Combine(home, "session-state", sessionId);
            var path = Path.Combine(root, "workspace.yaml");
            var adapter = new MetadataFileAdapter(source, root, sessionId);
            var reference = new LocalTranscriptReference(source, sessionId, DateTimeOffset.UnixEpoch,
                "workspace-name", 0, path);
            if (!TranscriptFileBoundary.IsExpectedPath(reference, adapter)) throw new InvalidDataException();
            // Validate every ancestor before querying the file; ordinary path APIs follow reparses.
            using var file = TranscriptFileBoundary.Open(reference, adapter);
            var bytes = new byte[MaximumMetadataBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = file.Stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaximumMetadataBytes) throw new InvalidDataException();
            return Parse(StrictUtf8.GetString(bytes, 0, count), sessionId);
        }
        catch (TranscriptFileBoundaryException ex) when (ex.NativeError is 2 or 3)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or JsonException or ArgumentException)
        {
            log.Write("session-name-invalid");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write("session-name-unavailable");
            return null;
        }
    }

    private static string? Parse(string text, string sessionId)
    {
        // Verified CLI and VS runtimes store id/name in a flat workspace.yaml mapping.
        // This intentionally supports only its scalar forms, not general YAML objects/tags/aliases.
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? id = null, name = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon < 1 || line[..colon].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
                (line.Length > colon + 1 && line[colon + 1] != ' '))
                throw new InvalidDataException();
            var key = line[..colon];
            if (!keys.Add(key)) throw new InvalidDataException();
            var scalar = line[(colon + 1)..].Trim();
            var continuation = new List<string>();
            while (index + 1 < lines.Length && lines[index + 1].StartsWith(' '))
            {
                var next = lines[++index];
                if (!next.StartsWith("  ", StringComparison.Ordinal)) throw new InvalidDataException();
                continuation.Add(next[2..]);
            }
            var value = ParseScalar(scalar, continuation);
            if (key == "id") id = value;
            else if (key == "name") name = value;
        }
        if (!string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        if (name is null) return null;
        if (!Protocol.ValidDisplayName(name)) throw new InvalidDataException();
        return name;
    }

    private static string? ParseScalar(string scalar, List<string> continuation)
    {
        if (scalar is "|-" or ">-")
        {
            if (continuation.Count == 0) throw new InvalidDataException();
            return string.Join(scalar == "|-" ? "\n" : " ", continuation);
        }
        if (continuation.Count != 0) throw new InvalidDataException();
        if (scalar is "" or "null" or "~") return null;
        if (scalar[0] == '"')
            return JsonSerializer.Deserialize<string>(scalar);
        if (scalar[0] == '\'')
        {
            if (scalar.Length < 2 || scalar[^1] != '\'') throw new InvalidDataException();
            var result = new StringBuilder();
            for (var index = 1; index < scalar.Length - 1; index++)
            {
                var value = scalar[index];
                if (value == '\'' && (++index >= scalar.Length - 1 || scalar[index] != '\''))
                    throw new InvalidDataException();
                result.Append(value);
            }
            return result.ToString();
        }
        if (scalar[0] is '!' or '&' or '*' or '[' or ']' or '{' or '}' or '|' or '>' or '@' or '`' or '#' ||
            scalar.Contains(": ", StringComparison.Ordinal) || scalar.StartsWith("- ", StringComparison.Ordinal) ||
            scalar.StartsWith("? ", StringComparison.Ordinal) || scalar is "-" or "?" or ":")
            throw new InvalidDataException();
        var comment = scalar.IndexOf(" #", StringComparison.Ordinal);
        return comment < 0 ? scalar : scalar[..comment].TrimEnd();
    }

    // Reuse the handle-based file boundary without registering or enabling a transcript format.
    private sealed class MetadataFileAdapter(SourceDescriptor source, string root, string sessionId) : ITranscriptFileAdapter
    {
        public string ProfileId => "copilot-workspace-name";
        public SourceDescriptor Source => source;
        public string TranscriptRoot => root;
        public string GetExpectedFileName(string id) => id == sessionId ? "workspace.yaml" : "";
        public TranscriptFileRecord ParseRecord(ReadOnlySpan<byte> utf8Record, string id) =>
            throw new NotSupportedException();
    }
}

using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace AgentSignaler.Updates;

public sealed class UpdateHandoffReady
{
    public Guid RunId { get; }
    public UpdateProcessIdentity Updater { get; }
    internal UpdateHandoffReady(Guid runId, UpdateProcessIdentity updater) { RunId = runId; Updater = updater; }
}

public sealed record UpdateScriptPin(string Name, long Size, string Sha256);
public sealed record UpdateDependencyFile(string Name, string Sha256);
public sealed record UpdateDependencyManifest(int SchemaVersion, int PolicyVersion, ImmutableArray<UpdateDependencyFile> Files);
public interface IUpdateScriptTrust
{
    // Must verify trusted timestamped Authenticode publisher policy, not merely a hash or trusted leaf.
    Task VerifyAsync(string path, CancellationToken cancellationToken);
}

public interface IUpdateHandoffAdapter
{
    Task<UpdateProcessIdentity> LaunchAsync(string entryScript, string manifestPath, string pipeName, CancellationToken cancellationToken);
    Task WaitForReadyAsync(UpdateProcessIdentity updater, Guid runId, string nonce, string pipeName, CancellationToken cancellationToken);
}

public sealed record UpdateHandoffRequest(UpdateInstalledPackage Installation, ReleaseCandidate Candidate,
    ImmutableArray<UpdateScriptPin> Scripts, string ChecksumSha256, string PackageSha256);

// Wire DTO intentionally separates package/version strings from the common discovery cache model.
public sealed record UpdateHandoffManifest(int SchemaVersion, string RunId, string Nonce, string Package,
    string InstallDirectory, string ProductCode, string InstalledVersion, long ReleaseId, string Tag,
    ImmutableArray<ReleaseAsset> Assets, string ChecksumSha256, string PackageSha256,
    UpdateProcessIdentity Parent, ImmutableArray<UpdateInstanceRegistration> Participants,
    ImmutableArray<UpdateScriptPin> Scripts, string ReadinessPipe, DateTimeOffset ExpiresAt);

public sealed class UpdateHandoffLauncher(IUpdateStorage storage, IUpdateProcessAdapter processes,
    IUpdateScriptTrust trust, IUpdateHandoffAdapter adapter, TimeProvider timeProvider)
{
    public static readonly ImmutableArray<string> RequiredScripts = UpdateHandoffPolicy.RequiredScripts;

    public async Task<ImmutableArray<UpdateScriptPin>> ReadInstalledPinsAsync(UpdateInstalledPackage installation,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(installation.InstallDirectory, "UpdaterDependencies.json");
        UpdatePath.RequireNoReparsePoints(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 65536) throw new InvalidDataException("update_dependency_limit");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var manifest = UpdateStrictJson.Read<UpdateDependencyManifest>(bytes.AsSpan(offset));
        if (manifest.SchemaVersion != UpdateHandoffPolicy.SchemaVersion || manifest.PolicyVersion != UpdateHandoffPolicy.DependencyPolicyVersion || manifest.Files.IsDefault || manifest.Files.Length != 3 ||
            manifest.Files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("update_dependency_schema");
        var result = ImmutableArray.CreateBuilder<UpdateScriptPin>();
        foreach (var pin in manifest.Files)
        {
            if (!RequiredScripts.Contains(pin.Name, StringComparer.Ordinal) || !IsHash(pin.Sha256))
                throw new InvalidDataException("update_dependency_identity");
            var script = Path.Combine(installation.InstallDirectory, pin.Name);
            UpdatePath.RequireNoReparsePoints(script);
            using var held = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (held.Length is < 1 or > 1048576 || !string.Equals(Convert.ToHexString(SHA256.HashData(held)), pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("update_dependency_changed");
            await trust.VerifyAsync(script, cancellationToken).ConfigureAwait(false);
            result.Add(new(pin.Name, held.Length, pin.Sha256));
        }
        return result.ToImmutable();
    }

    public async Task<UpdateHandoffReady> LaunchAsync(UpdateMaintenanceTicket ticket, UpdateHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicy.ValidateCandidate(request.Candidate);
        var installed = request.Installation;
        if (installed != ticket.Installation || request.Candidate.Version.CompareTo(new(installed.Version.Major, installed.Version.Minor, installed.Version.Build)) <= 0 ||
            request.Scripts.IsDefault || request.Scripts.Length != RequiredScripts.Length ||
            request.Scripts.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != RequiredScripts.Length ||
            !IsHash(request.ChecksumSha256) || !IsHash(request.PackageSha256))
            throw new InvalidDataException("update_handoff_invalid");
        foreach (var participant in ticket.Participants) UpdateMaintenanceValidation.Registration(participant, installed);
        var runDirectory = storage.CreateRunDirectory(ticket.RunId);
        var held = new List<FileStream>();
        try
        {
            foreach (var pin in request.Scripts)
            {
                if (!RequiredScripts.Contains(pin.Name, StringComparer.Ordinal) || pin.Size is < 1 or > 1048576 || !IsHash(pin.Sha256))
                    throw new InvalidDataException("update_script_pin");
                var source = Path.Combine(installed.InstallDirectory, pin.Name);
                UpdatePath.RequireNoReparsePoints(source);
                // No write/delete sharing: exact installed bytes and trust remain pinned throughout staging.
                var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(stream);
                if (stream.Length != pin.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), pin.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("update_script_changed");
                await trust.VerifyAsync(source, cancellationToken).ConfigureAwait(false);
                stream.Position = 0;
                var destination = Path.Combine(runDirectory, pin.Name);
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    output.Flush(true);
                }
                UpdatePath.RequireNoReparsePoints(destination);
                var staged = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(staged);
                if (staged.Length != pin.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(staged)), pin.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("update_script_changed");
                await trust.VerifyAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var pipe = "AgentSignaler.Update.Ready.v1." + nonce;
            var manifest = new UpdateHandoffManifest(1, ticket.RunId.ToString("N"), nonce, installed.Package.ToString(),
                installed.InstallDirectory, installed.ProductCode.ToString("B").ToUpperInvariant(), installed.Version.ToString(3),
                request.Candidate.Id, request.Candidate.Tag, request.Candidate.Assets, request.ChecksumSha256, request.PackageSha256,
                processes.Current, ticket.Participants, request.Scripts, pipe, timeProvider.GetUtcNow() + UpdateMaintenanceValidation.OperationLease);
            var manifestPath = Path.Combine(runDirectory, "handoff.json");
            // Child run directory inherits only current-user permissions from its protected parent.
            var manifestBytes = UpdateStrictJson.Write(manifest);
            using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await output.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false); output.Flush(true); }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            var updater = await adapter.LaunchAsync(Path.Combine(runDirectory, RequiredScripts[0]), manifestPath, pipe, budget.Token).ConfigureAwait(false);
            UpdateMaintenanceValidation.Process(updater);
            var parent = processes.Current;
            if (updater.UserSid != parent.UserSid || updater.SessionId != parent.SessionId ||
                !UpdateMaintenanceValidation.SameProcess(processes.Find(updater.ProcessId), updater))
                throw new UnauthorizedAccessException("update_updater_identity");
            await adapter.WaitForReadyAsync(updater, ticket.RunId, nonce, pipe, budget.Token).ConfigureAwait(false);
            return new(ticket.RunId, updater);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("update_handoff_timeout"); }
        finally { foreach (var stream in held) stream.Dispose(); }
    }

    public static bool IsHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record UpdateHandoffAcknowledgement(int SchemaVersion, string RunId, string Nonce, string State);

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateHandoffAdapter(IUpdateProcessAdapter processes) : IUpdateHandoffAdapter, IDisposable
{
    private NamedPipeServerStream? readiness;
    public Task<UpdateProcessIdentity> LaunchAsync(string entryScript, string manifestPath, string pipeName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (readiness is not null) throw new InvalidOperationException("update_handoff_busy");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var executable = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("update_requires_x64");
        UpdatePath.RequireNoReparsePoints(executable);
        readiness = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 4096, 4096);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(entryScript)! };
        var inherited = new[] { "SystemRoot", "WINDIR", "COMSPEC", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP",
            "PATH", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA", "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN" };
        start.Environment.Clear();
        foreach (var key in inherited)
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["PSModulePath"] = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "Modules");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", entryScript, "-AppHandoff", manifestPath }) start.ArgumentList.Add(argument);
        // Independent process: no job, no execution-policy bypass, no inline command, no environment secrets.
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("update_launch_failed");
            var identity = processes.Find(process.Id) ?? throw new IOException("update_launch_exited");
            if (!string.Equals(identity.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("update_launch_identity");
            return Task.FromResult(identity);
        }
        catch { readiness.Dispose(); readiness = null; throw; }
    }

    public async Task WaitForReadyAsync(UpdateProcessIdentity updater, Guid runId, string nonce, string pipeName, CancellationToken cancellationToken)
    {
        var pipe = readiness ?? throw new InvalidOperationException("update_launch_missing");
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!WindowsUpdateMaintenanceTransport.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != updater.ProcessId ||
                !UpdateMaintenanceValidation.SameProcess(processes.Find((int)pid), updater))
                throw new UnauthorizedAccessException("update_readiness_identity");
            var reply = await UpdatePipeFrame.ReadAsync<UpdateHandoffAcknowledgement>(pipe, cancellationToken).ConfigureAwait(false);
            if (reply.SchemaVersion != 1 || reply.RunId != runId.ToString("N") || reply.Nonce != nonce || reply.State != "ready")
                throw new InvalidDataException("update_readiness_invalid");
        }
        finally { pipe.Dispose(); readiness = null; }
    }

    public void Dispose() { readiness?.Dispose(); readiness = null; }
}
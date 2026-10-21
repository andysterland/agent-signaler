using System.Collections.Immutable;
using System.Text;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class UpdateHandoffRecoveryTests
{
    private static readonly Guid Run = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static UpdateDurableResult Result => new(1, Run.ToString("N"), UpdateStage.WaitingForExit,
        UpdateServicingOutcome.Pending, UpdateInstallerState.NotStarted, null, null, null, "1.2.4", null, []);

    [Fact]
    public void NotStartedJournalRemainsPendingNotSuccessful()
    {
        var result = UpdateHandoffRecovery.ReadResult(UpdateStrictJson.Write(Result), Run, "v1.2.4");
        Assert.Equal(UpdateInstallerState.NotStarted, result.MsiState);
        Assert.Equal(UpdateServicingOutcome.Pending, result.Outcome);
        Assert.Null(result.InstallerExitCode);
    }

    [Theory]
    [InlineData(UpdateInstallerState.Starting, false)]
    [InlineData(UpdateInstallerState.Running, true)]
    public void InterruptedInstallerStateIsPreserved(UpdateInstallerState state, bool identityKnown)
    {
        var pending = Result with { Stage = UpdateStage.Installing, MsiState = state,
            InstallerProcessId = identityKnown ? 42 : null, InstallerCreationTimeUtcTicks = identityKnown ? 1000L : null };
        var actual = UpdateHandoffRecovery.ReadResult(UpdateStrictJson.Write(pending), Run, "v1.2.4");
        Assert.Equal(state, actual.MsiState);
        Assert.Equal(UpdateServicingOutcome.Pending, actual.Outcome);
        Assert.Null(actual.InstallerExitCode);
    }

    [Fact]
    public void RebootRequiredCannotClaimAppsRestarted()
    {
        var reboot = Result with { Stage = UpdateStage.RebootRequired, Outcome = UpdateServicingOutcome.RebootRequired,
            MsiState = UpdateInstallerState.Completed, InstallerExitCode = 3010, InstallerProcessId = 42, InstallerCreationTimeUtcTicks = 1000,
            Restarts = [new(UpdateApplication.Client, 50, UpdateRestartState.Ready)] };
        Assert.Throws<InvalidDataException>(() => UpdateHandoffRecovery.ReadResult(UpdateStrictJson.Write(reboot), Run, "v1.2.4"));
    }

    [Theory]
    [InlineData("different-run")]
    [InlineData("different-version")]
    [InlineData("missing-creation")]
    [InlineData("completed-without-exit")]
    [InlineData("private-diagnostic")]
    public void MismatchedOrUnsafeResultFailsClosed(string mismatch)
    {
        var result = mismatch switch
        {
            "different-run" => Result with { RunId = Guid.NewGuid().ToString("N") },
            "different-version" => Result with { TargetVersion = "9.9.9" },
            "missing-creation" => Result with { InstallerProcessId = 42 },
            "completed-without-exit" => Result with { MsiState = UpdateInstallerState.Completed, InstallerProcessId = 42, InstallerCreationTimeUtcTicks = 1000 },
            _ => Result with { DiagnosticCode = "raw private error" }
        };
        Assert.Throws<InvalidDataException>(() => UpdateHandoffRecovery.ReadResult(UpdateStrictJson.Write(result), Run, "v1.2.4"));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void RestartResultCapacityIsExact(int count, bool accepted)
    {
        var result = Result with { Restarts = Enumerable.Range(1, count).Select(id => new UpdateRestartResult(UpdateApplication.Client, id, UpdateRestartState.StillRunning)).ToImmutableArray() };
        if (accepted) Assert.Equal(count, UpdateHandoffRecovery.ReadResult(UpdateStrictJson.Write(result), Run, "v1.2.4").Restarts.Length);
        else Assert.Throws<InvalidDataException>(() => UpdateHandoffRecovery.ReadResult(UpdateStrictJson.Write(result), Run, "v1.2.4"));
    }

    [Fact]
    public void EveryUnresolvedRunIsReturnedWithoutInferringChronologyOrChangingServicingState()
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        const string install = @"C:\Users\test\AppData\Local\Programs\AgentSignaler\Remote";
        var installed = new UpdateInstalledPackage(UpdatePackage.Remote, Guid.NewGuid(), UpdatePolicy.UpgradeCode(UpdatePackage.Remote),
            new Version(1, 2, 3), install, sid);
        var parent = new UpdateProcessIdentity(1, 100, sid, 1, Path.Combine(install, "AgentSignaler.Client.exe"));
        var participant = new UpdateInstanceRegistration(1, parent, new(UpdateApplication.Client, @"C:\Users\test\remote.json", true),
            UpdateMaintenanceValidation.PipeName(installed, parent));
        var runs = Enumerable.Range(1, 32).Select(index => Guid.Parse($"{33 - index:x8}-2222-3333-4444-555555555555")).ToArray();
        var gate = new UpdateMaintenanceGate(1, runs[0], install,
            parent with { ProcessId = 90, ExecutablePath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" },
            DateTimeOffset.UtcNow.AddMinutes(10), UpdateMaintenancePhase.Servicing, parent);
        var gateBytes = UpdateStrictJson.Write(gate);
        var storage = new ObservationStorage(runs, gateBytes.ToArray());
        foreach (var run in runs)
        {
            var manifest = new UpdateHandoffManifest(1, run.ToString("N"), new('a', 64), "Remote", install,
                installed.ProductCode.ToString("B"), "1.2.3", 10, "v1.2.4",
                [new(11, "AgentSignaler.Dashboard.msi", 100), new(12, "AgentSignaler.Remote.msi", 100), new(13, "SHA256SUMS.txt", 100)],
                new('b', 64), new('c', 64), parent, [participant], [], "AgentSignaler.Update.Ready.v1." + new string('a', 64), DateTimeOffset.UnixEpoch);
            var result = Result with { RunId = run.ToString("N"), Stage = UpdateStage.Installing,
                MsiState = UpdateInstallerState.Running, InstallerProcessId = 42, InstallerCreationTimeUtcTicks = 1000 };
            storage.Documents[(run, "handoff.json")] = UpdateStrictJson.Write(manifest);
            storage.Documents[(run, "result.json")] = UpdateStrictJson.Write(result);
        }

        var observed = new UpdateHandoffRecovery(installed, storage).ReadResults();
        Assert.Equal(runs.Order(), observed.Select(result => result.RunId).Order());
        Assert.All(observed, result =>
        {
            Assert.True(result.RequiresManualRecovery);
            Assert.Equal(UpdateInstallerState.Running, result.Result.MsiState);
            Assert.Equal(UpdateServicingOutcome.Pending, result.Result.Outcome);
        });
        Assert.Equal(gateBytes, storage.Read("gate.json"));
    }

    [Fact]
    public void UnknownResultPropertiesAreNotSilentlyIgnored()
    {
        var json = Encoding.UTF8.GetString(UpdateStrictJson.Write(Result));
        var bytes = Encoding.UTF8.GetBytes(json[..^1] + ",\"command\":\"powershell evil\"}");
        Assert.Throws<System.Text.Json.JsonException>(() => UpdateHandoffRecovery.ReadResult(bytes, Run, "v1.2.4"));
    }

    private sealed class ObservationStorage(Guid[] runs, byte[] gate) : IUpdateStorage
    {
        public Dictionary<(Guid Run, string Name), byte[]> Documents { get; } = [];
        public string DirectoryPath => @"C:\storage";
        public byte[]? Read(string name, int maximumBytes = 65536) => name == "gate.json" ? gate : null;
        public byte[]? ReadRun(Guid runId, string name) => Documents.GetValueOrDefault((runId, name));
        public IReadOnlyList<Guid> ListRuns() => runs;
        public IDisposable AcquireLease() => throw new InvalidOperationException("Recovery observation must not acquire a servicing lease.");
        public void Write(string name, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("Recovery observation must not write state.");
        public void Delete(string name) => throw new InvalidOperationException("Recovery observation must not delete state.");
        public IReadOnlyList<string> List(string pattern, int maximumCount) => throw new NotSupportedException();
        public string CreateRunDirectory(Guid runId) => throw new InvalidOperationException("Recovery observation must not start another run.");
    }
}
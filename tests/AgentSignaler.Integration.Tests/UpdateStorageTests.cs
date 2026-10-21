using System.Security.AccessControl;
using System.Security.Principal;
using AgentSignaler.Updates;

namespace AgentSignaler.Integration.Tests;

public sealed class UpdateStorageTests
{
    [Fact]
    public void MissingInventoryProbeDoesNotCreateStorageOrChangeExistingDocuments()
    {
        using var fixture = new TemporaryRoot();
        Assert.False(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        storage.Write("check.json", "{}"u8);
        var before = Directory.EnumerateFileSystemEntries(fixture.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.False(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(fixture.Path, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal("{}"u8.ToArray(), storage.Read("check.json"));
    }

    [Theory]
    [InlineData("gate.json")]
    [InlineData("restart.json")]
    public void MissingInventoryProbeFindsExactDirectoryMarkersWithoutClearingOrAcquiringServicingLease(string name)
    {
        using var fixture = new TemporaryRoot();
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        storage.Write(name, "{\"unknownOutcome\":true}"u8);
        using var lease = storage.AcquireLease();
        Assert.True(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
        Assert.False(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install + ".other"));
        Assert.Equal("{\"unknownOutcome\":true}"u8.ToArray(), storage.Read(name));
    }

    [Fact]
    public void MissingInventoryProbeRejectsForeignWritableStorageWithoutRepairingIt()
    {
        using var fixture = new TemporaryRoot();
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        storage.Write("gate.json", "{}"u8);
        var info = new DirectoryInfo(storage.DirectoryPath);
        var acl = info.GetAccessControl();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        Assert.Throws<UnauthorizedAccessException>(() => UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
        Assert.Contains(info.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(users) && (rule.FileSystemRights & FileSystemRights.Write) != 0);
    }

    [Fact]
    public void RegisteredInstallationDirectoryAllowsReadOnlyGrantsAndPinsAgainstReplacement()
    {
        using var fixture = new TemporaryRoot();
        Directory.CreateDirectory(fixture.Install);
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new DirectoryInfo(fixture.Install).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(fixture.Install).SetAccessControl(acl);
        using var pin = WindowsUpdateInventoryPath.PinVerifiedDirectory(fixture.Install, identity.User!.Value);
        Assert.Throws<IOException>(() => Directory.Move(fixture.Install, fixture.Install + ".replaced"));
        Assert.True(Directory.Exists(fixture.Install));
        Assert.False(Directory.Exists(fixture.Install + ".replaced"));
    }

    [Fact]
    public void RegisteredInstallationDirectoryRejectsForeignWriteWithoutChangingPermissions()
    {
        using var fixture = new TemporaryRoot();
        Directory.CreateDirectory(fixture.Install);
        using var identity = WindowsIdentity.GetCurrent();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var info = new DirectoryInfo(fixture.Install);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsUpdateInventoryPath.PinVerifiedDirectory(fixture.Install, identity.User!.Value));
        Assert.Contains(info.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(users) && (rule.FileSystemRights & FileSystemRights.Write) != 0);
    }

    [Fact]
    public void ProtectedStorageAndItsAncestorsCannotBeRenamedWhilePinned()
    {
        using var fixture = new TemporaryRoot();
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        Assert.Throws<IOException>(() => Directory.Move(storage.DirectoryPath, storage.DirectoryPath + ".replaced"));
        Assert.Throws<IOException>(() => Directory.Move(fixture.Path, fixture.Path + ".replaced"));
        Assert.True(Directory.Exists(storage.DirectoryPath));
        Assert.True(Directory.Exists(fixture.Path));
    }

    [Fact]
    public void DiscoveryLeaseIsDistinctFromServicingAndExclusiveAcrossInstances()
    {
        using var fixture = new TemporaryRoot();
        using var first = new UpdateStorage(fixture.Path, fixture.Install);
        using var second = new UpdateStorage(fixture.Path, fixture.Install);
        using var servicing = first.AcquireLease();
        using (first.AcquireCheckLease()) Assert.Throws<IOException>(() => second.AcquireCheckLease());
        using var discoveryAfterRelease = second.AcquireCheckLease();
        Assert.NotNull(discoveryAfterRelease);
    }

    [Fact]
    public void PerInstallLeaseIsExclusiveAcrossIndependentStorageInstances()
    {
        using var fixture = new TemporaryRoot();
        using var first = new UpdateStorage(fixture.Path, fixture.Install);
        using var second = new UpdateStorage(fixture.Path, fixture.Install);
        using (first.AcquireLease()) Assert.Throws<IOException>(() => second.AcquireLease());
        using var acquiredAfterRelease = second.AcquireLease();
        Assert.Equal(first.DirectoryPath, second.DirectoryPath);
    }

    [Fact]
    public void ProtectedAtomicDocumentsAndRunFilesRoundTripWithoutInventoryAccess()
    {
        using var fixture = new TemporaryRoot();
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        storage.Write("gate.json", "{\"v\":1}"u8);
        storage.Write("gate.json", "{\"v\":2}"u8);
        Assert.Equal("{\"v\":2}"u8.ToArray(), storage.Read("gate.json"));
        var acl = new FileInfo(System.IO.Path.Combine(storage.DirectoryPath, "gate.json")).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(), rule => Assert.Equal(identity.User, rule.IdentityReference));
        var runId = Guid.NewGuid();
        var directory = storage.CreateRunDirectory(runId);
        File.WriteAllText(System.IO.Path.Combine(directory, "result.json"), "{\"pending\":true}");
        Assert.Single(storage.ListRuns());
        Assert.Equal("{\"pending\":true}"u8.ToArray(), storage.ReadRun(runId, "result.json"));
        Assert.Throws<ArgumentException>(() => storage.ReadRun(runId, "..\\foreign.json"));
    }

    [Fact]
    public void ForeignWritableDirectoryIsRejectedWithoutRepairingItsAcl()
    {
        using var fixture = new TemporaryRoot();
        string directory;
        using (var storage = new UpdateStorage(fixture.Path, fixture.Install)) directory = storage.DirectoryPath;
        var info = new DirectoryInfo(directory);
        var acl = info.GetAccessControl();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        Assert.Throws<UnauthorizedAccessException>(() => new UpdateStorage(fixture.Path, fixture.Install));
        Assert.Contains(info.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(), r => r.IdentityReference.Equals(users));
    }

    [Fact]
    public void ExactDocumentByteLimitRejectsOverflowWithoutReplacingPriorState()
    {
        using var fixture = new TemporaryRoot();
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        var bytes = new byte[65536];
        storage.Write("result.json", bytes);
        Assert.Equal(bytes, storage.Read("result.json"));
        Assert.Throws<InvalidDataException>(() => storage.Write("result.json", new byte[65537]));
        Assert.Equal(bytes, storage.Read("result.json"));
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentSignaler.Update.Tests." + Guid.NewGuid().ToString("N"));
        public string Install => System.IO.Path.Combine(Path, "Programs", "AgentSignaler", "Remote");
        public TemporaryRoot() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
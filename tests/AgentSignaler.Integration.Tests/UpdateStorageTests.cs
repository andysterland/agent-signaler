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
    public void MissingInventoryProbeAcceptsManagedStoragePermissionsWithoutChangingThem()
    {
        using var fixture = new TemporaryRoot();
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        storage.Write("gate.json", "{}"u8);
        var info = new DirectoryInfo(storage.DirectoryPath);
        var acl = info.GetAccessControl();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        acl.SetAccessRuleProtection(false, true);
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        var before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        Assert.True(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
        Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        Assert.Equal("{}"u8.ToArray(), storage.Read("gate.json"));
    }

    [Fact]
    public void ManualUpdaterDirectoryWithInheritedPermissionsSupportsStartupWithoutChangingItsAcl()
    {
        using var fixture = new TemporaryRoot();
        var directory = Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "AgentSignaler", "Updates"));
        Assert.False(directory.GetAccessControl().AreAccessRulesProtected);
        var before = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        Assert.False(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        using var lease = storage.AcquireLease();
        storage.Write("gate.json", "{}"u8);
        Assert.True(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
        Assert.Equal("{}"u8.ToArray(), storage.Read("gate.json"));
        Assert.Equal(before, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    public void RegisteredInstallationDirectoryAllowsReadOnlyGrantsAndPinsAgainstReplacement()
    {
        using var fixture = new TemporaryRoot();
        Directory.CreateDirectory(fixture.Install);
        var acl = new DirectoryInfo(fixture.Install).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(fixture.Install).SetAccessControl(acl);
        using var pin = WindowsUpdateInventoryPath.PinVerifiedDirectory(fixture.Install);
        Assert.Throws<IOException>(() => Directory.Move(fixture.Install, fixture.Install + ".replaced"));
        Assert.True(Directory.Exists(fixture.Install));
        Assert.False(Directory.Exists(fixture.Install + ".replaced"));
    }

    [Fact]
    public void RegisteredInstallationDirectoryAllowsManagedWriteGrantsWithoutChangingPermissions()
    {
        using var fixture = new TemporaryRoot();
        Directory.CreateDirectory(fixture.Install);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var info = new DirectoryInfo(fixture.Install);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        var before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        using var pin = WindowsUpdateInventoryPath.PinVerifiedDirectory(fixture.Install);
        Assert.Throws<IOException>(() => Directory.Move(fixture.Install, fixture.Install + ".replaced"));
        Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    public void StorageAndItsAncestorsCannotBeRenamedWhilePinned()
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
    public void AtomicDocumentsAndRunFilesInheritManagedPermissionsAndRoundTripWithoutInventoryAccess()
    {
        using var fixture = new TemporaryRoot();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var root = new DirectoryInfo(fixture.Path);
        var managedAcl = root.GetAccessControl();
        managedAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        root.SetAccessControl(managedAcl);
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        storage.Write("gate.json", "{\"v\":1}"u8);
        storage.Write("gate.json", "{\"v\":2}"u8);
        Assert.Equal("{\"v\":2}"u8.ToArray(), storage.Read("gate.json"));
        var runId = Guid.NewGuid();
        var directory = storage.CreateRunDirectory(runId);
        File.WriteAllText(System.IO.Path.Combine(directory, "result.json"), "{\"pending\":true}");
        foreach (var info in new FileSystemInfo[] { new DirectoryInfo(storage.DirectoryPath), new DirectoryInfo(directory),
            new FileInfo(System.IO.Path.Combine(storage.DirectoryPath, "gate.json")), new FileInfo(System.IO.Path.Combine(directory, "result.json")) })
        {
            FileSystemSecurity acl = info is DirectoryInfo child ? child.GetAccessControl() : ((FileInfo)info).GetAccessControl();
            Assert.False(acl.AreAccessRulesProtected);
            Assert.Contains(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>(),
                rule => rule.IsInherited && rule.IdentityReference.Equals(users) && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        }
        Assert.Single(storage.ListRuns());
        Assert.Equal("{\"pending\":true}"u8.ToArray(), storage.ReadRun(runId, "result.json"));
        Assert.Throws<ArgumentException>(() => storage.ReadRun(runId, "..\\foreign.json"));
    }

    [Fact]
    public void ManagedDirectoryPermissionsAreAcceptedWithoutChangingItsAcl()
    {
        using var fixture = new TemporaryRoot();
        string directory;
        using (var storage = new UpdateStorage(fixture.Path, fixture.Install)) directory = storage.DirectoryPath;
        var info = new DirectoryInfo(directory);
        var acl = info.GetAccessControl();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        var before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        using var reopened = new UpdateStorage(fixture.Path, fixture.Install);
        reopened.Write("check.json", "{}"u8);
        Assert.Equal("{}"u8.ToArray(), reopened.Read("check.json"));
        Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    public void ManagedDocumentPermissionsDoNotBlockMaintenanceOrRecoveryReads()
    {
        using var fixture = new TemporaryRoot();
        using var storage = new UpdateStorage(fixture.Path, fixture.Install);
        storage.Write("gate.json", "{}"u8);
        var runId = Guid.NewGuid();
        var run = storage.CreateRunDirectory(runId);
        File.WriteAllText(System.IO.Path.Combine(run, "result.json"), "{}");
        foreach (var path in new[] { System.IO.Path.Combine(storage.DirectoryPath, "gate.json"), System.IO.Path.Combine(run, "result.json") })
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            acl.SetAccessRuleProtection(false, true);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, AccessControlType.Allow));
            info.SetAccessControl(acl);
            var before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            Assert.True(UpdateStorage.HasMaintenanceRecords(fixture.Path, fixture.Install));
            Assert.Equal("{}"u8.ToArray(), storage.Read("gate.json"));
            Assert.Equal("{}"u8.ToArray(), storage.ReadRun(runId, "result.json"));
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
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
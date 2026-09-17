using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentSignaler.Tests;

internal static class CurrentUserOwnedTranscriptFixture
{
    public static void CreateOwnedDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!OperatingSystem.IsWindows())
        {
            directory.Create();
            return;
        }
        using var current = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        // Elevated runners can default to Administrators as owner rather than the current user.
        security.SetOwner(current.User!);
        security.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        directory.Create(security);
    }

    public static void WriteTranscriptFile(string path, string contents)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }
        using var current = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetOwner(current.User!);
        security.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl,
            AccessControlType.Allow));
        using var file = new FileInfo(path).Create(FileMode.Create, FileSystemRights.Write, FileShare.Read,
            4096, FileOptions.None, security);
        using var writer = new StreamWriter(file);
        writer.Write(contents);
    }
}

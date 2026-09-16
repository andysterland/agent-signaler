using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentSignaler.Remote;

public static class ClientConfigurationRevision
{
    public static string Read(string configPath) => Convert.ToHexString(SHA256.HashData(AtomicFile.ReadBounded(configPath, 262144)));
}

public static class ClientIdentity
{
    public static string CanonicalPath(string configPath)
    {
        if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException("An absolute configuration path is required.");
        var path = Path.GetFullPath(configPath);
        // Reject aliases through junctions/symlinks rather than permitting two owners of the same state.
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Configuration paths must not contain symbolic links or junctions.");
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    public static string PipeName(string configPath) => "AgentSignaler.Client." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserDomainName + "\\" + Environment.UserName + "|" + CanonicalPath(configPath))));

    public static FileStream AcquireOwner(string configPath)
    {
        var directory = Path.GetDirectoryName(CanonicalPath(configPath))!;
        Directory.CreateDirectory(directory);
        // File sharing is enforced across Windows sessions, unlike a Local\ named mutex.
        // The directory scope also protects the existing shared sessions.json.
        return new FileStream(Path.Combine(directory, "client-owner.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public static long AllocateGeneration(string configPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(CanonicalPath(configPath))!, "client-generation");
        long previous = 0;
        if (File.Exists(path))
        {
            var text = Encoding.UTF8.GetString(AtomicFile.ReadBounded(path, 32));
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out previous) || previous < 1)
                throw new InvalidDataException("Client generation state is corrupt. Restore the saved generation before restarting.");
        }
        var next = checked(previous + 1);
        AtomicFile.Write(path, Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture)));
        return next;
    }
}

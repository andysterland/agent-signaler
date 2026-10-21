using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace AgentSignaler.Updates;

public sealed record UpdateInstalledPackage(UpdatePackage Package, Guid ProductCode, Guid UpgradeCode,
    Version Version, string InstallDirectory, string UserSid);

// Facts are read-only. Implementations must not configure, repair or install MSI products.
public sealed record UpdateInventoryProduct(Guid ProductCode, Guid UpgradeCode, Version Version,
    string InstallDirectory, string UserSid, bool PerUserUnmanaged, bool Installed, string Architecture,
    bool CanonicalDirectoryVerified = false);

public interface IUpdateInventoryAdapter
{
    Task<ImmutableArray<UpdateInventoryProduct>> ReadAsync(string userSid, CancellationToken cancellationToken);
}

public sealed class UpdateInventory(IUpdateInventoryAdapter adapter, string userSid, string localAppData)
{
    public static Guid UpgradeCode(UpdatePackage package) => UpdatePolicy.UpgradeCode(package);

    // Resolve from MSI registration first, then match the caller. Never infer installation
    // ownership or eligibility merely from a default directory or an assembly version.
    [SupportedOSPlatform("windows")]
    public static UpdateInstalledPackage? Resolve(UpdatePackage package, string installationDirectory)
    {
        _ = UpdatePolicy.UpgradeCode(package);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("update_inventory_user");
        var installed = new UpdateInventory(new WindowsUpdateInventoryAdapter(), sid, local)
            .FindAsync(package).GetAwaiter().GetResult();
        return installed is not null && UpdatePath.EqualsDirectory(installationDirectory, installed.InstallDirectory) ? installed : null;
    }

    public async Task<UpdateInstalledPackage?> FindAsync(UpdatePackage package, CancellationToken cancellationToken = default)
    {
        ImmutableArray<UpdateInventoryProduct> products;
        try { products = await adapter.ReadAsync(userSid, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (ExpectedReadFailure(error))
        { throw new InvalidDataException("update_inventory_unavailable"); }
        if (products.IsDefault || products.Length > 512) throw new InvalidDataException("update_inventory_limit");
        var matching = products.Where(p => p.UpgradeCode == UpgradeCode(package)).ToArray();
        if (matching.Length == 0) return null;
        if (matching.Length != 1) throw new InvalidDataException("update_inventory_ambiguous");
        var p = matching[0];
        _ = UpdatePath.Canonical(localAppData);
        var registeredDirectory = UpdatePath.Canonical(p.InstallDirectory);
        if (!p.Installed || !p.PerUserUnmanaged || p.Architecture != "x64" || p.UserSid != userSid ||
            !p.CanonicalDirectoryVerified ||
            p.ProductCode == Guid.Empty || p.Version.Major is < 0 or > 255 || p.Version.Minor is < 0 or > 255 ||
            p.Version.Build is < 0 or > 65535 || p.Version.Revision != -1)
            throw new InvalidDataException("update_inventory_ineligible");
        return new(package, p.ProductCode, p.UpgradeCode, p.Version, registeredDirectory, userSid);
    }

    internal static bool ExpectedReadFailure(Exception error) => error is COMException or FormatException or OverflowException or
        Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or ArgumentException or IOException or
        InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception;
}

public static class UpdatePath
{
    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 || path.Contains('"') ||
            path.Any(char.IsControl) || path.IndexOfAny(['/', '*', '?', '|', '<', '>']) >= 0)
            throw new InvalidDataException("update_path_invalid");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(path), full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("update_path_not_canonical");
        foreach (var segment in full[(Path.GetPathRoot(full)?.Length ?? 0)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment.EndsWith('.') || segment.EndsWith(' ') || stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')
                throw new InvalidDataException("update_path_ambiguous");
        }
        return full;
    }

    public static bool EqualsDirectory(string left, string right) =>
        string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);

    public static void RequireNoReparsePoints(string path)
    {
        for (var current = Canonical(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("update_path_reparse");
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateInventoryAdapter : IUpdateInventoryAdapter
{
    public Task<ImmutableArray<UpdateInventoryProduct>> ReadAsync(string userSid, CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != userSid) throw new UnauthorizedAccessException("update_inventory_user");
        var result = ImmutableArray.CreateBuilder<UpdateInventoryProduct>();
        object? installerObject = null;
        try
        {
            var related = ReadRelatedProductCodes(static (upgrade, index) =>
            {
                var code = new StringBuilder(39);
                var status = MsiEnumRelatedProducts(upgrade.ToString("B"), 0, index, code);
                return (status, code.ToString());
            }, cancellationToken);
            related = ReadCurrentUserProductCodes(related, userSid, product =>
            {
                var code = new StringBuilder(39);
                var sid = new StringBuilder(256);
                uint sidSize = 256;
                var status = MsiEnumProductsEx(product.ToString("B"), userSid, 2, 0, code, out var context, sid, ref sidSize);
                return (status, code.ToString(), context, sid.ToString());
            }, cancellationToken);
            if (related.Count == 0) return Task.FromResult(result.ToImmutable());
            installerObject = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer", true)!)!;
            dynamic installer = installerObject;
            foreach (var (productCode, expectedUpgrade) in related)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var product = productCode.ToString("B");
                var cachedMsi = Read(product, userSid, "LocalPackage");
                object? databaseObject = null;
                object? summaryObject = null;
                try
                {
                    databaseObject = installer.OpenDatabase(cachedMsi, 0);
                    dynamic database = databaseObject;
                    string upgrade = Property(database, "UpgradeCode");
                    if (!Guid.TryParse(upgrade, out var upgradeCode) || upgradeCode != expectedUpgrade)
                        throw new InvalidDataException("update_inventory_cached_identity");
                    summaryObject = database.SummaryInformation(0);
                    dynamic summary = summaryObject;
                    string template = summary.Property[7];
                    int flags = summary.Property[15];
                    string allUsers = Property(database, "ALLUSERS", true);
                    string cachedCode = Property(database, "ProductCode");
                    string cachedVersion = Property(database, "ProductVersion");
                    var registeredVersion = Read(product, userSid, "VersionString");
                    if (Guid.Parse(cachedCode) != Guid.Parse(product) ||
                        MsiReleaseVersion.Parse(cachedVersion) != MsiReleaseVersion.Parse(registeredVersion))
                        throw new InvalidDataException("update_inventory_cached_identity");
                    var installDirectory = UpdatePath.Canonical(Read(product, userSid, "InstallLocation"));
                    using var directoryPin = WindowsUpdateInventoryPath.PinVerifiedDirectory(installDirectory, userSid);
                    result.Add(new(Guid.Parse(product), upgradeCode, Version.Parse(registeredVersion),
                        installDirectory, userSid, allUsers == "\0" && (flags & 8) != 0,
                        Read(product, userSid, "State") == "5", template.Split(';')[0], true));
                }
                finally
                {
                    if (summaryObject is not null) Marshal.FinalReleaseComObject(summaryObject);
                    if (databaseObject is not null) Marshal.FinalReleaseComObject(databaseObject);
                }
            }
            return Task.FromResult(result.ToImmutable());
        }
        catch (Exception error) when (UpdateInventory.ExpectedReadFailure(error))
        { throw new InvalidDataException("update_inventory_unavailable"); }
        finally { if (installerObject is not null) Marshal.FinalReleaseComObject(installerObject); }
    }

    internal static Dictionary<Guid, Guid> ReadRelatedProductCodes(
        Func<Guid, uint, (uint Status, string ProductCode)> enumerate, CancellationToken cancellationToken)
    {
        var products = new Dictionary<Guid, Guid>();
        foreach (var package in Enum.GetValues<UpdatePackage>())
        {
            var upgrade = UpdateInventory.UpgradeCode(package);
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (status, code) = enumerate(upgrade, index);
                if (status == 259) break;
                if (status != 0 || index >= 128) throw new InvalidDataException("update_inventory_unavailable");
                if (!Guid.TryParse(code, out var product) || product == Guid.Empty || !products.TryAdd(product, upgrade))
                    throw new InvalidDataException("update_inventory_ambiguous");
            }
        }
        return products;
    }

    internal static Dictionary<Guid, Guid> ReadCurrentUserProductCodes(IReadOnlyDictionary<Guid, Guid> related, string userSid,
        Func<Guid, (uint Status, string ProductCode, uint Context, string UserSid)> enumerate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (related.Count > 256) throw new InvalidDataException("update_inventory_limit");
        var products = new Dictionary<Guid, Guid>();
        foreach (var (product, upgrade) in related)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registration = enumerate(product);
            // Related discovery includes other install contexts; never inspect their cached MSI.
            if (registration.Status is 259 or 1605) continue;
            if (registration.Status != 0 || registration.Context != 2 || registration.UserSid != userSid ||
                !Guid.TryParse(registration.ProductCode, out var registeredProduct) || registeredProduct != product)
                throw new InvalidDataException("update_inventory_unavailable");
            products.Add(product, upgrade);
        }
        return products;
    }

    private static string Property(dynamic database, string name, bool optional = false)
    {
        object? viewObject = null;
        object? recordObject = null;
        try
        {
            viewObject = database.OpenView($"SELECT `Value` FROM `Property` WHERE `Property`='{name}'");
            dynamic view = viewObject;
            view.Execute();
            recordObject = view.Fetch();
            if (recordObject is null) return optional ? "\0" : throw new InvalidDataException("update_inventory_property");
            dynamic record = recordObject;
            return record.StringData[1];
        }
        finally
        {
            if (recordObject is not null) Marshal.FinalReleaseComObject(recordObject);
            if (viewObject is not null) Marshal.FinalReleaseComObject(viewObject);
        }
    }

    private static string Read(string product, string sid, string property)
    {
        var value = new StringBuilder(4097);
        uint count = 4097;
        if (MsiGetProductInfoEx(product, sid, 2, property, value, ref count) != 0 || count > 4096)
            throw new InvalidDataException("update_inventory_property");
        return value.ToString();
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumProductsExW")]
    private static extern uint MsiEnumProductsEx(string product, string sid, uint contexts, uint index,
        StringBuilder code, out uint context, StringBuilder owner, ref uint ownerLength);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoExW")]
    private static extern uint MsiGetProductInfoEx(string product, string sid, uint context, string property,
        StringBuilder value, ref uint length);
}
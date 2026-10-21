using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentSignaler.Updates;

public enum UpdatePackage { Dashboard, Remote }
public enum UpdateCheckStatus { Unknown, Checking, UpToDate, Available, CheckFailed, CheckIncomplete }
public enum UpdateStage
{
    Unknown, Checking, UpToDate, Available, CheckFailed, Preparing, AwaitingParticipants,
    HandingOff, WaitingForExit, Downloading, Validating, Installing, Verifying,
    Restarting, Completed, RebootRequired, Failed, Cancelled
}

public readonly record struct MsiReleaseVersion : IComparable<MsiReleaseVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public MsiReleaseVersion(int major, int minor, int patch)
    {
        if (major is < 0 or > 255 || minor is < 0 or > 255 || patch is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(major), "Version exceeds MSI bounds.");
        (Major, Minor, Patch) = (major, minor, patch);
    }

    public static MsiReleaseVersion Parse(string text, bool tag = false)
    {
        if (text is null || text.Length > 14 ||
            !Regex.IsMatch(text, tag ? @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z" :
                @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("update-version-invalid");
        var parts = (tag ? text[1..] : text).Split('.');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major > 255 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) || minor > 255 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch) || patch > 65535)
            throw new InvalidDataException("update-version-invalid");
        return new(major, minor, patch);
    }

    public int CompareTo(MsiReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }
    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}");
    public static bool operator >(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) <= 0;
}

public sealed record ReleaseAsset(long Id, string Name, long Size);
public sealed record ReleaseCandidate(long Id, string Tag, MsiReleaseVersion Version,
    DateTimeOffset PublishedAt, bool Prerelease, ImmutableArray<ReleaseAsset> Assets)
{
    public Uri DetailsUri => new($"https://github.com/andysterland/agent-signaler/releases/tag/{Tag}");
    public ReleaseAsset PackageAsset(UpdatePackage package) => Assets.Single(a => a.Name == UpdatePolicy.AssetName(package));
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseCandidate? Candidate = null,
    DateTimeOffset? CheckedAt = null, DateTimeOffset? NextCheckAt = null,
    string? DiagnosticCode = null, bool FromCache = false);

public static class UpdatePolicy
{
    public const string Repository = "andysterland/agent-signaler";
    public const int MetadataLimit = 4 * 1024 * 1024;
    public const int CacheLimit = 64 * 1024;
    public const int PageSize = 20;
    public const int PageLimit = 5;
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan MetadataDeadline = TimeSpan.FromSeconds(15);
    public static string AssetName(UpdatePackage package) => package switch
    {
        UpdatePackage.Dashboard => "AgentSignaler.Dashboard.msi",
        UpdatePackage.Remote => "AgentSignaler.Remote.msi",
        _ => throw new InvalidDataException("update-package-invalid")
    };
    public static Guid UpgradeCode(UpdatePackage package) => package switch
    {
        UpdatePackage.Dashboard => new("D67CE744-C442-473A-B751-CA70D3CBCA4D"),
        UpdatePackage.Remote => new("BFA03A34-37C9-4149-9789-9A68EC2A9C3A"),
        _ => throw new InvalidDataException("update-package-invalid")
    };

    public static void ValidateCandidate(ReleaseCandidate candidate)
    {
        if (candidate.Id <= 0 || candidate.Version != MsiReleaseVersion.Parse(candidate.Tag, tag: true) ||
            candidate.PublishedAt.Offset != TimeSpan.Zero || candidate.PublishedAt == default ||
            candidate.Assets.IsDefault || candidate.Assets.Length != 3)
            throw new InvalidDataException("update-release-invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<long>();
        foreach (var asset in candidate.Assets)
        {
            var maximum = asset.Name == "SHA256SUMS.txt" ? 65536L : 1073741824L;
            if (asset.Name is not ("SHA256SUMS.txt" or "AgentSignaler.Dashboard.msi" or "AgentSignaler.Remote.msi") ||
                asset.Id <= 0 || !ids.Add(asset.Id) || !names.Add(asset.Name) || asset.Size <= 0 || asset.Size > maximum)
                throw new InvalidDataException("update-assets-invalid");
        }
    }
}

public sealed class UpdateStateMachine
{
    public UpdateStage Stage { get; private set; } = UpdateStage.Unknown;
    public void MoveTo(UpdateStage next)
    {
        var allowed = (Stage, next) switch
        {
            (UpdateStage.Unknown or UpdateStage.UpToDate or UpdateStage.CheckFailed or UpdateStage.Available, UpdateStage.Checking) => true,
            (UpdateStage.Checking, UpdateStage.UpToDate or UpdateStage.Available or UpdateStage.CheckFailed) => true,
            (UpdateStage.Available, UpdateStage.Preparing) => true,
            (UpdateStage.Preparing, UpdateStage.AwaitingParticipants) => true,
            (UpdateStage.AwaitingParticipants, UpdateStage.HandingOff) => true,
            (UpdateStage.HandingOff, UpdateStage.WaitingForExit) => true,
            (UpdateStage.WaitingForExit, UpdateStage.Downloading) => true,
            (UpdateStage.Downloading, UpdateStage.Validating) => true,
            (UpdateStage.Validating, UpdateStage.Installing) => true,
            (UpdateStage.Installing, UpdateStage.Verifying or UpdateStage.RebootRequired) => true,
            (UpdateStage.Verifying, UpdateStage.Restarting) => true,
            (UpdateStage.Restarting, UpdateStage.Completed) => true,
            (UpdateStage.Preparing or UpdateStage.AwaitingParticipants or UpdateStage.HandingOff or UpdateStage.WaitingForExit or
                UpdateStage.Downloading or UpdateStage.Validating or UpdateStage.Installing or UpdateStage.Verifying or
                UpdateStage.Restarting, UpdateStage.Failed or UpdateStage.Cancelled) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("update-transition-invalid");
        Stage = next;
    }
}
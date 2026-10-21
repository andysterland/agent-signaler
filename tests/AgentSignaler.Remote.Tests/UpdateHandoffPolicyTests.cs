using System.Collections.Immutable;
using System.Text.Json;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class UpdateHandoffPolicyTests
{
    [Fact]
    public void SharedFixturePinsPublisherDependenciesAssetsAndUpgradeCodes()
    {
        using var fixture = ReadFixture();
        var root = fixture.RootElement;
        Assert.Equal(UpdateHandoffPolicy.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(UpdateHandoffPolicy.DependencyPolicyVersion, root.GetProperty("policyVersion").GetInt32());
        Assert.Equal(42, UpdateHandoffPolicy.ClientUpdateExitCode);
        Assert.Equal(UpdateHandoffPolicy.ClientUpdateExitCode, root.GetProperty("clientUpdateExitCode").GetInt32());
        Assert.Equal(UpdateHandoffPolicy.ReleasePublisher, root.GetProperty("publisher").GetString());
        Assert.Equal(UpdateHandoffPolicy.CodeSigningEku, root.GetProperty("codeSigningEku").GetString());
        Assert.Equal(UpdateHandoffLauncher.RequiredScripts.ToArray(), root.GetProperty("updaterScripts").EnumerateArray().Select(p => p.GetString()).ToArray());
        foreach (var package in Enum.GetValues<UpdatePackage>())
            Assert.Equal(UpdatePolicy.UpgradeCode(package), Guid.Parse(root.GetProperty("upgradeCodes").GetProperty(package.ToString()).GetString()!));
        var assets = root.GetProperty("releaseAssets").EnumerateArray().Select((a, i) => new ReleaseAsset(i + 1, a.GetString()!, 1)).ToImmutableArray();
        UpdatePolicy.ValidateCandidate(new(1, "v1.2.3", new(1, 2, 3), DateTimeOffset.UnixEpoch, true, assets));
        Assert.Equal("SHA256SUMS.txt", assets.Single(a => a.Name != UpdatePolicy.AssetName(UpdatePackage.Dashboard) && a.Name != UpdatePolicy.AssetName(UpdatePackage.Remote)).Name);
    }

    [Fact]
    public void OrdinaryClientExitAlwaysOverridesUpdateRestartConsent()
    {
        foreach (var cleanUpdate in new[] { false, true })
        foreach (var failed in new[] { false, true })
            Assert.Equal(0, UpdateHandoffPolicy.GetClientExitCode(true, cleanUpdate, failed));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 42)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    public void OnlyCleanUpdateOwnedClientShutdownAuthorizesRestart(bool cleanUpdate, bool failed, int expected) =>
        Assert.Equal(expected, UpdateHandoffPolicy.GetClientExitCode(false, cleanUpdate, failed));

    [Fact]
    public void SharedVersionBoundaryCasesMatchManagedPolicy()
    {
        using var fixture = ReadFixture();
        foreach (var item in fixture.RootElement.GetProperty("versions").EnumerateArray())
        {
            var text = item.GetProperty("text").GetString()!;
            if (item.GetProperty("valid").GetBoolean()) Assert.Equal(text, MsiReleaseVersion.Parse(text).ToString());
            else Assert.Throws<InvalidDataException>(() => MsiReleaseVersion.Parse(text));
        }
    }

    [Fact]
    public void SharedSignatureEvidenceCasesMatchProductionPublisherPolicy()
    {
        using var fixture = ReadFixture();
        foreach (var item in fixture.RootElement.GetProperty("signatures").EnumerateArray())
        {
            var evidence = new UpdateSignatureEvidence(item.GetProperty("trustedSignature").GetBoolean(), item.GetProperty("timestampValid").GetBoolean(),
                item.GetProperty("publisher").GetString()!, item.GetProperty("selfSigned").GetBoolean(),
                item.GetProperty("enhancedKeyUsages").EnumerateArray().Select(v => v.GetString()!).ToImmutableArray());
            if (item.GetProperty("valid").GetBoolean()) UpdateHandoffPolicy.RequireSignature(evidence);
            else Assert.Throws<UnauthorizedAccessException>(() => UpdateHandoffPolicy.RequireSignature(evidence));
        }
    }

    private static JsonDocument ReadFixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "fixtures", "update-policy-v1.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        throw new FileNotFoundException("Shared updater policy fixture not found.");
    }
}
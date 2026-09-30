using System.Text;
using System.Text.Json;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class OwnershipAndSettingsTests
{
    [Fact]
    public void SupportedVersionMarkerIsInspectedWithoutLoadingOrExecutingTheAssembly()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "process-fixture-path.txt")).Trim();
        Assert.True(DashboardResourceLease.SupportsResourceLease(fixture));
        Assert.False(DashboardResourceLease.SupportsResourceLease(typeof(DashboardRuntime).Assembly.Location));
    }

    [Fact]
    public void OperationalImplementationsComeFromTheDeployedCoreWithoutWinUiDependencies()
    {
        var assembly = typeof(DashboardRuntime).Assembly;
        Assert.Equal("AgentSignaler.Dashboard.Core", assembly.GetName().Name);
        Assert.Same(assembly, typeof(DashboardSettings).Assembly);
        Assert.Same(assembly, typeof(DevBoxCatalogController).Assembly);
        Assert.Same(assembly, typeof(WindowsAppConnectionController).Assembly);
        Assert.Same(assembly, typeof(PrerequisiteCheck).Assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), dependency =>
            dependency.Name?.Contains("WinUI", StringComparison.OrdinalIgnoreCase) == true ||
            dependency.Name?.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase) == true ||
            dependency.Name == "Microsoft.UI.Xaml");
    }

    [Theory]
    [InlineData("""{"Port":1023}""")]
    [InlineData("""{"Port":65536}""")]
    [InlineData("""{"Port":51820,"Port":51821}""")]
    [InlineData("""{"Future":{"x":1,"x":2}}""")]
    [InlineData("""{"RpcPort":51821,"RpcPort":"ignored"}""")]
    [InlineData("""{"RpcPort":{"x":1,"x":2}}""")]
    [InlineData("""{"RpcPort":"ignored","Port":1023}""")]
    [InlineData("""{"RpcPort":null,"Theme":"invalid"}""")]
    public void InvalidPortsAndDuplicatePropertiesAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => DashboardSettings.FromJson(json));

    [Theory]
    [InlineData(1024)]
    [InlineData(65535)]
    public void ExactReceiverPortBoundariesRemainValid(int port)
    {
        Assert.Equal(port, DashboardSettings.FromJson($"{{\"Port\":{port}}}").Port);
        Assert.Equal(51821, DashboardSettings.FromJson("""{"Port":51821}""").Port);
        Assert.Equal(51820, DashboardSettings.FromJson("{}").Port);
    }

    [Fact]
    public void LegacyRpcPortMigrationPreservesNestedAndCaseVariantExtensionFields()
    {
        const string json = """
            {"RpcPort":"ignored","rpcPort":65536,"Future":{"RpcPort":true,"untouched":[1,null,"value"]},
             "Theme":"Dark","Port":54001,"ReceiveDetailedConversations":false}
            """;
        var settings = DashboardSettings.FromJson(json);
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(54001, settings.Port);
        Assert.False(settings.ReceiveDetailedConversations);
        Assert.False(settings.ExtensionData!.ContainsKey("RpcPort"));
        using var original = JsonDocument.Parse(json);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        Assert.False(saved.RootElement.TryGetProperty("RpcPort", out _));
        Assert.Equal(65536, saved.RootElement.GetProperty("rpcPort").GetInt32());
        Assert.True(JsonElement.DeepEquals(original.RootElement.GetProperty("Future"), saved.RootElement.GetProperty("Future")));
    }

    [Fact]
    public void SettingsSaveRemovesOnlyTopLevelLegacyRpcPortFromExtensionData()
    {
        var path = Path.GetFullPath(Path.Combine("test-artifacts", $"legacy-settings-{Guid.NewGuid():N}"));
        try
        {
            using var document = JsonDocument.Parse("""{"RpcPort":{"ignored":true},"Future":{"RpcPort":42}}""");
            var settings = new DashboardSettings
            {
                ExtensionData = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            };
            settings.Save(path);
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "dashboard-settings.json")));
            Assert.False(saved.RootElement.TryGetProperty("RpcPort", out _));
            Assert.Equal(42, saved.RootElement.GetProperty("Future").GetProperty("RpcPort").GetInt32());
            Assert.Single(Directory.GetFiles(path));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void SettingsReadByteBoundIsExactAndDoesNotRewriteLegacyExtensionData()
    {
        var suffix = "\"}";
        var prefix = "{\"Future\":\"";
        var exact = prefix + new string('x', DashboardSettings.MaximumFileBytes - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(DashboardSettings.MaximumFileBytes, Encoding.UTF8.GetByteCount(exact));
        Assert.NotNull(DashboardSettings.FromJson(exact).ExtensionData);
        Assert.Throws<InvalidDataException>(() => DashboardSettings.FromJson(exact + " "));
    }

    [Fact]
    public void SettingsWriteAboveByteLimitFailsWithoutReplacingFileOrLeavingPendingFile()
    {
        var path = Path.GetFullPath(Path.Combine("test-artifacts", $"settings-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);
        try
        {
            var original = new DashboardSettings { AutoStartSharing = false };
            original.Save(path);
            var saved = File.ReadAllBytes(Path.Combine(path, "dashboard-settings.json"));
            using var document = JsonDocument.Parse("\"" + new string('x', DashboardSettings.MaximumFileBytes) + "\"");
            var excessive = original with { ExtensionData = new Dictionary<string, JsonElement> { ["Future"] = document.RootElement.Clone() } };
            Assert.Throws<InvalidDataException>(() => excessive.Save(path));
            Assert.Equal(saved, File.ReadAllBytes(Path.Combine(path, "dashboard-settings.json")));
            Assert.Single(Directory.GetFiles(path));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void CaseAndExplicitEnvironmentAliasesContendForOneHandleWithoutOpeningDatabase()
    {
        var path = Path.GetFullPath(Path.Combine("test-artifacts", $"lease-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);
        try
        {
            using (var lease = DashboardResourceLease.Acquire(path, path.ToUpperInvariant()))
            {
                Assert.Throws<DashboardOwnershipException>(() => DashboardResourceLease.Acquire(path.ToUpperInvariant(), path));
                Assert.Single(Directory.GetFiles(path));
                Assert.False(File.Exists(Path.Combine(path, "dashboard.db")));
            }
            using var next = DashboardResourceLease.Acquire(path, path);
            Assert.True(next.IsHeld);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ConflictingArgumentAndEnvironmentDirectoriesAreRejectedWithoutStorageMutation()
    {
        var root = Path.GetFullPath(Path.Combine("test-artifacts", $"conflict-{Guid.NewGuid():N}"));
        try
        {
            Assert.Throws<ArgumentException>(() => DashboardResourceLease.Acquire(Path.Combine(root, "a"), Path.Combine(root, "b")));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EnvironmentVariableFormsResolveToTheSameLeaseAndRejectUnsafeExpandedPaths()
    {
        var path = Path.GetFullPath(Path.Combine("test-artifacts", $"expanded-{Guid.NewGuid():N}"));
        var variable = "AGENT_SIGNALER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, path);
        try
        {
            using (var lease = DashboardResourceLease.Acquire($"%{variable}%", path))
            {
                Assert.Equal(path, lease.CanonicalDirectory, ignoreCase: true);
                Assert.Throws<DashboardOwnershipException>(() => DashboardResourceLease.Acquire(path, $"%{variable}%"));
            }
            Environment.SetEnvironmentVariable(variable, @"\\synthetic.invalid\share");
            Assert.Throws<ArgumentException>(() => DashboardResourceLease.ValidatePath($"%{variable}%"));
            Environment.SetEnvironmentVariable(variable, path + "\n");
            Assert.Throws<ArgumentException>(() => DashboardResourceLease.ValidatePath($"%{variable}%"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Directory.Delete(path, true);
        }
    }

    [Theory]
    [InlineData("""{"Port":1023}""")]
    [InlineData("""{"Port":"51820","RpcPort":51821}""")]
    [InlineData("""{"Port":65536,"RpcPort":"ignored"}""")]
    [InlineData("""{"Theme":"invalid"}""")]
    [InlineData("{")]
    public async Task InvalidKnownSettingsUseRecoveryDefaultsWithoutRewriting(string json)
    {
        var path = Path.GetFullPath(Path.Combine("test-artifacts", $"ports-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Combine(path, "dashboard-settings.json"), json);
            using var lease = DashboardResourceLease.Acquire(path, path);
            await using var recovered = new DashboardRuntime(lease);
            Assert.True(recovered.Settings.State.Recovered);
            Assert.False(recovered.Settings.State.Saved.AutoStartSharing);
            Assert.False(recovered.Settings.State.Saved.ReceiveDetailedConversations);
            Assert.True((await recovered.ShutdownAsync()).Clean);
            Assert.Equal(json, File.ReadAllText(Path.Combine(path, "dashboard-settings.json")));
            Assert.False(File.Exists(Path.Combine(path, "dashboard.db")));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ResourceAdmissionIsAllOrNothingAndDoubleReleaseCannotReleaseAnotherOwner()
    {
        var gate = new RuntimeCommandGate();
        using var first = gate.Enter(["azure"]);
        Assert.Equal(1003, Assert.Throws<RuntimeCommandException>(() => gate.Enter(["settings", "azure"])).Error.Code);
        using var settings = gate.Enter(["settings"]);
        first.Dispose();
        using var next = gate.Enter(["azure"]);
        first.Dispose();
        Assert.Throws<RuntimeCommandException>(() => gate.Enter(["azure"]));
    }
}

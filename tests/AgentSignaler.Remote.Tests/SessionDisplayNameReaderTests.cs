using AgentSignaler.Contracts;
using Xunit;
using static AgentSignaler.Tests.CurrentUserOwnedTranscriptFixture;

namespace AgentSignaler.Remote.Tests;

public sealed class SessionDisplayNameReaderTests : IDisposable
{
    private const string SessionId = "f58e1207-231b-4a6f-b311-34bbd544139a";
    private readonly string _home = Path.Combine(AppContext.BaseDirectory, "test-data", "names-" + Guid.NewGuid().ToString("N"));
    private string Metadata => Path.Combine(_home, "session-state", SessionId, "workspace.yaml");
    private string LogPath => Path.Combine(_home, "diagnostic.log");
    private SourceDescriptor Source(string kind = "copilot-cli") => new(kind, "verified-scope");
    private RemoteConfiguration Configuration(string kind = "copilot-cli") => new()
    {
        Integrations = [new()
        {
            Id = "configured-target", Kind = kind, DisplayName = "Synthetic runtime", InstallationId = "fixture",
            ScopeId = "verified-scope", HookDirectory = Path.Combine(_home, "hooks"),
            Capability = IntegrationCapability.Configured, SupportedEvents = HookAdapters.Events(kind)
        }]
    };

    public SessionDisplayNameReaderTests()
    {
        CreateOwnedDirectory(_home);
        CreateOwnedDirectory(Path.GetDirectoryName(Metadata)!);
    }

    private string? Read(string sessionId = SessionId, string kind = "copilot-cli") =>
        SessionDisplayNameReader.Read(Configuration(kind), Source(kind), sessionId, new(LogPath));

    private void Write(string fields) => WriteTranscriptFile(Metadata, $"id: {SessionId}\n{fields}");

    [Theory]
    [InlineData("name: A useful name", "A useful name")]
    [InlineData("name: 'It''s useful'", "It's useful")]
    [InlineData("name: \"A \\\"quoted\\\" name\"", "A \"quoted\" name")]
    [InlineData("name: |-\n  A block name", "A block name")]
    [InlineData("name: >-\n  A folded\n  name", "A folded name")]
    [InlineData("name: Name # comment", "Name")]
    public void ReadsVerifiedScalarForms(string fields, string expected)
    {
        Write(fields);
        Assert.Equal(expected, Read());
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void VisualStudioUsesItsConfiguredSharedRuntimeHome()
    {
        Write("name: VS session");
        Assert.Equal("VS session", Read(kind: "visual-studio"));
    }

    [Fact]
    public void RefreshesAndOnlyReturnsName()
    {
        Write("cwd: PRIVATE-PATH\nsummary: PRIVATE-SUMMARY\nprompt: PRIVATE-PROMPT\nname: First");
        Assert.Equal("First", Read());
        Write("cwd: PRIVATE-PATH\nname: Renamed");
        Assert.Equal("Renamed", Read());
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void MissingFileAndMissingNameAreNormal()
    {
        Assert.Null(Read());
        Write("summary: Not a name\nuser_named: false");
        Assert.Null(Read());
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void LegacyConfigurationDoesNotProbeAnUnconfiguredHome()
    {
        Write("name: Not returned");
        var config = Configuration() with { Integrations = [] };
        Assert.Null(SessionDisplayNameReader.Read(config, Source(), SessionId, new(LogPath)));
        Assert.Null(SessionDisplayNameReader.Read(config, Source(), "opaque-legacy-id", new(LogPath)));
        Assert.False(File.Exists(LogPath));
    }

    [Theory]
    [InlineData("name: ''")]
    [InlineData("name: '  '")]
    [InlineData("name: \"control\\ncharacter\"")]
    [InlineData("name: \"unterminated")]
    [InlineData("name: 'unterminated")]
    [InlineData("name: First\nname: Second")]
    [InlineData("id: another\nname: Name")]
    [InlineData("name: *alias")]
    [InlineData("name: !!str Name")]
    [InlineData("name: [Name]")]
    [InlineData("name: |-\n  First\n  Second")]
    [InlineData("not a mapping\nname: Name")]
    public void RejectsMalformedMetadataWithoutLoggingContents(string fields)
    {
        Write(fields);
        Assert.Null(Read());
        var log = File.ReadAllText(LogPath);
        Assert.Contains("session-name-invalid", log);
        Assert.DoesNotContain(fields, log);
    }

    [Fact]
    public void RequiresMatchingMetadataIdentity()
    {
        WriteTranscriptFile(Metadata, "id: 185136d0-32de-419d-b889-732588628f32\nname: Other session");
        Assert.Null(Read());
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("session:stream")]
    [InlineData("session/child")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void RejectsUnsafeOrUnverifiedSessionIds(string id)
    {
        Write("name: Not returned");
        Assert.Null(Read(id));
    }

    [Fact]
    public void RequiresVerifiedMatchingSourceAndUnambiguousRoot()
    {
        Write("name: Not returned");
        var config = Configuration();
        Assert.Null(SessionDisplayNameReader.Read(config, null, SessionId, new(LogPath)));
        Assert.Null(SessionDisplayNameReader.Read(config, Source() with { ScopeId = "other" }, SessionId, new(LogPath)));
        Assert.Null(SessionDisplayNameReader.Read(config, Source("vscode"), SessionId, new(LogPath)));
        config = config with { Integrations = [config.Integrations[0] with { Capability = IntegrationCapability.VerificationRequired }] };
        Assert.Null(SessionDisplayNameReader.Read(config, Source(), SessionId, new(LogPath)));
        config = Configuration();
        config = config with { Integrations = [config.Integrations[0], config.Integrations[0] with
        {
            Id = "another", HookDirectory = Path.Combine(_home, "other", "hooks")
        }] };
        Assert.Null(SessionDisplayNameReader.Read(config, Source(), SessionId, new(LogPath)));
    }

    [Fact]
    public void EnforcesExactNameAndMetadataSizeBounds()
    {
        Write("name: " + new string('a', 128));
        Assert.Equal(new string('a', 128), Read());
        Write("name: " + new string('a', 129));
        Assert.Null(Read());
        var header = $"id: {SessionId}\nname: Bounded\nignored: ";
        WriteTranscriptFile(Metadata, header + new string('x', SessionDisplayNameReader.MaximumMetadataBytes - header.Length));
        Assert.Equal("Bounded", Read());
        File.AppendAllText(Metadata, "x");
        Assert.Null(Read());
    }

    [Fact]
    public void RejectsInvalidUtf8AndHandlesInaccessibleMetadata()
    {
        Write("name: Valid");
        File.AppendAllBytes(Metadata, [0xff]);
        Assert.Null(Read());
        Write("name: Valid");
        using var held = new FileStream(Metadata, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(Read());
        Assert.Contains("session-name-unavailable", File.ReadAllText(LogPath));
    }

    [Fact]
    public async Task RejectsReparsePointSessionDirectory()
    {
        Write("name: Not returned");
        var directory = Path.GetDirectoryName(Metadata)!;
        var physical = Path.Combine(_home, "physical-session");
        Directory.Move(directory, physical);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{directory}\" \"{physical}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try { Assert.Null(Read()); }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public void RejectsHardLinkedMetadata()
    {
        Write("name: Not returned");
        var link = Path.Combine(_home, "linked.yaml");
        Assert.True(CreateHardLink(link, Metadata, IntPtr.Zero));
        Assert.Null(Read());
        File.Delete(link);
        Assert.Equal("Not returned", Read());
    }

    [Fact]
    public void CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SessionDisplayNameReader.Read(Configuration(), Source(), SessionId, new(LogPath), cancellation.Token));
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}

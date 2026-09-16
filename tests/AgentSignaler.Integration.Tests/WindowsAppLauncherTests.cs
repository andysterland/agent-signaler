using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Integration.Tests;

public sealed partial class WindowsAppLauncherTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProgressReportsActualStagesBeforeWorkAndSkipsLaunchWhenWindowIsReused(bool cached, bool reuse)
    {
        var stages = new List<WindowsAppLaunchStage>();
        var resolver = new FakeResolver
        {
            Resolve = (_, _) =>
            {
                Assert.Equal(WindowsAppLaunchStage.RefreshingConnection, stages.Last());
                return Task.FromResult(ConnectionTestData.Resolved);
            }
        };
        var platform = new FakePlatform
        {
            Probe = _ =>
            {
                Assert.Equal(WindowsAppLaunchStage.SearchingLocalWindows, Assert.Single(stages));
                return reuse ? WindowsAppActivationDisposition.ExistingWindowActivated : WindowsAppActivationDisposition.NoExistingWindow;
            },
            OnLaunch = _ => Assert.Equal(WindowsAppLaunchStage.Launching, stages.Last())
        };
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) =>
        {
            Assert.Equal(WindowsAppLaunchStage.RefreshingConnection, stages.Last());
            return Task.CompletedTask;
        });
        Task<WindowsAppConnection?> Read(CancellationToken _) => Task.FromResult<WindowsAppConnection?>(ConnectionTestData.CachedMapping);
        if (cached)
            await launcher.OpenCurrentLastKnownAsync(Guid.NewGuid(), Read, progress: stages.Add);
        else
            await launcher.OpenCurrentAsync(Guid.NewGuid(), Read, progress: stages.Add);

        Assert.Equal(reuse
            ? [WindowsAppLaunchStage.SearchingLocalWindows]
            : cached
                ? [WindowsAppLaunchStage.SearchingLocalWindows, WindowsAppLaunchStage.Launching]
                : new[] { WindowsAppLaunchStage.SearchingLocalWindows, WindowsAppLaunchStage.RefreshingConnection, WindowsAppLaunchStage.Launching },
            stages);
        Assert.Equal(reuse || cached ? 0 : 1, resolver.Calls);
        Assert.Equal(reuse ? 0 : 1, platform.Uris.Count);
    }

    [Theory]
    [InlineData(WindowsAppLaunchStage.SearchingLocalWindows)]
    [InlineData(WindowsAppLaunchStage.RefreshingConnection)]
    [InlineData(WindowsAppLaunchStage.Launching)]
    public async Task CancellationAtProgressBoundaryPreventsLaunch(object value)
    {
        using var cancellation = new CancellationTokenSource();
        var stageToCancel = (WindowsAppLaunchStage)value;
        var platform = new FakePlatform();
        var launcher = new WindowsAppLauncher(new FakeResolver(), platform, (_, _, _) => Task.CompletedTask);
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<OperationCanceledException>(() => launcher.OpenCurrentAsync(id,
            _ => Task.FromResult<WindowsAppConnection?>(ConnectionTestData.Mapping), cancellation.Token,
            stage => { if (stage == stageToCancel) cancellation.Cancel(); }));
        Assert.Empty(platform.Uris);
        Assert.False(WindowsAppOperationGate.Shared.IsBusy(id));
    }

    [Fact]
    public async Task EveryNormalLaunchResolvesAndAtomicallySavesBeforeActivation()
    {
        var events = new List<string>();
        WindowsAppConnection? saved = null;
        var resolver = new FakeResolver { Resolve = (_, _) => { events.Add("resolve"); return Task.FromResult(ConnectionTestData.Resolved); } };
        var platform = new FakePlatform { OnLaunch = uri =>
        {
            Assert.Equal(uri.OriginalString, saved!.LastKnownConnectionUri);
            Assert.Equal(ConnectionTestData.RetrievedAt, saved.ConnectionUriRetrievedAtUtc);
            Assert.Equal(ConnectionTestData.Tenant, saved.AzureTenantId);
            Assert.Equal("user@example.com", saved.AzureAccountUpn);
            events.Add("launch");
        } };
        var id = Guid.NewGuid();
        var launcher = new WindowsAppLauncher(resolver, platform, (machineId, mapping, _) =>
        {
            Assert.Equal(id, machineId);
            events.Add("persist");
            saved = mapping;
            return Task.CompletedTask;
        });
        await launcher.OpenAsync(id, ConnectionTestData.CachedMapping);
        await launcher.OpenAsync(id, saved);
        Assert.Equal(["resolve", "persist", "launch", "resolve", "persist", "launch"], events);
        Assert.Equal(2, resolver.Calls);
        Assert.Equal(ConnectionTestData.Uri, Assert.Single(platform.Uris.Distinct()));
        Assert.Equal(ConnectionTestData.Mapping.DevBoxName, saved!.DevBoxName);
        Assert.Equal(ConnectionTestData.Mapping.ProjectName, saved.ProjectName);
        Assert.Equal(ConnectionTestData.Mapping.DevCenterEndpoint, saved.DevCenterEndpoint);
    }

    [Fact]
    public async Task PersistenceIsAwaitedNotMerelyStarted()
    {
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform();
        var launcher = new WindowsAppLauncher(new FakeResolver(), platform, (_, _, _) => saved.Task);
        var pending = launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping);
        Assert.False(pending.IsCompleted);
        Assert.Empty(platform.Uris);
        saved.SetResult();
        await pending;
        Assert.Single(platform.Uris);
    }

    [Fact]
    public async Task MissingInvalidMappingOrProtocolNeverResolvesSavesOrLaunches()
    {
        foreach (var failure in new[] { WindowsAppFailure.MissingMapping, WindowsAppFailure.InvalidMapping, WindowsAppFailure.ProtocolMissing })
        {
            var resolver = new FakeResolver();
            var platform = new FakePlatform { Available = failure != WindowsAppFailure.ProtocolMissing };
            var saves = 0;
            var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => { saves++; return Task.CompletedTask; });
            var mapping = failure switch
            {
                WindowsAppFailure.MissingMapping => null,
                WindowsAppFailure.InvalidMapping => ConnectionTestData.Mapping with { ProjectName = "../unsafe" },
                _ => ConnectionTestData.Mapping
            };
            var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(Guid.NewGuid(), mapping));
            Assert.Equal(failure, error.Failure);
            Assert.Equal(0, saves);
            Assert.Equal(0, resolver.Calls);
            Assert.Empty(platform.Uris);
        }
    }

    public static IEnumerable<object[]> ResolverFailures() =>
        new[] { WindowsAppFailure.SignInRequired, WindowsAppFailure.CliUnsupported, WindowsAppFailure.CliUnavailable,
            WindowsAppFailure.TimedOut, WindowsAppFailure.AccountMismatch, WindowsAppFailure.DevBoxUnavailable,
            WindowsAppFailure.ApiUnavailable, WindowsAppFailure.MalformedResponse, WindowsAppFailure.UnsafeUri }
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(ResolverFailures))]
    public async Task ResolutionFailureNeverMutatesOrAutomaticallyLaunchesCache(object value)
    {
        var failure = (WindowsAppFailure)value;
        var resolver = new FakeResolver { Resolve = (_, _) => throw new WindowsAppConnectionException(failure) };
        var platform = new FakePlatform();
        var saved = false;
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => { saved = true; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.CachedMapping));
        Assert.Equal(failure, error.Failure);
        Assert.False(saved);
        Assert.Empty(platform.Uris);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task PersistenceFailurePreventsActivationAndHidesDatabaseErrors(int failure)
    {
        var platform = new FakePlatform();
        var message = "secret " + ConnectionTestData.Uri;
        Exception expected = failure switch
        {
            0 => new IOException(message),
            1 => new SqliteException(message, 10),
            2 => new UnauthorizedAccessException(message),
            3 => new JsonException(message),
            _ => new SecurityException(message)
        };
        var launcher = new WindowsAppLauncher(new FakeResolver(), platform,
            (_, _, _) => throw expected);
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping));
        Assert.Equal(WindowsAppFailure.PersistenceFailed, error.Failure);
        Assert.Empty(platform.Uris);
        AssertSafe(error);
    }

    [Fact]
    public async Task FailureReleasesTheGateSoAnExplicitRetryCanSucceed()
    {
        var resolver = new FakeResolver { Resolve = (_, _) => throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable) };
        var platform = new FakePlatform();
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => Task.CompletedTask);
        var id = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(id, ConnectionTestData.CachedMapping));
        AssertSafe(error);
        Assert.False(WindowsAppOperationGate.Shared.IsBusy(id));
        Assert.Empty(platform.Uris);
        resolver.Resolve = (_, _) => Task.FromResult(ConnectionTestData.Resolved);
        await launcher.OpenAsync(id, ConnectionTestData.CachedMapping);
        Assert.Single(platform.Uris);
    }

    [Fact]
    public async Task ActivationFailureIsNotSuccessAndRetainsAlreadyCommittedRefresh()
    {
        WindowsAppConnection? saved = null;
        var platform = new WindowsAppPlatform(() => true, _ => throw new Win32Exception("secret " + ConnectionTestData.Uri), new FakeWindows());
        var launcher = new WindowsAppLauncher(new FakeResolver(), platform,
            (_, mapping, _) => { saved = mapping; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping));
        Assert.Equal(WindowsAppFailure.ActivationFailed, error.Failure);
        Assert.Equal(ConnectionTestData.Uri, saved!.LastKnownConnectionUri);
        AssertSafe(error);
    }

    [Fact]
    public async Task ExplicitCacheLaunchNeverResolvesOrSavesAndNeverExpires()
    {
        var resolver = new FakeResolver { Resolve = (_, _) => throw new Exception("Must not resolve") };
        var platform = new FakePlatform();
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => throw new Exception("Must not save"));
        await launcher.OpenLastKnownAsync(Guid.NewGuid(), ConnectionTestData.CachedMapping with
        {
            ConnectionUriRetrievedAtUtc = DateTimeOffset.UnixEpoch
        });
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(ConnectionTestData.Uri, Assert.Single(platform.Uris));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CachedLaunchRequiresMappingValidatedCacheAndProtocol(int shape)
    {
        var platform = new FakePlatform { Available = shape != 3 };
        var resolver = new FakeResolver();
        var mapping = shape switch
        {
            0 => null,
            1 => ConnectionTestData.Mapping,
            2 => ConnectionTestData.CachedMapping with { LastKnownConnectionUri = "https://evil.example/secret" },
            _ => ConnectionTestData.CachedMapping
        };
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => throw new Exception("Must not save"));
        await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenLastKnownAsync(Guid.NewGuid(), mapping));
        Assert.Empty(platform.Uris);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task SameMachineGateIsSharedAcrossLauncherInstancesAndExplicitCache()
    {
        var completion = new TaskCompletionSource<ResolvedDevBoxConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new FakeResolver { Resolve = (_, _) => completion.Task };
        var platform = new FakePlatform();
        var first = new WindowsAppLauncher(resolver, platform, (_, _, _) => Task.CompletedTask);
        var second = new WindowsAppLauncher(new FakeResolver(), platform, (_, _, _) => Task.CompletedTask);
        var id = Guid.NewGuid();
        var pending = first.OpenAsync(id, ConnectionTestData.Mapping);
        Assert.True(WindowsAppOperationGate.Shared.IsBusy(id));
        Assert.Equal(WindowsAppFailure.Busy,
            (await Assert.ThrowsAsync<WindowsAppConnectionException>(() => second.OpenAsync(id, ConnectionTestData.Mapping))).Failure);
        Assert.Equal(WindowsAppFailure.Busy,
            (await Assert.ThrowsAsync<WindowsAppConnectionException>(() => second.OpenLastKnownAsync(id, ConnectionTestData.CachedMapping))).Failure);
        await second.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping);
        completion.SetResult(ConnectionTestData.Resolved);
        await pending;
        Assert.False(WindowsAppOperationGate.Shared.IsBusy(id));
        await second.OpenAsync(id, ConnectionTestData.Mapping);
        Assert.Equal(3, platform.Uris.Count);
    }

    [Fact]
    public async Task SharedGateCanCoverPhaseThreeSaveRefreshSignInAndClear()
    {
        var id = Guid.NewGuid();
        var gate = new WindowsAppOperationGate();
        var launcher = new WindowsAppLauncher(new FakeResolver(), new FakePlatform(), (_, _, _) => Task.CompletedTask, gate);
        var lease = gate.Enter(id);
        await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(id, ConnectionTestData.Mapping));
        lease.Dispose();
        using var next = gate.Enter(id);
        lease.Dispose();
        Assert.True(gate.IsBusy(id));
    }

    [Fact]
    public async Task CancellationDuringResolutionPreventsSaveAndActivationAndReleasesGate()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new FakeResolver { Resolve = (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromResult(ConnectionTestData.Resolved);
        } };
        var platform = new FakePlatform();
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => throw new Exception("Must not save"));
        var id = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.OpenAsync(id, ConnectionTestData.CachedMapping, cancellation.Token));
        Assert.Empty(platform.Uris);
        Assert.False(WindowsAppOperationGate.Shared.IsBusy(id));
    }

    [Fact]
    public async Task CancelledPersistenceNeverLaunchesAndReleasesGate()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform();
        var launcher = new WindowsAppLauncher(new FakeResolver(), platform, (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var id = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.OpenAsync(id, ConnectionTestData.Mapping, cancellation.Token));
        Assert.Empty(platform.Uris);
        Assert.False(WindowsAppOperationGate.Shared.IsBusy(id));
    }

    [Fact]
    public async Task PreCancelledFreshAndCachedActionsDoNothing()
    {
        var resolver = new FakeResolver();
        var platform = new FakePlatform();
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => throw new Exception("Must not save"));
        var cancellation = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping, cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.OpenLastKnownAsync(Guid.NewGuid(), ConnectionTestData.CachedMapping, cancellation));
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(platform.Uris);
    }

    [Fact]
    public async Task InvalidResolvedIdentityAndUriCannotBePersisted()
    {
        foreach (var resolved in new[]
        {
            ConnectionTestData.Resolved with { AzureTenantId = Guid.NewGuid() },
            ConnectionTestData.Resolved with { SubscriptionId = Guid.Empty },
            ConnectionTestData.Resolved with { AzureAccountUpn = "different@example.com" },
            ConnectionTestData.Resolved with { ConnectionUri = new Uri("https://evil.example/secret") }
        })
        {
            var resolver = new FakeResolver { Resolve = (_, _) => Task.FromResult(resolved) };
            var platform = new FakePlatform();
            var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => throw new Exception("Must not save"));
            await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping));
            Assert.Empty(platform.Uris);
        }
    }

    [Fact]
    public void PlatformPassesOriginalStringToShellAndDisposesImmediately()
    {
        ProcessStartInfo? captured = null;
        var process = new Disposable();
        var platform = new WindowsAppPlatform(() => true, start => { captured = start; return process; }, new FakeWindows());
        var uri = WindowsAppConnectionValidator.Validate(ConnectionTestData.Uri.Replace("source=test", "source=%74est"), "user@example.com");
        Assert.True(platform.IsProtocolAvailable());
        Assert.Equal(WindowsAppActivationDisposition.ConnectionUriActivated, platform.Activate(uri, ConnectionTestData.Mapping.DevBoxName));
        Assert.Equal(uri.OriginalString, captured!.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Empty(captured.ArgumentList);
        Assert.Empty(captured.Arguments);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void ShellActivationWithoutNewProcessSucceeds()
    {
        var activations = 0;
        var platform = new WindowsAppPlatform(() => true, _ => { activations++; return null; }, new FakeWindows());
        platform.Activate(new Uri(ConnectionTestData.Uri), ConnectionTestData.Mapping.DevBoxName);
        Assert.Equal(1, activations);
    }

    [Fact]
    public void Win32ActivationFailureIsSanitized()
    {
        var platform = new WindowsAppPlatform(() => true, _ => throw new Win32Exception("secret " + ConnectionTestData.Uri), new FakeWindows());
        var error = Assert.Throws<WindowsAppConnectionException>(() => platform.Activate(new Uri(ConnectionTestData.Uri), ConnectionTestData.Mapping.DevBoxName));
        Assert.Equal(WindowsAppFailure.ActivationFailed, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(18)]
    [InlineData(2)]
    public async Task PackagedDelegatedAndClassicAssociationsAllowFreshAndCachedLaunches(int supportedKind)
    {
        var queries = new List<WindowsAppPlatform.AssociationString>();
        int Query(uint flags, WindowsAppPlatform.AssociationString kind, string association,
            string? extra, nint output, ref uint length)
        {
            Assert.Equal(0x00001000u, flags);
            Assert.Equal("ms-cloudpc", association);
            Assert.Null(extra);
            Assert.Equal(nint.Zero, output);
            Assert.Equal(0u, length);
            queries.Add(kind);
            length = (int)kind == supportedKind ? 64u : 0u;
            return (int)kind == supportedKind ? 1 : unchecked((int)0x80070483);
        }
        var activations = 0;
        var platform = new WindowsAppPlatform(() => WindowsAppPlatform.ProbeRegistration(Query),
            _ => { activations++; return null; }, new FakeWindows());
        var resolver = new FakeResolver();
        var saves = 0;
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => { saves++; return Task.CompletedTask; });

        await launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping);
        await launcher.OpenLastKnownAsync(Guid.NewGuid(), ConnectionTestData.CachedMapping);

        Assert.Equal(2, activations);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(1, saves);
        if (supportedKind != 2)
            Assert.DoesNotContain(WindowsAppPlatform.AssociationString.Executable, queries);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(unchecked((int)0x80070483), 64)]
    [InlineData(unchecked((int)0x80070005), 64)]
    public void MissingEmptyOrFailedAssociationsAreUnavailable(int result, int requiredLength)
    {
        var queries = 0;
        int Query(uint flags, WindowsAppPlatform.AssociationString kind, string association,
            string? extra, nint output, ref uint length)
        {
            queries++;
            length = (uint)requiredLength;
            return result;
        }
        Assert.False(WindowsAppPlatform.ProbeRegistration(Query));
        Assert.Equal(3, queries);
    }

    [Fact]
    public void AssociationProbeFailureIsUnavailableWithoutDisclosingDetails()
    {
        var platform = new WindowsAppPlatform(() => throw new IOException("secret"), _ => throw new Exception("Must not activate"), new FakeWindows());
        Assert.False(platform.IsProtocolAvailable());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExpectedAssociationFailuresBecomeSanitizedProtocolFailures(int failure)
    {
        var message = "secret " + ConnectionTestData.Uri;
        Exception expected = failure switch
        {
            0 => new IOException(message),
            1 => new UnauthorizedAccessException(message),
            _ => new SecurityException(message)
        };
        var resolver = new FakeResolver();
        var platform = new WindowsAppPlatform(() => throw expected, _ => throw new InvalidOperationException("Must not activate"), new FakeWindows());
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => throw new InvalidOperationException("Must not save"));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping));
        Assert.Equal(WindowsAppFailure.ProtocolMissing, error.Failure);
        Assert.Equal(0, resolver.Calls);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProgrammingErrorsPropagateAndReleaseTheGate(int boundary)
    {
        var expected = new InvalidOperationException("Programming error");
        var resolver = new FakeResolver
        {
            Resolve = (_, _) => boundary == 0 ? throw expected : Task.FromResult(ConnectionTestData.Resolved)
        };
        var activations = 0;
        var saves = 0;
        var platform = new WindowsAppPlatform(
            () => boundary == 2 ? throw expected : true,
            _ => { activations++; throw expected; }, new FakeWindows());
        var gate = new WindowsAppOperationGate();
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) =>
        {
            saves++;
            return boundary == 1 ? throw expected : Task.CompletedTask;
        }, gate);
        var id = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.OpenAsync(id, ConnectionTestData.Mapping));
        Assert.Same(expected, error);
        Assert.False(gate.IsBusy(id));
        Assert.Equal(boundary == 3 ? 1 : 0, activations);
        Assert.Equal(boundary is 1 or 3 ? 1 : 0, saves);
    }

    private static void AssertSafe(Exception error)
    {
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("ms-cloudpc:", error.ToString());
        Assert.DoesNotContain(ConnectionTestData.Mapping.DevBoxName, error.ToString());
        Assert.DoesNotContain("987654", error.ToString());
    }

    private sealed class FakeResolver : IDevBoxConnectionResolver
    {
        public int Calls;
        public Func<WindowsAppConnection, CancellationToken, Task<ResolvedDevBoxConnection>> Resolve =
            (_, _) => Task.FromResult(ConnectionTestData.Resolved);
        public Task<ResolvedDevBoxConnection> ResolveAsync(WindowsAppConnection mapping, CancellationToken cancellationToken)
        {
            Calls++;
            return Resolve(mapping, cancellationToken);
        }
    }

    private sealed class FakePlatform : IWindowsAppPlatform
    {
        public bool Available = true;
        public int ProtocolCalls;
        public Func<string, WindowsAppActivationDisposition>? Probe;
        public List<string> Names { get; } = [];
        public Action<Uri>? OnLaunch;
        public List<string> Uris { get; } = [];
        public bool IsProtocolAvailable() { ProtocolCalls++; return Available; }
        public WindowsAppActivationDisposition TryActivateExisting(string devBoxName)
        {
            Names.Add(devBoxName);
            return Probe?.Invoke(devBoxName) ?? WindowsAppActivationDisposition.NoExistingWindow;
        }
        public WindowsAppActivationDisposition Activate(Uri connectionUri, string devBoxName)
        {
            Names.Add(devBoxName);
            OnLaunch?.Invoke(connectionUri);
            Uris.Add(connectionUri.OriginalString);
            return WindowsAppActivationDisposition.ConnectionUriActivated;
        }
    }

    private sealed class Disposable : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
}

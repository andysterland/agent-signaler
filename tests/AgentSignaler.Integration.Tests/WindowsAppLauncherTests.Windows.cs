using System.ComponentModel;
using System.Globalization;
using System.Security;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed partial class WindowsAppLauncherTests
{
    [Theory]
    [InlineData("devbox-name", "devbox-name")]
    [InlineData("DEVBOX-NAME - Windows App", "devbox-name")]
    [InlineData("Windows App: devbox-name", "devbox-name")]
    [InlineData("Windows App (devbox-name)", "devbox-name")]
    [InlineData("[devbox-name] - Windows App", "devbox-name")]
    [InlineData(" \tdevbox-name\r\n", "devbox-name")]
    [InlineData("-devbox-name-", "devbox-name")]
    [InlineData("Windows App -devbox-name- Windows App", "devbox-name")]
    [InlineData("box1-2 - Windows App", "box1-2")]
    [InlineData("box_1.2-name - Windows App", "box_1.2-name")]
    [InlineData("prefixbox1 then box1 - Windows App", "box1")]
    public void TitleMatcherAcceptsExactBoundedNames(string title, string name) =>
        Assert.True(WindowsAppWindowTitleMatcher.IsMatch(title, name));

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    [InlineData('\r')]
    [InlineData('\n')]
    [InlineData('\u00a0')]
    [InlineData('\u2013')]
    [InlineData('\u2014')]
    [InlineData(':')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('[')]
    [InlineData(']')]
    public void TitleMatcherTestsBothSidesOfEverySeparator(char separator)
    {
        Assert.True(WindowsAppWindowTitleMatcher.IsMatch($"App{separator}box1{separator}App", "box1"));
        Assert.True(WindowsAppWindowTitleMatcher.IsMatch($"{separator}box1", "box1"));
        Assert.True(WindowsAppWindowTitleMatcher.IsMatch($"box1{separator}", "box1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Windows App")]
    [InlineData("box10")]
    [InlineData("box1suffix")]
    [InlineData("prefixbox1")]
    [InlineData("prefixbox1suffix")]
    [InlineData("box1-2")]
    [InlineData("prefix-box1")]
    [InlineData("box1.name")]
    [InlineData("box1_name")]
    [InlineData("name.box1")]
    [InlineData("name_box1")]
    [InlineData("/box1/")]
    [InlineData("{box1}")]
    [InlineData("\"box1\"")]
    [InlineData("prefixbox1 box10 box1-2")]
    public void TitleMatcherRejectsEmptyPartialAndUnsupportedBoundaries(string? title) =>
        Assert.False(WindowsAppWindowTitleMatcher.IsMatch(title, "box1"));

    [Fact]
    public void TitleMatcherIsBoundedAndOrdinalNotCultureSensitive()
    {
        var title = "box1" + new string(' ', WindowsAppWindowTitleMatcher.MaximumTitleLength - 4);
        Assert.True(WindowsAppWindowTitleMatcher.IsMatch(title, "box1"));
        Assert.False(WindowsAppWindowTitleMatcher.IsMatch(title + " ", "box1"));
        Assert.False(WindowsAppWindowTitleMatcher.IsMatch("box1", ""));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(WindowsAppWindowTitleMatcher.IsMatch("BUILD-BOX", "build-box"));
            Assert.False(WindowsAppWindowTitleMatcher.IsMatch("bu\u0130ld-box", "build-box"));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstMatchingWindowWinsAndOnlyMinimizedWindowsAreRestored(bool minimized)
    {
        var windows = new FakeWindows { Candidates = [1, 2, 3], Minimized = _ => minimized };
        windows.Title = window => window == 1 ? "unrelated secret title" : "box1 - Windows App";
        var platform = new WindowsAppPlatform(activate: _ => throw new InvalidOperationException("Must not launch"), windows: windows);

        Assert.Equal(WindowsAppActivationDisposition.ExistingWindowActivated,
            platform.Activate(new Uri(ConnectionTestData.Uri), "box1"));

        Assert.Equal(minimized
            ? ["enumerate", "title:1", "title:2", "minimized:2", "restore:2", "foreground:2"]
            : new[] { "enumerate", "title:1", "title:2", "minimized:2", "foreground:2" }, windows.Events);
    }

    [Fact]
    public void UntitledNonmatchingInaccessibleAndInvalidCandidatesAreSkipped()
    {
        var windows = new FakeWindows { Candidates = [1, 2, 3, 4, 5, 6], Exists = window => window != 4 };
        windows.Title = window => window switch
        {
            1 => null,
            2 => "box10",
            3 => throw new Win32Exception("secret unrelated title 987654"),
            5 => new string('x', WindowsAppWindowTitleMatcher.MaximumTitleLength + 1),
            _ => "box1"
        };
        var platform = new WindowsAppPlatform(windows: windows);
        Assert.Equal(WindowsAppActivationDisposition.ExistingWindowActivated, platform.TryActivateExisting("box1"));
        Assert.Equal("foreground:6", windows.Events.Last());
        Assert.DoesNotContain("title:4", windows.Events);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void DisappearingMatchedWindowFallsThroughToNextMatchOrShell(int stage, bool anotherMatch)
    {
        var gone = false;
        var windows = new FakeWindows
        {
            Candidates = anotherMatch ? [1, 2] : [1],
            Exists = window => window != 1 || !gone,
            Title = window => { if (window == 1 && stage == 0) gone = true; return "box1"; },
            Minimized = _ => stage == 1,
            OnRestore = window => { if (window == 1) { gone = true; return false; } return true; },
            OnForeground = window =>
            {
                if (window == 1 && stage >= 2) { gone = true; return stage == 3; }
                return true;
            }
        };
        var launches = 0;
        var platform = new WindowsAppPlatform(activate: _ => { launches++; return null; }, windows: windows);
        Assert.Equal(anotherMatch ? WindowsAppActivationDisposition.ExistingWindowActivated : WindowsAppActivationDisposition.ConnectionUriActivated,
            platform.Activate(new Uri(ConnectionTestData.Uri), "box1"));
        Assert.Equal(anotherMatch ? 0 : 1, launches);
        if (anotherMatch) Assert.Equal("foreground:2", windows.Events.Last());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LiveMatchFailuresAreSanitizedNeverLaunchAndReleaseBothGates(int stage)
    {
        var windows = new FakeWindows { Candidates = [987654], Title = _ => ConnectionTestData.Mapping.DevBoxName };
        var sensitive = "secret unrelated title 987654 " + ConnectionTestData.Mapping.DevBoxName + ConnectionTestData.Uri;
        windows.Minimized = _ => stage == 2 ? throw new Win32Exception(sensitive) : stage is 0 or 3;
        windows.OnRestore = _ => stage == 3 ? throw new UnauthorizedAccessException(sensitive) : false;
        windows.OnForeground = _ => stage == 4 ? throw new SecurityException(sensitive) : false;
        var platform = new WindowsAppPlatform(
            () => throw new InvalidOperationException("Must not probe"),
            _ => throw new InvalidOperationException("Must not launch"), windows);
        var gate = new WindowsAppOperationGate();
        var launcher = new WindowsAppLauncher(new FakeResolver(), platform,
            (_, _, _) => throw new InvalidOperationException("Must not save"), gate);
        var id = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => launcher.OpenAsync(id, ConnectionTestData.Mapping));
        Assert.Equal(WindowsAppFailure.ActivationFailed, error.Failure);
        AssertSafe(error);
        Assert.Null(error.DiagnosticDetail);
        Assert.False(gate.IsBusy(id));

        windows.Minimized = _ => false;
        windows.OnForeground = _ => true;
        // Retry on another thread also proves the process-wide lock was released.
        var retry = await Task.Run(() => launcher.OpenAsync(id, ConnectionTestData.Mapping)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WindowsAppActivationDisposition.ExistingWindowActivated, retry.Disposition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExpectedEnumerationFailuresAllowShellActivation(int kind)
    {
        Exception failure = kind switch
        {
            0 => new Win32Exception("secret"),
            1 => new IOException("secret"),
            2 => new UnauthorizedAccessException("secret"),
            _ => new SecurityException("secret")
        };
        var windows = new FakeWindows { OnEnumerate = () => throw failure };
        var launches = 0;
        var platform = new WindowsAppPlatform(activate: _ => { launches++; return null; }, windows: windows);
        Assert.Equal(WindowsAppActivationDisposition.NoExistingWindow, platform.TryActivateExisting("box1"));
        Assert.Equal(WindowsAppActivationDisposition.ConnectionUriActivated, platform.Activate(new Uri(ConnectionTestData.Uri), "box1"));
        Assert.Equal(1, launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyReuseSkipsProtocolResolutionPersistenceAndShellAndUsesMappedName(bool cached)
    {
        var platform = new FakePlatform
        {
            Available = false,
            Probe = _ => WindowsAppActivationDisposition.ExistingWindowActivated
        };
        var resolver = new FakeResolver { Resolve = (_, _) => throw new InvalidOperationException("Must not resolve") };
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => throw new InvalidOperationException("Must not save"));
        var mapping = cached ? ConnectionTestData.CachedMapping : ConnectionTestData.Mapping;
        var id = Guid.NewGuid();
        var disposition = cached
            ? await launcher.OpenLastKnownAsync(id, mapping)
            : (await launcher.OpenAsync(id, mapping)).Disposition;
        Assert.Equal(WindowsAppActivationDisposition.ExistingWindowActivated, disposition);
        Assert.Equal(mapping.DevBoxName, Assert.Single(platform.Names));
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, platform.ProtocolCalls);
        Assert.Empty(platform.Uris);
        Assert.False(WindowsAppOperationGate.Shared.IsBusy(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoMatchPreservesOrderingAndRechecksImmediatelyBeforeShell(bool cached)
    {
        var events = new List<string>();
        var windows = new FakeWindows { OnEnumerate = () => { events.Add("enumerate"); return []; } };
        var platform = new WindowsAppPlatform(() => { events.Add("protocol"); return true; },
            _ => { events.Add("shell"); return null; }, windows);
        var resolver = new FakeResolver
        {
            Resolve = (_, _) => { events.Add("resolve"); return Task.FromResult(ConnectionTestData.Resolved); }
        };
        var launcher = new WindowsAppLauncher(resolver, platform, (_, _, _) => { events.Add("persist"); return Task.CompletedTask; });
        if (cached) await launcher.OpenLastKnownAsync(Guid.NewGuid(), ConnectionTestData.CachedMapping);
        else await launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping);
        Assert.Equal(cached
            ? ["enumerate", "protocol", "enumerate", "shell"]
            : new[] { "enumerate", "protocol", "resolve", "persist", "enumerate", "shell" }, events);
    }

    [Fact]
    public async Task WindowAppearingDuringResolutionIsReusedAfterCommittingRefresh()
    {
        var windows = new FakeWindows { Title = _ => ConnectionTestData.Mapping.DevBoxName };
        var resolver = new FakeResolver { Resolve = (_, _) =>
        {
            windows.Candidates = [1];
            return Task.FromResult(ConnectionTestData.Resolved);
        } };
        WindowsAppConnection? saved = null;
        windows.OnForeground = _ => { Assert.NotNull(saved); return true; };
        var platform = new WindowsAppPlatform(() => true, _ => throw new InvalidOperationException("Must not launch"), windows);
        var launcher = new WindowsAppLauncher(resolver, platform, (_, mapping, _) => { saved = mapping; return Task.CompletedTask; });
        var result = await launcher.OpenAsync(Guid.NewGuid(), ConnectionTestData.Mapping);
        Assert.Equal(WindowsAppActivationDisposition.ExistingWindowActivated, result.Disposition);
        Assert.Equal(ConnectionTestData.CachedMapping, result.Mapping);
        Assert.Equal(saved, result.Mapping);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(2, windows.Events.Count(value => value == "enumerate"));
        Assert.DoesNotContain(ConnectionTestData.Uri, result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationLockSerializesAcrossPlatformInstancesIncludingEarlyProbes(bool earlyProbe)
    {
        using var insideShell = new ManualResetEventSlim();
        using var releaseShell = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondEnumerated = new ManualResetEventSlim();
        var first = new WindowsAppPlatform(activate: _ =>
        {
            insideShell.Set();
            Assert.True(releaseShell.Wait(TimeSpan.FromSeconds(5)));
            return null;
        }, windows: new FakeWindows());
        var second = new WindowsAppPlatform(activate: _ => null, windows: new FakeWindows
        {
            OnEnumerate = () => { secondEnumerated.Set(); return []; }
        });
        var firstTask = Task.Run(() => first.Activate(new Uri(ConnectionTestData.Uri), "box1"));
        Task<WindowsAppActivationDisposition>? secondTask = null;
        try
        {
            Assert.True(insideShell.Wait(TimeSpan.FromSeconds(5)));
            secondTask = Task.Run(() =>
            {
                secondStarted.Set();
                return earlyProbe ? second.TryActivateExisting("box1") : second.Activate(new Uri(ConnectionTestData.Uri), "box1");
            });
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(secondEnumerated.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally { releaseShell.Set(); }
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(secondTask);
        await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondEnumerated.IsSet);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WindowProgrammingErrorsPropagateAndReleaseActivationLock(int stage)
    {
        var expected = new InvalidOperationException("Programming error");
        var windows = new FakeWindows
        {
            OnEnumerate = () => stage == 0 ? throw expected : [1],
            Title = _ => stage == 1 ? throw expected : "box1",
            Minimized = _ => stage == 2 ? throw expected : false,
            OnForeground = _ => throw expected
        };
        var platform = new WindowsAppPlatform(windows: windows);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => platform.TryActivateExisting("box1")));
        windows.OnEnumerate = () => [];
        Assert.Equal(WindowsAppActivationDisposition.NoExistingWindow,
            await Task.Run(() => platform.TryActivateExisting("box1")).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class FakeWindows : ITopLevelWindowPlatform
    {
        public IReadOnlyList<nint> Candidates = [];
        public Func<IReadOnlyList<nint>>? OnEnumerate;
        public Func<nint, string?> Title = _ => null;
        public Func<nint, bool> Exists = _ => true;
        public Func<nint, bool> Minimized = _ => false;
        public Func<nint, bool> OnRestore = _ => true;
        public Func<nint, bool> OnForeground = _ => true;
        public List<string> Events { get; } = [];
        public IReadOnlyList<nint> Enumerate() { Events.Add("enumerate"); return OnEnumerate?.Invoke() ?? Candidates; }
        public string? GetTitle(nint window) { Events.Add($"title:{window}"); return Title(window); }
        public bool IsWindow(nint window) => Exists(window);
        public bool IsMinimized(nint window) { Events.Add($"minimized:{window}"); return Minimized(window); }
        public bool Restore(nint window) { Events.Add($"restore:{window}"); return OnRestore(window); }
        public bool BringToForeground(nint window) { Events.Add($"foreground:{window}"); return OnForeground(window); }
    }
}

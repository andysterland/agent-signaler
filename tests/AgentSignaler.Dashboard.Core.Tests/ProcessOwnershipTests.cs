using System.Diagnostics;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class ProcessOwnershipTests
{
    [Fact]
    public async Task IndependentProcessesContendAndForcedExitReleasesExactlyOneLease()
    {
        var path = CreateDirectory();
        try
        {
            using var owner = Start("--lease", path);
            try
            {
                Assert.Equal("ready", await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
                using var contender = Start("--lease", path.ToUpperInvariant());
                await contender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, contender.ExitCode);
                Assert.Equal("ownership-conflict", (await contender.StandardError.ReadToEndAsync()).Trim());
                owner.Kill();
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var cleanup = Stopwatch.StartNew();
                DashboardResourceLease lease;
                while (true)
                {
                    try
                    {
                        lease = DashboardResourceLease.Acquire(path, path);
                        break;
                    }
                    catch (DashboardOwnershipException) when (cleanup.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        // Process exit can precede completion of kernel file-handle cleanup.
                        await Task.Delay(25);
                    }
                }
                using var releasedLease = lease;
                Assert.True(lease.IsHeld);
                Assert.Throws<DashboardOwnershipException>(() => DashboardResourceLease.Acquire(path, path));
            }
            finally { await Stop(owner); }
        }
        finally { Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureCliJobContainsChildAndGrandchildDuringNormalAndAbruptOwnerExit(bool abrupt)
    {
        var path = CreateDirectory();
        var marker = Path.Combine(path, "synthetic-owned");
        try
        {
            using var owner = Start("--azure-owner", marker);
            try
            {
                Assert.Equal("ready", await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
                var childId = await ReadPid(marker + ".child");
                var grandchildId = await ReadPid(marker + ".grandchild");
                using var child = Process.GetProcessById(childId);
                using var grandchild = Process.GetProcessById(grandchildId);
                if (abrupt) owner.Kill();
                else await owner.StandardInput.WriteLineAsync("stop");
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await grandchild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(child.HasExited);
                Assert.True(grandchild.HasExited);
                if (!abrupt) Assert.Equal(0, owner.ExitCode);
            }
            finally { await Stop(owner); }
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task JunctionAliasResolvesToTheSameProcessLease()
    {
        var root = CreateDirectory();
        var target = Path.Combine(root, "target");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(target);
        try
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add($"New-Item -ItemType Junction -Path '{alias.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
            using var junction = Process.Start(info)!;
            await junction.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, junction.ExitCode);
            using var lease = DashboardResourceLease.Acquire(target, alias);
            Assert.Equal(lease.CanonicalDirectory, DashboardResourceLease.ResolveExistingDirectory(alias), ignoreCase: true);
            Assert.Throws<DashboardOwnershipException>(() => DashboardResourceLease.Acquire(alias, target));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(root, true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.GetFullPath(Path.Combine("test-artifacts", $"process-core-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static Process Start(string mode, string path)
    {
        var assembly = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "process-fixture-path.txt")).Trim();
        var start = new ProcessStartInfo(Path.ChangeExtension(assembly, ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(path);
        return Process.Start(start)!;
    }
    private static async Task<int> ReadPid(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, deadline.Token), out var pid)) return pid; }
            catch (IOException) { }
            await Task.Delay(25, deadline.Token);
        }
    }
    private static async Task Stop(Process process)
    {
        if (process.HasExited) return;
        process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

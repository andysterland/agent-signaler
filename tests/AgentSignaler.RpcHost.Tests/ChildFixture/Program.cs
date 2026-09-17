using System.Diagnostics;

var directory = Environment.GetEnvironmentVariable("AGENT_SIGNALER_TEST_CHILD_DIRECTORY");
if (directory is null || !Directory.Exists(directory)) return 2;
if (args.Length > 0 && args[0] == "version")
{
    Console.WriteLine("{\"azure-cli\":\"2.90.0\"}");
    return 0;
}
if (args.Length == 0 || args[0] != "--owned-grandchild")
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--owned-grandchild");
    using var child = Process.Start(start) ?? throw new InvalidOperationException();
}
await File.WriteAllTextAsync(Path.Combine(directory, $"{Environment.ProcessId}.running"), Environment.ProcessId.ToString());
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;

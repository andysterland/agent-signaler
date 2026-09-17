using System.Diagnostics;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Dashboard.Core.Tests.ProcessFixture;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) return 2;
        if (args[0] == "--lease")
        {
            try
            {
                using var lease = DashboardResourceLease.Acquire(args[1], args[1]);
                Console.WriteLine("ready");
                await Console.In.ReadLineAsync();
                return 0;
            }
            catch (DashboardOwnershipException) { Console.Error.WriteLine("ownership-conflict"); return 3; }
        }
        if (args[0] == "--azure-owner")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--azure-child");
            start.ArgumentList.Add(args[1]);
            using var child = new AzureCliProcessRunner().Start(start);
            Console.WriteLine("ready");
            await Console.In.ReadLineAsync();
            return 0;
        }
        if (args[0] == "--azure-child")
        {
            File.WriteAllText(args[1] + ".child", Environment.ProcessId.ToString());
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--azure-grandchild");
            start.ArgumentList.Add(args[1]);
            using var grandchild = Process.Start(start)!;
            await Task.Delay(Timeout.Infinite);
        }
        if (args[0] == "--azure-grandchild")
        {
            File.WriteAllText(args[1] + ".grandchild", Environment.ProcessId.ToString());
            await Task.Delay(Timeout.Infinite);
        }
        return 2;
    }
}

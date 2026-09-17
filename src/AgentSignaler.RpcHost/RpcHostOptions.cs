using System.Globalization;
using AgentSignaler.Dashboard;

namespace AgentSignaler.RpcHost;

internal sealed record RpcHostOptions(int? RpcPort, string? DataDirectory)
{
    public static RpcHostOptions Parse(string[] args)
    {
        int? port = null;
        string? directory = null;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Invalid arguments.");
            switch (args[i])
            {
                case "--rpc-port" when port is null:
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                        parsed is < 1024 or > 65535) throw new ArgumentException("Invalid RPC port.");
                    port = parsed;
                    break;
                case "--data-directory" when directory is null:
                    directory = DashboardResourceLease.ValidatePath(Environment.ExpandEnvironmentVariables(args[i + 1]));
                    break;
                default:
                    throw new ArgumentException("Unknown or duplicate argument.");
            }
        }
        return new(port, directory);
    }
}

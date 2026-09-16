using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AgentSignaler.Dashboard;

internal static class Firewall
{
    private const string RuleName = "Agent Signaler Dashboard (Private)";

    public static int RunHelper(string[] args)
    {
        if (args.Length != 2 ||
            args[0] is not ("--firewall-add" or "--firewall-remove") ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535)
            return 2;

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        object? policy = null;
        object? rules = null;
        object? rule = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            policy = Activator.CreateInstance(policyType)!;
            rules = ((dynamic)policy).Rules;
            if (args[0] == "--firewall-remove")
            {
                ((dynamic)rules).Remove(RuleName);
                return 0;
            }

            rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
            dynamic entry = rule;
            entry.Name = RuleName;
            entry.Description = "Agent Signaler HTTP status receiver. No authentication; trusted LAN/VPN only.";
            entry.Protocol = 6; // TCP
            entry.LocalPorts = port.ToString(CultureInfo.InvariantCulture);
            entry.Direction = 1; // Inbound
            entry.Action = 1; // Allow
            entry.Profiles = 2; // Private only, never Public or Domain
            entry.ApplicationName = Environment.ProcessPath!;
            entry.EdgeTraversal = false;
            entry.Enabled = true;
            ((dynamic)rules).Remove(RuleName);
            ((dynamic)rules).Add(entry);
            return 0;
        }
        catch (Exception error) when (error is COMException or Win32Exception or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            // The elevated process communicates only a non-sensitive result code.
            return 4;
        }
        finally
        {
            if (rule is not null && Marshal.IsComObject(rule)) Marshal.FinalReleaseComObject(rule);
            if (rules is not null && Marshal.IsComObject(rules)) Marshal.FinalReleaseComObject(rules);
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    public static async Task<string> ChangeAsync(bool add, int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{(add ? "--firewall-add" : "--firewall-remove")} {port.ToString(CultureInfo.InvariantCulture)}"
            };
            using var helper = Process.Start(start)
                ?? throw new InvalidOperationException("Could not launch the firewall helper.");
            await helper.WaitForExitAsync();
            return helper.ExitCode switch
            {
                0 => add ? "Private-network firewall rule added for the running port." : "Agent Signaler firewall rule removed.",
                2 => "The firewall request was invalid. Restart Agent Signaler and try again.",
                3 => "Administrator approval is required to change the firewall.",
                _ => "Windows could not update the firewall. Check Windows Defender Firewall service and your organization's policy."
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "Administrator approval was canceled. The firewall was not changed.";
        }
    }
}

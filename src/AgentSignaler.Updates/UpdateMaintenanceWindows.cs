using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Updates;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateProcessAdapter : IUpdateProcessAdapter
{
    public UpdateProcessIdentity Current => Find(Environment.ProcessId) ?? throw new InvalidOperationException("update_process_missing");
    public UpdateProcessIdentity? Find(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 87) return null; // PID no longer exists. Access denied is not proof of exit.
            throw new Win32Exception(error, "update_process_query");
        }
        var path = new StringBuilder(1025);
        uint size = 1025;
        if (!QueryFullProcessImageName(process, 0, path, ref size) ||
            !GetProcessTimes(process, out var creation, out _, out _, out _) ||
            !ProcessIdToSessionId((uint)processId, out var session) || !OpenProcessToken(process, 8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "update_process_query");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return new(processId, DateTime.FromFileTimeUtc(creation).Ticks,
                identity.User?.Value ?? throw new UnauthorizedAccessException("update_process_user"),
                checked((int)session), UpdatePath.Canonical(path.ToString()));
    }

    public ImmutableArray<UpdateProcessIdentity> Enumerate(UpdateInstalledPackage installation)
    {
        if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "update_process_inventory");
        try
        {
            if (count > 32768) throw new InvalidDataException("update_process_inventory_limit");
            var result = ImmutableArray.CreateBuilder<UpdateProcessIdentity>();
            var stride = Marshal.SizeOf<WtsProcess>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WtsProcess>(IntPtr.Add(buffer, checked(i * stride)));
                if (entry.UserSid == IntPtr.Zero || new SecurityIdentifier(entry.UserSid).Value != installation.UserSid) continue;
                var process = Find(checked((int)entry.ProcessId));
                if (process is null) continue;
                var parent = Path.GetDirectoryName(process.ExecutablePath)!;
                if (!UpdatePath.EqualsDirectory(parent, installation.InstallDirectory)) continue;
                var name = Path.GetFileName(process.ExecutablePath);
                if (name == "AgentSignaler.Relay.exe")
                    throw new InvalidOperationException("update_relay_active"); // Ephemeral hook/maintenance race; retry explicitly.
                if (name is not ("AgentSignaler.Client.exe" or "AgentSignaler.Configurator.exe" or "AgentSignaler.Dashboard.exe"))
                    throw new InvalidDataException("update_process_unrecognized");
                result.Add(process);
                if (result.Count > UpdateMaintenanceValidation.MaximumParticipants) throw new InvalidDataException("update_participant_capacity");
            }
            return result.ToImmutable();
        }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsProcess { public uint SessionId; public uint ProcessId; public IntPtr Name; public IntPtr UserSid; }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateProcesses(IntPtr server, uint reserved, uint version, out IntPtr processes, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
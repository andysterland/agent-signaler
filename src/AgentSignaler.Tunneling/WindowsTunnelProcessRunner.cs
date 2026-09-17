using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Tunneling;

/// <summary>
/// Starts a verified executable suspended, assigns its non-inheritable kill-on-close job,
/// then resumes it. No executable code runs outside dashboard-owned containment.
/// </summary>
public sealed class WindowsTunnelProcessRunner : ITunnelProcessRunner
{
    private readonly TunnelDiagnostics diagnostics;

    public WindowsTunnelProcessRunner() : this(null) { }

    internal WindowsTunnelProcessRunner(Action<string>? debugOutput) => diagnostics = new(debugOutput);

    public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timing = diagnostics.Begin($"Command {TunnelDiagnostics.CommandName(arguments)}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        deadline.Token.ThrowIfCancellationRequested();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        await using var process = NativeChild.Start(executable, arguments, stdout, stderr, null, 65536, verifyTrust: true, timing);
        using var registration = deadline.Token.Register(process.Kill);
        using var execution = timing.BeginPhase("Command execution");
        try
        {
            var exit = await process.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            execution.Complete(exit);
            timing.Complete(exit);
            return new CliCommandResult(exit, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TunnelException("The CLI command timed out. Pending resource identity was retained.")
            { FailureKind = CliFailureKind.CommandTimeout };
        }
    }

    public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
        Action<string> outputLine, CancellationToken cancellationToken)
    {
        using var timing = diagnostics.Begin("Host process launch");
        cancellationToken.ThrowIfCancellationRequested();
        var process = NativeChild.Start(executable, arguments, null, null, outputLine, 1024 * 1024, verifyTrust: true, timing);
        process.BindCancellation(cancellationToken);
        timing.Complete();
        return Task.FromResult<ITunnelHostProcess>(process);
    }
}

internal sealed class NativeChild : ITunnelHostProcess
{
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle process;
    private readonly StreamReader stdout;
    private readonly StreamReader stderr;
    private readonly FileStream? executableLock;
    private CancellationTokenRegistration cancellation;
    private int disposed;
    public Task<int> Completion { get; }

    private NativeChild(SafeFileHandle job, SafeFileHandle process, SafeFileHandle output, SafeFileHandle error,
        FileStream? executableLock, StringBuilder? outBuffer, StringBuilder? errBuffer, Action<string>? onLine, int limit)
    {
        this.job = job;
        this.process = process;
        this.executableLock = executableLock;
        stdout = new StreamReader(new FileStream(output, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8, true, 4096);
        stderr = new StreamReader(new FileStream(error, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8, true, 4096);
        Completion = ObserveAsync(
            Task.Run(() => Pump(stdout, outBuffer, onLine, limit)),
            Task.Run(() => Pump(stderr, errBuffer, null, limit)));
    }

    internal static NativeChild Start(string executable, IReadOnlyList<string> arguments, StringBuilder? stdout,
        StringBuilder? stderr, Action<string>? outputLine, int limit, bool verifyTrust, TunnelDiagnostics.Scope? timing = null)
    {
        if (!OperatingSystem.IsWindows()) throw new TunnelException("CLI containment requires Windows.", TunnelState.Unsupported)
        { FailureKind = CliFailureKind.UnsupportedPlatform };
        FileStream? lockedFile = null;
        SafeFileHandle? job = null, process = null, thread = null, outRead = null, outWrite = null, errRead = null, errWrite = null, input = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            if (string.IsNullOrWhiteSpace(executable))
                throw new TunnelException("Choose an explicitly installed absolute local CLI executable path.", TunnelState.Unsupported);
            executable = DevTunnelDiagnostics.ValidateCliPath(executable);
            lockedFile = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (verifyTrust)
            {
                using var signature = timing?.BeginPhase("Signature verification");
                Native.VerifyMicrosoftSignature(executable);
                signature?.Complete();
            }

            using var startup = timing?.BeginPhase("Process startup");
            job = Native.CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new Native.JobExtendedLimits();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobExtendedLimits>()))
                throw new Win32Exception();
            var security = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
            if (!Native.CreatePipe(out outRead, out outWrite, ref security, 0) ||
                !Native.CreatePipe(out errRead, out errWrite, ref security, 0) ||
                !Native.SetHandleInformation(outRead, 1, 0) || !Native.SetHandleInformation(errRead, 1, 0))
                throw new Win32Exception();
            input = Native.CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
            if (input.IsInvalid) throw new Win32Exception();
            nuint attributeSize = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeSize);
            attributes = Marshal.AllocHGlobal(checked((int)attributeSize));
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref attributeSize)) throw new Win32Exception();
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errWrite.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            // PROC_THREAD_ATTRIBUTE_JOB_LIST makes ownership atomic with process creation,
            // including a dashboard crash between CreateProcess and ResumeThread.
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x2000D, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var startupInfo = new Native.StartupInfoEx
            {
                StartupInfo = new Native.StartupInfo
                {
                    Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100,
                    StdInput = input.DangerousGetHandle(), StdOutput = outWrite.DangerousGetHandle(),
                    StdError = errWrite.DangerousGetHandle()
                },
                AttributeList = attributes
            };
            var commandLine = new StringBuilder(QuoteArgument(executable));
            foreach (var argument in arguments)
            {
                if (argument.Contains('\0')) throw new ArgumentException("Arguments cannot contain NUL.");
                commandLine.Append(' ').Append(QuoteArgument(argument));
            }
            // CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT.
            if (!Native.CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x08080004,
                IntPtr.Zero, Path.GetDirectoryName(executable), ref startupInfo, out var info))
                throw new Win32Exception();
            process = new SafeFileHandle(info.Process, ownsHandle: true);
            thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            if (Native.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            outWrite.Dispose(); outWrite = null;
            errWrite.Dispose(); errWrite = null;
            var result = new NativeChild(job, process, outRead, errRead, lockedFile, stdout, stderr, outputLine, limit);
            job = process = outRead = errRead = null;
            lockedFile = null;
            startup?.Complete();
            return result;
        }
        catch
        {
            // A failed setup must terminate this exact suspended child as well.
            if (process is { IsInvalid: false }) Native.TerminateProcess(process, 1);
            throw;
        }
        finally
        {
            if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
            input?.Dispose(); thread?.Dispose(); outWrite?.Dispose(); errWrite?.Dispose();
            job?.Dispose(); process?.Dispose(); outRead?.Dispose(); errRead?.Dispose(); lockedFile?.Dispose();
        }
    }

    internal static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
            else result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    internal void BindCancellation(CancellationToken token) => cancellation = token.Register(Kill);
    internal void Kill() => job.Dispose();

    private void Pump(StreamReader reader, StringBuilder? buffer, Action<string>? onLine, int limit)
    {
        var count = 0;
        var line = new StringBuilder();
        var block = new char[1024];
        try
        {
            int read;
            while ((read = reader.Read(block, 0, block.Length)) != 0)
            {
                count += read;
                if (count > limit) throw new TunnelException("The CLI exceeded its bounded output limit.")
                { FailureKind = CliFailureKind.OutputLimit };
                buffer?.Append(block, 0, read);
                if (onLine is null) continue;
                for (var index = 0; index < read; index++)
                {
                    if (block[index] == '\n')
                    {
                        onLine(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else
                    {
                        if (line.Length >= 4096) throw new TunnelException("The CLI exceeded its bounded line length.")
                        { FailureKind = CliFailureKind.OutputLimit };
                        line.Append(block[index]);
                    }
                }
            }
            if (line.Length > 0) onLine?.Invoke(line.ToString().TrimEnd('\r'));
        }
        catch { Kill(); throw; }
    }

    private async Task<int> ObserveAsync(Task output, Task error)
    {
        try
        {
            while (Native.WaitForSingleObject(process, 0) == 0x102)
                await Task.Delay(25).ConfigureAwait(false);
            if (!Native.GetExitCodeProcess(process, out var exit)) throw new Win32Exception();
            Kill(); // An exited root must not leave descendants keeping pipes or relays alive.
            await Task.WhenAll(output, error).ConfigureAwait(false);
            return unchecked((int)exit);
        }
        catch { Kill(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Dispose();
        Kill();
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        finally
        {
            try { stdout.Dispose(); }
            finally
            {
                try { stderr.Dispose(); }
                finally
                {
                    try { process.Dispose(); }
                    finally { executableLock?.Dispose(); }
                }
            }
        }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobExtendedLimits
    {
        public JobBasicLimits BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }

    internal static void VerifyMicrosoftSignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer,
            StateAction = 1, ProviderFlags = 0x1000 // Cache-only trust verification; never opens UI.
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            var trustResult = WinVerifyTrust((IntPtr)(-1), ref action, ref data);
            if (trustResult != 0)
                throw new TunnelException("The installed CLI has no valid trusted Authenticode signature.", TunnelState.Unsupported)
                { FailureKind = CliFailureKind.UntrustedSignature, ErrorCode = trustResult };
#pragma warning disable SYSLIB0057
            using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signedCertificate);
#pragma warning restore SYSLIB0057
            if (certificate.GetNameInfo(X509NameType.SimpleName, false) != "Microsoft Corporation")
                throw new TunnelException("The installed CLI is not signed by Microsoft Corporation.", TunnelState.Unsupported)
                { FailureKind = CliFailureKind.WrongPublisher };
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust((IntPtr)(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
}

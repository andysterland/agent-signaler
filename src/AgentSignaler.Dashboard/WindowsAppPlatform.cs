using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace AgentSignaler.Dashboard;

internal enum WindowsAppActivationDisposition
{
    NoExistingWindow,
    ExistingWindowActivated,
    ConnectionUriActivated
}

internal interface IWindowsAppPlatform
{
    bool IsProtocolAvailable();
    WindowsAppActivationDisposition TryActivateExisting(string devBoxName);
    WindowsAppActivationDisposition Activate(Uri connectionUri, string devBoxName);
}

internal sealed class WindowsAppPlatform(
    Func<bool>? registrationProbe = null,
    Func<ProcessStartInfo, IDisposable?>? activate = null,
    ITopLevelWindowPlatform? windows = null) : IWindowsAppPlatform
{
    private static readonly object activationLock = new();
    private readonly Func<bool> registrationProbe = registrationProbe ?? ProbeRegistration;
    private readonly Func<ProcessStartInfo, IDisposable?> activate = activate ?? (start => Process.Start(start));
    private readonly ITopLevelWindowPlatform windows = windows ?? new WindowsAppWindowPlatform();

    public bool IsProtocolAvailable()
    {
        try { return CheckProtocolAvailability(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    internal bool CheckProtocolAvailability() => registrationProbe();

    public WindowsAppActivationDisposition TryActivateExisting(string devBoxName)
    {
        lock (activationLock) return ActivateExisting(devBoxName);
    }

    public WindowsAppActivationDisposition Activate(Uri connectionUri, string devBoxName)
    {
        lock (activationLock)
        {
            var disposition = ActivateExisting(devBoxName);
            if (disposition == WindowsAppActivationDisposition.ExistingWindowActivated) return disposition;
            try
            {
                // Packaged apps and existing instances can activate without creating a process.
                using var process = activate(new ProcessStartInfo(connectionUri.OriginalString) { UseShellExecute = true });
                return WindowsAppActivationDisposition.ConnectionUriActivated;
            }
            catch (Exception error) when (IsExpectedWindowFailure(error))
            {
                throw new WindowsAppConnectionException(WindowsAppFailure.ActivationFailed);
            }
        }
    }

    private WindowsAppActivationDisposition ActivateExisting(string devBoxName)
    {
        IReadOnlyList<nint> candidates;
        try { candidates = windows.Enumerate(); }
        catch (Exception error) when (IsExpectedWindowFailure(error))
        {
            return WindowsAppActivationDisposition.NoExistingWindow;
        }
        foreach (var window in candidates)
        {
            try
            {
                if (!windows.IsWindow(window) ||
                    !WindowsAppWindowTitleMatcher.IsMatch(windows.GetTitle(window), devBoxName)) continue;
            }
            catch (Exception error) when (IsExpectedWindowFailure(error))
            {
                continue;
            }

            try
            {
                if (!windows.IsWindow(window)) continue;
                if (windows.IsMinimized(window) && !windows.Restore(window))
                {
                    if (!windows.IsWindow(window)) continue;
                    throw new WindowsAppConnectionException(WindowsAppFailure.ActivationFailed);
                }
                if (!windows.BringToForeground(window))
                {
                    if (!windows.IsWindow(window)) continue;
                    throw new WindowsAppConnectionException(WindowsAppFailure.ActivationFailed);
                }
                if (!windows.IsWindow(window)) continue;
                return WindowsAppActivationDisposition.ExistingWindowActivated;
            }
            catch (Exception error) when (IsExpectedWindowFailure(error))
            {
                // A vanished match can be skipped; a live match must never open a duplicate.
                if (!IsGone(window))
                    throw new WindowsAppConnectionException(WindowsAppFailure.ActivationFailed);
            }
        }
        return WindowsAppActivationDisposition.NoExistingWindow;
    }

    private bool IsGone(nint window)
    {
        try { return !windows.IsWindow(window); }
        catch (Exception error) when (IsExpectedWindowFailure(error))
        {
            throw new WindowsAppConnectionException(WindowsAppFailure.ActivationFailed);
        }
    }

    private static bool IsExpectedWindowFailure(Exception error) =>
        error is Win32Exception or IOException or UnauthorizedAccessException or SecurityException;

    private static bool ProbeRegistration()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return ProbeRegistration(AssocQueryString);
    }

    internal enum AssociationString : uint { Executable = 2, DelegateExecute = 18, AppId = 21 }

    internal delegate int AssociationQuery(uint flags, AssociationString kind, string association,
        string? extra, nint output, ref uint length);

    internal static bool ProbeRegistration(AssociationQuery query)
    {
        const uint isProtocol = 0x00001000; // ASSOCF_IS_PROTOCOL uses the current user's association.
        // Store apps may have an AppUserModelID or a delegated verb, not a command line.
        foreach (var kind in new[] { AssociationString.AppId, AssociationString.DelegateExecute, AssociationString.Executable })
        {
            uint length = 0;
            var result = query(isProtocol, kind, "ms-cloudpc", null, 0, ref length);
            // S_FALSE with a nonempty required buffer confirms the association without reading its contents.
            if (result == 1 && length > 1) return true;
        }
        return false;
    }

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryString(uint flags, AssociationString kind, string association,
        string? extra, nint output, ref uint length);
}

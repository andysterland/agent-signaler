using System.Runtime.InteropServices;
using AgentSignaler.Updates;

namespace AgentSignaler.Client;

internal enum UpdateConsent { Declined, Confirmed, Unavailable }

// A dedicated unowned dialog thread keeps the tray procedure and Exit responsive.
// Uses documented TaskDialogIndirect/TDN_TIMER/TDM_CLICK_BUTTON, not MessageBoxTimeout.
internal static class UpdateConfirmation
{
    private const uint Created = 0, Timer = 4, Destroyed = 5;
    private const uint ClickButton = 0x0400 + 102;
    private const int Yes = 6, No = 7;
    private const uint ConfirmationMilliseconds = 60000;

    public static Task<UpdateConsent> ConfirmAsync(MsiReleaseVersion version, bool preview, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(UpdateConsent.Declined);
        var completion = new TaskCompletionSource<UpdateConsent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(Show(version, preview, cancellationToken)); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or System.ComponentModel.Win32Exception or ExternalException)
            { completion.TrySetResult(UpdateConsent.Unavailable); }
        }) { IsBackground = true, Name = "AgentSignaler.UpdateConfirmation" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static UpdateConsent Show(MsiReleaseVersion version, bool preview, CancellationToken cancellationToken)
    {
        nint dialog = 0;
        var sync = new object();
        void Decline()
        {
            lock (sync)
                if (dialog != 0) PostMessage(dialog, ClickButton, No, 0);
        }
        TaskDialogCallback callback = (window, notification, elapsed, _, _) =>
        {
            if (notification == Created)
            {
                lock (sync) dialog = window;
                if (cancellationToken.IsCancellationRequested) Decline();
            }
            else if (notification == Timer && (elapsed >= ConfirmationMilliseconds || cancellationToken.IsCancellationRequested))
                Decline();
            else if (notification == Destroyed)
            {
                lock (sync) dialog = 0;
            }
            return 0;
        };
        using var cancelled = cancellationToken.Register(Decline);
        var configuration = new TaskDialogConfiguration
        {
            Size = (uint)Marshal.SizeOf<TaskDialogConfiguration>(),
            Flags = 0x0008 | 0x0800, // Allow cancellation and documented timer callbacks.
            CommonButtons = 0x0002 | 0x0004, DefaultButton = No,
            WindowTitle = "Agent Signaler Remote update",
            MainInstruction = $"Update Remote to version {version}{(preview ? " (preview)" : "")}?",
            Content = "Client and all open Configurator windows will close. Reporting pauses during servicing. " +
                "Only previously running apps will restart. Unsaved Configurator edits must be saved or discarded before continuing.\n\n" +
                "Choose Yes to authorize this update. This confirmation expires after 60 seconds. " +
                "You can still use Exit from the notification-area menu.",
            Callback = callback
        };
        var result = TaskDialogIndirect(ref configuration, out var selected, 0, 0);
        GC.KeepAlive(callback);
        if (cancellationToken.IsCancellationRequested) return UpdateConsent.Declined;
        if (result < 0) return UpdateConsent.Unavailable;
        return selected == Yes ? UpdateConsent.Confirmed : UpdateConsent.Declined;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TaskDialogCallback(nint window, uint notification, nuint wParam, nint lParam, nint data);

    // CommCtrl.h wraps TASKDIALOGCONFIG in pshpack1.h, including on x64.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct TaskDialogConfiguration
    {
        public uint Size;
        public nint Parent, Instance;
        public uint Flags, CommonButtons;
        public string? WindowTitle;
        public nint MainIcon;
        public string? MainInstruction, Content;
        public uint ButtonCount;
        public nint Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public nint RadioButtons;
        public int DefaultRadioButton;
        public string? VerificationText, ExpandedInformation, ExpandedControlText, CollapsedControlText;
        public nint FooterIcon;
        public string? Footer;
        public TaskDialogCallback? Callback;
        public nint CallbackData;
        public uint Width;
    }

    [DllImport("comctl32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int TaskDialogIndirect(ref TaskDialogConfiguration configuration, out int button, nint radio, nint verified);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}

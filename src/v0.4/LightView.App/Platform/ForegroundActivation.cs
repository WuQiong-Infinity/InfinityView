using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LightView.App.Platform;

internal static class ForegroundActivation
{
    // No unverified/null executable route or ASFW_ANY can reach the native grant.
    internal static bool Grant(bool ownerVerified, string? expectedExecutable, int? verifiedPid, Func<uint,bool>? grant = null)
    {
        if (!ownerVerified || FrameworkApi.IsNullOrWhiteSpace(expectedExecutable) || verifiedPid is null or <= 0) return false;
        try { return (grant ?? AllowSetForegroundWindow)((uint)verifiedPid.Value); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
    internal static bool Request(Action restore, Action show, Action activate, Func<IntPtr> handle, Func<IntPtr,bool> foreground)
    { restore(); show(); activate(); var window = handle(); return window != IntPtr.Zero && foreground(window); }

    public static bool BringToFront(Window window, bool restoreMaximized = false)
    {
        window.Dispatcher.VerifyAccess();
        // Prefer an owned active/visible child; modal native dialogs stay above their owner.
        var child = window.OwnedWindows.Cast<Window>().LastOrDefault(w => w.IsVisible && w.IsActive)
            ?? (!window.IsEnabled ? window.OwnedWindows.Cast<Window>().LastOrDefault(w=>w.IsVisible) : null);
        if (child is not null) window = child;
        IntPtr initial = new WindowInteropHelper(window).Handle;
        if (!window.IsEnabled && initial != IntPtr.Zero)
        {
            IntPtr popup = GetLastActivePopup(initial);
            if (popup != initial && IsWindowVisible(popup)) return SetForegroundWindow(popup);
            return false;
        }
        return Request(
            () => { if (window.WindowState == WindowState.Minimized) window.WindowState = restoreMaximized ? WindowState.Maximized : WindowState.Normal; },
            () => { if (!window.IsVisible) window.Show(); }, () => window.Activate(),
            () => new WindowInteropHelper(window).Handle,
            handle =>
            {
                bool accepted = SetForegroundWindow(handle);
                if (!accepted) { var info = new FlashInfo { Size=(uint)Marshal.SizeOf(typeof(FlashInfo)), Window=handle, Flags=2, Count=1 }; FlashWindowEx(ref info); }
                return accepted;
            });
    }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
}

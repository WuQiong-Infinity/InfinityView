using System.ComponentModel;
using LightView.App.Persistence;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LightView.Core.Settings;

namespace LightView.App.Platform;

internal sealed record MonitorWorkArea(string DeviceName, SavedWindowBounds Bounds, bool Primary = false);
internal sealed record ValidatedPlacement(SavedWindowBounds Bounds, string DeviceName);

internal static class WindowPlacement
{
    public static void Save(string directory, Rect normalBounds, double dpi, bool maximized)
    {
        var store = new SettingsStore(directory); var latest = store.Load();
        if (latest.IsReadOnly || !latest.Settings.Window.RememberPlacement) return;
        Capture(latest.Settings.Window, normalBounds, dpi, maximized);
        if (latest.Settings.Window.RestoreBounds is not null) store.Save(latest.Settings);
    }
    // Persist normal WPF bounds in DIP and their capture DPI. Validation uses physical
    // virtual-screen pixels, preserving legal negative coordinates on left/top monitors.
    public static ValidatedPlacement ValidatePhysical(SavedWindowBounds? saved, IReadOnlyList<MonitorWorkArea> monitors, string? preferred = null,
                                                      double minimumWidth = 640, double minimumHeight = 420)
    {
        if (monitors.Count == 0) throw new InvalidOperationException("未找到可用显示器。");
        MonitorWorkArea fallback = monitors.FirstOrDefault(m => m.DeviceName == preferred) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        bool valid = saved is not null && FrameworkApi.IsFinite(saved.Left) && FrameworkApi.IsFinite(saved.Top) &&
            FrameworkApi.IsFinite(saved.Width) && FrameworkApi.IsFinite(saved.Height) && saved.Width > 0 && saved.Height > 0;
        var intersecting = valid ? monitors.Select(m => (Monitor: m, Area: Intersection(saved!, m.Bounds))).Where(m => m.Area > 0).OrderByDescending(m => m.Area).ToArray() : [];
        var target = intersecting.Length > 0 ? intersecting[0].Monitor : fallback;
        var work = target.Bounds;
        double width = FrameworkApi.Clamp(valid ? saved!.Width : 1120, Math.Min(minimumWidth, work.Width), work.Width);
        double height = FrameworkApi.Clamp(valid ? saved!.Height : 760, Math.Min(minimumHeight, work.Height), work.Height);
        double left = intersecting.Length > 0 ? saved!.Left : work.Left + (work.Width - width) / 2;
        double top = intersecting.Length > 0 ? saved!.Top : work.Top + (work.Height - height) / 2;
        return new(new(left, top, width, height), target.DeviceName);
    }
    private static double Intersection(SavedWindowBounds a, SavedWindowBounds b) =>
        Math.Max(0, Math.Min(a.Left + a.Width, b.Left + b.Width) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Top + a.Height, b.Top + b.Height) - Math.Max(a.Top, b.Top));

    public static bool Restore(Window window, WindowSettings settings)
    {
        if (!settings.RememberPlacement || settings.RestoreBounds is not { } saved) return false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        var monitors = ReadWorkAreas(); if (monitors.Count == 0) return false;
        double savedDpi = settings.SavedDpiScale is double dpi && FrameworkApi.IsFinite(dpi) && dpi > 0 ? dpi : 1;
        var physical = new SavedWindowBounds(saved.Left * savedDpi, saved.Top * savedDpi, saved.Width * savedDpi, saved.Height * savedDpi);
        var initial = ValidatePhysical(physical, monitors, settings.MonitorDeviceName);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        Position(handle, initial.Bounds);
        double currentDpi = Math.Max(96, GetDpiForWindow(handle)) / 96d;
        var desired = initial.Bounds with { Width = saved.Width * currentDpi, Height = saved.Height * currentDpi };
        var final = ValidatePhysical(desired, monitors, initial.DeviceName, window.MinWidth * currentDpi, window.MinHeight * currentDpi);
        Position(handle, final.Bounds);
        if (settings.StartMaximized) window.WindowState = WindowState.Maximized;
        return true;
    }
    public static void Capture(WindowSettings target, Rect normalBounds, double dpi, bool maximized)
    {
        if (normalBounds.IsEmpty || !FrameworkApi.IsFinite(normalBounds.Left) || !FrameworkApi.IsFinite(normalBounds.Top) || !FrameworkApi.IsFinite(normalBounds.Width) || !FrameworkApi.IsFinite(normalBounds.Height) || normalBounds.Width <= 0 || normalBounds.Height <= 0 || !FrameworkApi.IsFinite(dpi) || dpi <= 0) return;
        target.RestoreBounds = new(normalBounds.Left, normalBounds.Top, normalBounds.Width, normalBounds.Height);
        target.SavedDpiScale = dpi;
        var physical = new SavedWindowBounds(normalBounds.Left * dpi, normalBounds.Top * dpi, normalBounds.Width * dpi, normalBounds.Height * dpi);
        var monitors = ReadWorkAreas();
        target.MonitorDeviceName = monitors.Count > 0 ? ValidatePhysical(physical, monitors).DeviceName : null;
        target.StartMaximized = maximized;
    }
    public static IReadOnlyList<MonitorWorkArea> ReadWorkAreas()
    {
        var result = new List<MonitorWorkArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfoW(monitor, ref info)) result.Add(new(info.Device, new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return result;
    }
    private static void Position(IntPtr window, SavedWindowBounds bounds)
    {
        if (!SetWindowPos(window, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0014))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public int Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

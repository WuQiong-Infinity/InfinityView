using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LightView.App.Platform;

internal sealed class WindowAppearance
{
    private static readonly ConditionalWeakTable<Window, WindowAppearance> Instances = new();
    private readonly Window window;
    private HwndSource? source;

    private WindowAppearance(Window window)
    {
        this.window = window;
        window.SourceInitialized += SourceChanged;
        PresentationSource.AddSourceChangedHandler(window, PresentationChanged);
        window.Activated += Refresh;
        window.Deactivated += Refresh;
        window.StateChanged += Refresh;
        window.Closed += Closed;
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        BindSource();
    }

    public static void Attach(Window window) => Instances.GetValue(window, value => new(value));

    // Documented Windows 11 DWM attributes; a failure leaves normal native chrome intact.
    // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    public static bool Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !FrameworkApi.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        try
        {
            uint dark = SystemParameters.HighContrast ? 0u : 1u;
            uint caption = SystemParameters.HighContrast ? uint.MaxValue : ColorRef(window, "ChromeBrush", 0x2B2420);
            uint text = SystemParameters.HighContrast ? uint.MaxValue : ColorRef(window, window.IsActive ? "TextBrush" : "SecondaryBrush", 0xF9F5F2);
            uint border = SystemParameters.HighContrast ? uint.MaxValue : ColorRef(window, window.IsActive ? "BorderBrush" : "DividerBrush", 0x4E4139);
            int a = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
            int b = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
            int c = DwmSetWindowAttribute(handle, 36, ref text, sizeof(uint));
            int d = DwmSetWindowAttribute(handle, 34, ref border, sizeof(uint));
            return a >= 0 && b >= 0 && c >= 0 && d >= 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static uint ColorRef(Window window, string key, uint fallback)
    {
        if (window.TryFindResource(key) is not SolidColorBrush brush) return fallback;
        return (uint)(brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16);
    }
    private void BindSource()
    {
        var current = PresentationSource.FromVisual(window) as HwndSource;
        if (current != source)
        {
            source?.RemoveHook(WndProc); source = current; source?.AddHook(WndProc);
        }
        Apply(window);
    }
    private void SourceChanged(object? sender, EventArgs e) => BindSource();
    private void PresentationChanged(object sender, SourceChangedEventArgs e) => BindSource();
    private void Refresh(object? sender, EventArgs e) => Apply(window);
    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            window.Dispatcher.InvokeAsync(() => Apply(window));
    }
    private IntPtr WndProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x031A or 0x031E or 0x001A) window.Dispatcher.InvokeAsync(() => Apply(window));
        return IntPtr.Zero;
    }
    private void Closed(object? sender, EventArgs e)
    {
        source?.RemoveHook(WndProc); source = null;
        PresentationSource.RemoveSourceChangedHandler(window, PresentationChanged);
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        window.SourceInitialized -= SourceChanged;
        window.Activated -= Refresh; window.Deactivated -= Refresh; window.StateChanged -= Refresh; window.Closed -= Closed;
        Instances.Remove(window);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}

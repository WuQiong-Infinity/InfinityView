using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Persistence;
using LightView.App.Platform;
using LightView.App.Views;
using LightView.Core.Settings;
using LightView.Core.Commands;
using LightView.Core.Viewing;

namespace LightView.Tests;

internal static class FullscreenValidation
{
    public static int Run(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(output, "userdata"));
        var app = new LightView.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var cases = new (string Name, Action Test)[]
        {
            ("top edge requires 120ms continuous dwell and leaving cancels it", Dwell),
            ("chrome hides only after 800ms and holds for menu focus busy/error", Hold),
            ("image panning suppresses chrome and resets top-edge dwell", ImageDrag),
            ("normal mode and disabled auto-hide always keep chrome visible", Modes),
            ("legal negative monitor coordinates and partial visibility survive", Negative),
            ("missing monitor and giant or invalid bounds return to usable work area", Missing),
            ("DPI-scaled bounds remain on the intended monitor", Dpi),
            ("fullscreen overlay never changes viewport size zoom or offsets", () => Overlay(output)),
            ("hidden native restore applies dimensions and does not restore minimized state", () => Native(output)),
            ("placement save preserves latest external settings and future schema", () => External(output))
            ,("real native normal and maximized fullscreen sequences restore exact state", () => NativeSequence(output))
        };
        int failures = 0;
        foreach (var item in cases)
        {
            try { item.Test(); Console.WriteLine("PASS " + item.Name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        }
        Console.WriteLine($"RESULT {cases.Length - failures}/{cases.Length} fullscreen/placement checks passed");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Dwell()
    {
        var state = new FullscreenChromeState(); state.Reset(true, true); var t = DateTimeOffset.UtcNow;
        Check(!state.Update(true, true, true, false, false, t), "Revealed without dwell.");
        Check(!state.Update(true, true, true, false, false, t.AddMilliseconds(119)), "Revealed too early.");
        Check(state.Update(true, true, true, false, false, t.AddMilliseconds(120)), "Did not reveal at threshold.");
        state.Reset(true, true); state.Update(true, true, true, false, false, t);
        state.Update(true, true, false, false, false, t.AddMilliseconds(100));
        Check(!state.Update(true, true, true, false, false, t.AddMilliseconds(200)), "Noncontinuous edge time accumulated.");
    }
    private static void Hold()
    {
        var t = DateTimeOffset.UtcNow; var state = new FullscreenChromeState(); state.Reset(true, true);
        state.Update(true, true, false, false, true, t);
        Check(state.Update(true, true, false, false, true, t.AddMinutes(5)), "Hold was ignored.");
        state.Update(true, true, false, false, false, t.AddMinutes(5));
        Check(state.Update(true, true, false, false, false, t.AddMinutes(5).AddMilliseconds(799)), "Hid before delay.");
        Check(!state.Update(true, true, false, false, false, t.AddMinutes(5).AddMilliseconds(800)), "Did not hide at delay.");
        Check(state.Update(true, true, false, true, false, t.AddMinutes(6)), "Pointer in toolbar did not hold it.");
    }
    private static void Modes()
    {
        var state = new FullscreenChromeState(); state.Reset(true, false); Check(state.IsVisible, "Auto-hide off still hides.");
        Check(state.Update(false, true, false, false, false, DateTimeOffset.UtcNow), "Normal mode hides.");
    }
    private static void ImageDrag()
    {
        var state = new FullscreenChromeState(); var now = DateTimeOffset.UtcNow;
        state.Reset(true, true);
        Check(!state.Update(true, true, true, false, true, now, imageDrag: true), "Capture revealed hidden chrome");
        Check(!state.Update(true, true, true, true, true, now.AddSeconds(4), imageDrag: true), "Panning across edge revealed chrome");
        Check(!state.Update(true, true, true, false, false, now.AddSeconds(5)), "Release reused drag edge dwell");
        Check(state.Update(true, true, true, false, false, now.AddSeconds(5).AddMilliseconds(120)), "Release prevented deliberate edge reveal");
        Check(!state.Update(true, true, false, false, true, now.AddSeconds(6), imageDrag: true), "Visible chrome remained over panning");
        Check(state.Update(false, true, false, false, false, now.AddSeconds(7), imageDrag: true), "Normal window chrome changed");
    }
    private static MonitorWorkArea[] Monitors() => [new("primary", new(0, 0, 1920, 1040), true), new("left", new(-1920, -200, 1920, 1080))];
    private static void Negative()
    {
        var bounds = new SavedWindowBounds(-1700, -100, 900, 700);
        Check(WindowPlacement.ValidatePhysical(bounds, Monitors()).Bounds == bounds, "Legal negative position moved.");
        var partial = new SavedWindowBounds(-2000, 100, 800, 600);
        Check(WindowPlacement.ValidatePhysical(partial, Monitors()).Bounds.Left == -2000, "Partial visible window was unnecessarily recentered.");
    }
    private static void Missing()
    {
        foreach (var saved in new SavedWindowBounds?[] { new(10000, -20000, 9000, 6000), new(double.NaN, 0, 800, 600), null })
        {
            var result = WindowPlacement.ValidatePhysical(saved, [Monitors()[0]], "removed-monitor").Bounds;
            Check(result.Left >= 0 && result.Top >= 0 && result.Left + result.Width <= 1920 && result.Top + result.Height <= 1040, "Window remains outside work area.");
        }
    }
    private static void Dpi()
    {
        var savedDip = new SavedWindowBounds(-1100, 50, 700, 500); double dpi = 1.5;
        var physical = new SavedWindowBounds(savedDip.Left * dpi, savedDip.Top * dpi, savedDip.Width * dpi, savedDip.Height * dpi);
        var result = WindowPlacement.ValidatePhysical(physical, Monitors(), "left", 640 * dpi, 420 * dpi);
        Check(result.DeviceName == "left" && result.Bounds == physical, "DPI conversion lost monitor or position.");
    }
    private static void Overlay(string output)
    {
        var main = new MainWindow(); var root = (FrameworkElement)main.Content; root.DataContext = main.DataContext; main.Content = null;
        var viewport = (ImageViewport)main.FindName("Viewport");
        byte[] bytes = new byte[64 * 32 * 4]; var source = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, bytes, 256); source.Freeze(); viewport.Source = source;
        main.ApplyChromeLayout(true);
        var top = (FrameworkElement)main.FindName("TopBar"); top.Visibility = Visibility.Collapsed;
        void Layout() { root.Measure(new Size(1120, 760)); root.Arrange(new Rect(0, 0, 1120, 760)); root.UpdateLayout(); }
        Layout(); viewport.ActualPixels(); viewport.Transform.Pan(51, -37);
        var before = (viewport.RenderSize, viewport.Transform.Zoom, viewport.Transform.OffsetX, viewport.Transform.OffsetY);
        foreach (var visibility in new[] { Visibility.Visible, Visibility.Collapsed, Visibility.Visible })
        {
            top.Visibility = visibility; Layout();
            Check(before == (viewport.RenderSize, viewport.Transform.Zoom, viewport.Transform.OffsetX, viewport.Transform.OffsetY), "Toolbar changed canvas geometry.");
        }
        File.WriteAllText(Path.Combine(output, "overlay-geometry.json"), System.Text.Json.JsonSerializer.Serialize(new { viewportWidth = viewport.ActualWidth, viewportHeight = viewport.ActualHeight, viewport.Transform.Zoom, viewport.Transform.OffsetX, viewport.Transform.OffsetY, unchanged = true }));
        main.Close();
    }
    private static void Native(string output)
    {
        var work = WindowPlacement.ReadWorkAreas(); Check(work.Count > 0, "No native monitor.");
        var window = new Window { Width = 900, Height = 650, MinWidth = 640, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.Manual };
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var area = work.First(m => m.Primary).Bounds;
        var settings = new WindowSettings { RestoreBounds = new(area.Left + 80, area.Top + 70, 800, 600), SavedDpiScale = 1 };
        try
        {
            Check(WindowPlacement.Restore(window, settings), "No restore performed."); GetWindowRect(handle, out var rect);
            Check(rect.Right - rect.Left >= 640 && rect.Bottom - rect.Top >= 420 && window.WindowState != WindowState.Minimized, "Restored invalid size/state.");
            File.WriteAllText(Path.Combine(output, "native-placement.json"), System.Text.Json.JsonSerializer.Serialize(new { rect.Left, rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top, shown = window.IsVisible }));
            Check(!window.IsVisible, "Hidden native check unexpectedly showed UI.");
        }
        finally { window.Close(); }
    }
    private static void External(string output)
    {
        string dir = Path.Combine(output, "external"); var store = new SettingsStore(dir); var original = store.Load().Settings; store.Save(original);
        var external = new SettingsStore(dir); var latest = external.Load().Settings; latest.ZoomStep = 2; latest.Bindings = [new(CommandCatalog.Open, "K")]; external.Save(latest);
        WindowPlacement.Save(dir, new Rect(100, 90, 800, 600), 1, true);
        var actual = new SettingsStore(dir).Load().Settings;
        Check(actual.ZoomStep == 2 && actual.Bindings.Single().Gesture == "K" && actual.Window.StartMaximized && actual.Window.RestoreBounds == new SavedWindowBounds(100, 90, 800, 600), "Placement overwrote other settings.");
        File.WriteAllText(store.FilePath, "{\"schemaVersion\":99}"); string before = File.ReadAllText(store.FilePath);
        WindowPlacement.Save(dir, new Rect(100, 90, 800, 600), 1, false);
        Check(File.ReadAllText(store.FilePath) == before, "Placement overwrote future schema.");
    }
    private static void NativeSequence(string output)
    {
        var rows = new List<object>();
        foreach (var original in new[] { WindowState.Normal, WindowState.Maximized })
        {
            var window = new MainWindow { ShowActivated = false, Width = 800, Height = 600 };
            var controller = new FullScreenController(window);
            try
            {
                window.Show(); window.WindowState = original; Pump();
                var before = window.WindowState;
                controller.Toggle(); Pump();
                Check(controller.IsFullScreen && window.WindowStyle == WindowStyle.None && window.WindowState == WindowState.Maximized, "Did not enter native fullscreen");
                controller.Exit(); Pump();
                var handle = new WindowInteropHelper(window).Handle;
                Check(window.WindowState == original && IsZoomed(handle) == (original == WindowState.Maximized), "Native maximization restore changed");
                rows.Add(new { before = before.ToString(), after = window.WindowState.ToString(), nativeZoomed = IsZoomed(handle) });
            }
            finally { window.Close(); }
        }
        File.WriteAllText(Path.Combine(output, "native-sequences.json"), System.Text.Json.JsonSerializer.Serialize(rows));
    }
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}

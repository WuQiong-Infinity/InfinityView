using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LightView.App.Platform;

namespace LightView.Tests;

internal static class WindowAppearanceValidation
{
    public static int Run(string output)
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LightView;component/Styles/Colors.xaml", UriKind.Relative) });
        var window = new Window { Title = "LightView native attribute check", WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.CanResize };
        WindowAppearance.Attach(window);
        var handle = new WindowInteropHelper(window).EnsureHandle(); // Never show the test window.
        long before = GetWindowLongPtr(handle, -16).ToInt64();
        bool supported = WindowAppearance.Apply(window);
        long after = GetWindowLongPtr(handle, -16).ToInt64();
        if (before != after || (after & 0x00C40000) != 0x00C40000)
            throw new InvalidOperationException("Appearance removed native caption or resize frame.");
        if (window.WindowStyle != WindowStyle.SingleBorderWindow || window.ResizeMode != ResizeMode.CanResize || window.AllowsTransparency)
            throw new InvalidOperationException("Appearance changed native window behavior.");
        window.Close();
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "native-attributes.json"), System.Text.Json.JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.UtcNow, supported, nativeStyleBefore = before, nativeStyleAfter = after, testWindowShown = false,
            limitation = "DWM colors accept setters but GetWindowAttribute returns E_INVALIDARG for these attributes on this system; actual colors require screenshot inspection." }));
        Console.WriteLine($"PASS DWM setters accepted={supported}, native caption/resize flags preserved, hidden test window closed");
        return 0;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}

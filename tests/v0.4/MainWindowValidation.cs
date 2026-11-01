using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Persistence;
using LightView.App.Views;
using LightView.Core.Commands;

namespace LightView.Tests;

internal static class MainWindowValidation
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(Path.GetFullPath(output), "userdata"));
        var store = new SettingsStore(Path.Combine(Path.GetFullPath(output), "userdata"));
        var settings = store.Load().Settings;
        settings.Bindings = settings.Bindings.Select(b => b.CommandId == CommandCatalog.Open ? b with { Gesture = "CTRL+O" } : b).ToList();
        store.Save(settings);
        var app = new LightView.App.App(); app.InitializeComponent();
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        root.DataContext = window.DataContext;
        window.Content = null; // Layout the client visual without showing or sizing an HWND.
        TextBlock.SetForeground(root, (Brush)app.FindResource("TextBrush"));
        TextBlock.SetFontSize(root, 14);
        foreach (var (width, height) in new[] { (1120, 760), (640, 420) })
        {
            window.UpdateResponsiveCommands(width);
            root.Measure(new Size(width - 16, height - 39)); root.Arrange(new Rect(0, 0, width - 16, height - 39)); root.UpdateLayout();
            var top = (FrameworkElement)window.FindName("TopBar");
            var buttons = Descendants(top).OfType<ButtonBase>().Where(b => b.ActualWidth > 0 && AncestorsShown(b, top)).ToArray();
            if (buttons.Length != (width < 960 ? 5 : 10)) throw new InvalidOperationException("Wrong responsive toolbar button count.");
            var rectangles = buttons.Select(b => (b.Name, Rect: new Rect(b.TranslatePoint(new Point(), top), b.RenderSize))).ToArray();
            foreach (var b in rectangles)
            {
                if (b.Rect.Left < 0 || b.Rect.Right > top.ActualWidth + 0.5) throw new InvalidOperationException($"{width}: {b.Name} runs outside the toolbar");
                foreach (var other in rectangles.Where(o => o.Name != b.Name))
                    if (Rect.Intersect(b.Rect, other.Rect) is { Width: > 0.5, Height: > 0.5 }) throw new InvalidOperationException($"{width}: {b.Name} overlaps {other.Name}");
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"main-{width}.png")); encoder.Save(stream);
            Console.WriteLine($"PASS {width}x{height} toolbar: {buttons.Length} visible buttons, no overlap; offscreen render saved");
        }
        var open = (Button)window.FindName("OpenButton");
        if (open.ToolTip?.ToString()?.Contains("Ctrl+O", StringComparison.OrdinalIgnoreCase) != true) throw new InvalidOperationException("Tooltip ignored custom binding.");
        var menu = (ContextMenu)window.FindName("MoreMenu");
        foreach (string command in new[] { CommandCatalog.Settings, CommandCatalog.FullScreen, CommandCatalog.Details, CommandCatalog.Undo, CommandCatalog.Recovery })
            if (!menu.Items.OfType<MenuItem>().Any(item => item.Tag as string == command)) throw new InvalidOperationException("Overflow lost " + command);
        window.Close();
        Console.WriteLine("PASS custom binding tooltip and overflow command reachability; test window was never shown");
        return 0;
    }
    private static bool AncestorsShown(DependencyObject item, DependencyObject stop)
    {
        for (DependencyObject? current = item; current is not null && current != stop; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class SettingsNavigationValidation
{
    public static int Run(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(output, "userdata"));
        var app = new LightView.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        string missing = Path.Combine(output, "never-created-target");
        var initial = new AppSettings { Presets = [new FolderPreset { Id = "stable-trip", Name = "旅行", Path = missing }] };
        initial.Bindings.Add(new("future.command", "J"));
        AppSettings? saved = null;
        var window = new SettingsWindow(initial, candidate => { saved = candidate.Clone(); return Task.FromResult<string?>(null); }, null, false);
        var tabs = (TabControl)window.FindName("SettingsTabs"); Check(tabs.Items.Count == 6, "Six groups missing.");
        var search = (TextBox)window.FindName("CommandSearch"); var grid = (DataGrid)window.FindName("BindingsGrid");
        tabs.SelectedIndex = 2; search.Text = "旅行";
        Check(grid.Items.Count == 2 && grid.Items.Cast<ShortcutRow>().All(r => r.CommandId.EndsWith("stable-trip")), "Search lost preset actions.");
        search.Text = "MOUSE"; Check(grid.Items.Count == 0, "Search display gesture should use shown names, not hidden IDs.");
        search.Text = "";
        ((Button)window.FindName("RestoreShortcutsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(grid.Items.Cast<ShortcutRow>().Any(r => r.CommandId == "future.command" && r.Gesture == "J"), "Shortcut reset discarded unavailable command.");
        tabs.SelectedIndex = 1;
        ((TextBox)window.FindName("ZoomStepBox")).Text = "2";
        ((CheckBox)window.FindName("FullscreenAutoHideCheck")).IsChecked = false;
        ((CheckBox)window.FindName("RememberPlacementCheck")).IsChecked = false;
        ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(saved is not null && saved.ZoomStep == 2 && !saved.Window.FullScreenAutoHide && !saved.Window.RememberPlacement && saved.Presets[0].Id == "stable-trip" && saved.Presets[0].Path == missing, "Grouped apply changed IDs/paths or missed options.");
        var root = (FrameworkElement)window.Content; window.Content = null;
        TextBlock.SetForeground(root, (Brush)app.FindResource("TextBrush")); TextBlock.SetFontSize(root, 14);
        foreach (int index in Enumerable.Range(0, 6))
        {
            tabs.SelectedIndex = index;
            foreach (int width in index is 0 or 3 ? new[] { 944, 784 } : new[] { 944 })
            {
                int height = width == 784 ? 521 : 681;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                var apply = (Button)window.FindName("ApplyButton"); var point = apply.TranslatePoint(new Point(), root);
                Check(point.X >= 0 && point.X + apply.ActualWidth <= width && point.Y + apply.ActualHeight <= height, "Fixed footer fell outside the minimum window.");
                if (index == 3 && width == 784)
                    Check(((ScrollViewer)window.FindName("OrganizeScroll")).ScrollableHeight > 0 && ((DataGrid)window.FindName("PresetsGrid")).ActualHeight >= 180, "Minimum organize page needs a usable preset table and scroll access to its actions.");
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"settings-group-{index}-{width}.png")); encoder.Save(stream);
            }
        }
        window.Close();
        var row = new FolderPresetRow(new FolderPreset { Id = "unchanged", Name = "人工预设", Path = missing });
        row.RefreshPathStatusAsync().GetAwaiter().GetResult();
        Check(row.PathStatus == "不存在或不可访问" && !Directory.Exists(missing) && row.Id == "unchanged", "Path check wrote a folder or changed identity.");
        row.Path = ""; row.RefreshPathStatusAsync().GetAwaiter().GetResult(); Check(row.PathStatus == "未设置", "Empty target state wrong.");
        Console.WriteLine("PASS six groups, search, preserved unknown bindings, grouped options/IDs, fixed footer at 960/800, read-only path status; eight offscreen pages rendered");
        return 0;
    }
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
}

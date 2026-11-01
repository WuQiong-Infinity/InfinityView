using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LightView.App.Organizing;

namespace LightView.App.Views;

internal sealed class RecoveryWindow : Window
{
    public RecoveryWindow(string directory, RecoveryScan scan, Func<RecoveryItem, Task<string?>> keepAll)
    {
        Title = "未结束的文件操作 - InfinityView"; Width = 1080; Height = 580; MinWidth = 700; MinHeight = 400;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/InfinityView;component/Assets/InfinityView.ico"));
        LightView.App.Platform.WindowAppearance.Attach(this);
        SetResourceReference(BackgroundProperty, "WindowBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont"); FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var heading = new TextBlock { Text = "以下记录需要核对。未自动重放移动、删除源或清理临时文件。确认保留只记录决定，所有文件仍保留。恢复区不会自动清理，可在日志目录核对路径与空间占用。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var message = new TextBlock { Text = scan.Warning ?? $"显示 {scan.Items.Count} 条中断或保留副本记录。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(message, Dock.Bottom); root.Children.Add(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = scan.Items.ToList(),
            Background = Background, Foreground = Foreground,
            RowBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 36, 36)),
            AlternatingRowBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 42, 42)) };
        grid.SetResourceReference(Control.BackgroundProperty, "WindowBrush"); grid.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "SurfaceBrush"); grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "ChromeBrush");
        foreach (var (header, binding) in new[] { ("操作", "OperationId"), ("状态", "State"), ("原位置", "Source"), ("目标", "Destination"), ("临时文件", "Temporary") })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(binding), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(details, Dock.Bottom); root.Children.Add(details);
        grid.SelectionChanged += (_, _) => details.Text = grid.SelectedItem is RecoveryItem item
            ? $"{item.ExistingFiles}\n源：{item.Source}\n目标：{item.Destination}\n临时：{item.Temporary}" : "";
        var open = new Button { Content = "打开日志目录" }; buttons.Children.Add(open);
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { message.Text = error.Message; }
        };
        var location = new Button { Content = "打开所选目标位置" }; buttons.Children.Add(location);
        location.Click += (_, _) =>
        {
            if (grid.SelectedItem is not RecoveryItem item) return;
            try
            {
                string folder = System.IO.Path.GetDirectoryName(item.Destination) ?? throw new System.IO.IOException("目标位置未知。");
                using var lease = LightView.App.Platform.WindowsFileHandle.PinDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception)
            { message.Text = "未打开位置：" + error.Message; }
        };
        var keep = new Button { Content = "保留全部文件并确认所选" }; buttons.Children.Add(keep);
        keep.Click += async (_, _) =>
        {
            if (grid.SelectedItem is not RecoveryItem item) return;
            if (item.IsRetainedCopy) { message.Text = "该副本已安全保留，可打开目标位置查看；不会自动清理。"; return; }
            keep.IsEnabled = false;
            try
            {
                string? error = await keepAll(item);
                if (error is not null) { message.Text = error; return; }
                grid.ItemsSource = ((IEnumerable<RecoveryItem>)grid.ItemsSource).Where(row => row != item).ToList();
                message.Text = "已记录保留全部文件；未移动或删除任何图片。";
            }
            finally { keep.IsEnabled = true; }
        };
        var close = new Button { Content = "关闭" }; close.Click += (_, _) => Close(); buttons.Children.Add(close);
        root.Children.Add(grid);
        if (scan.Items.Count > 0) grid.SelectedIndex = 0;
    }
}

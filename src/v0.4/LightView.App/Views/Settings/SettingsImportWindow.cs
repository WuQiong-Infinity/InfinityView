using System.Windows;
using System.Windows.Controls;
using LightView.App.Persistence;
using LightView.App.Platform;
using LightView.Core.Settings;

namespace LightView.App.Views.Settings;

internal sealed class SettingsImportWindow : Window
{
    private readonly AppSettings baseline;
    private readonly SettingsImportPreview preview;
    private readonly (SettingsGroups Group, CheckBox Box)[] choices;
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button apply = new() { Content = "应用导入", IsDefault = true };
    public AppSettings? SelectedSettings { get; private set; }
    public SettingsImportWindow(string fileName, AppSettings baseline, SettingsImportPreview preview)
    {
        this.baseline = baseline.Clone(); this.preview = preview;
        Title = "导入配置 — InfinityView"; Width = 660; Height = 580; MinWidth = 600; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont"); FontSize = 14;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/InfinityView;component/Assets/InfinityView.ico"));
        WindowAppearance.Attach(this);
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var content = new StackPanel(); root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(new TextBlock { Text = "选择要替换的设置组", Style = (Style)FindResource("PageTitle") });
        content.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(fileName), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "以当前已应用设置为基准，只替换勾选组。当前尚未应用的编辑将放弃。取消不会保存或改变运行设置。", Style = (Style)FindResource("SecondaryText"), Margin = new Thickness(0, 12, 0, 12) });
        choices = new[] { (SettingsGroups.Appearance, "外观与显示"), (SettingsGroups.Viewing, "查看与操作"), (SettingsGroups.Shortcuts, "快捷键与鼠标"), (SettingsGroups.Organizing, "整理预设与恢复选项") }
            .Select(item => (item.Item1, new CheckBox { Content = item.Item2, IsChecked = true })).ToArray();
        foreach (var choice in choices) { content.Children.Add(choice.Box); choice.Box.Checked += (_, _) => Refresh(); choice.Box.Unchecked += (_, _) => Refresh(); }
        content.Children.Add(summary);
        apply.Click += (_, _) => { SelectedSettings = BuildSelection(); DialogResult = true; };
        Refresh();
    }
    internal AppSettings BuildSelection() => SettingsTransfer.Merge(baseline, preview,
        choices.Where(choice => choice.Box.IsChecked == true).Aggregate(SettingsGroups.None, (all, choice) => all | choice.Group));
    private void Refresh()
    {
        try
        {
            var candidate = BuildSelection(); apply.IsEnabled = true;
            var lines = new List<string>();
            if (choices[0].Box.IsChecked == true) lines.Add($"外观：字号 {baseline.Appearance.DetailsFontSize:0} → {candidate.Appearance.DetailsFontSize:0}，描边 {baseline.Appearance.DetailsOutlineThickness:0.0} → {candidate.Appearance.DetailsOutlineThickness:0.0}；详情布局将替换。");
            if (choices[1].Box.IsChecked == true) lines.Add($"查看：每档缩放 {baseline.ZoomStep:0.##} → {candidate.ZoomStep:0.##}；保持缩放位置 {baseline.KeepViewOnImageChange} → {candidate.KeepViewOnImageChange}；同时替换鼠标切图区域、拖动按钮、全屏和窗口记忆选项。");
            if (choices[2].Box.IsChecked == true) lines.Add($"快捷键：{baseline.Bindings.Count} → {candidate.Bindings.Count} 条；不会按预设名称重定向绑定。");
            if (choices[3].Box.IsChecked == true)
            { lines.Add($"整理：{baseline.Presets.Count} → {candidate.Presets.Count} 个预设。" + (preview.IncludesTargetPaths ? "包含本机文件夹路径。" : "包中不含路径，需要重新配置。")); lines.AddRange(preview.Warnings); }
            lines.Add("应用前会备份当前配置。导入不会创建目标目录、操作照片或更改 Windows 默认应用。");
            summary.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
        }
        catch (FormatException error) { apply.IsEnabled = false; summary.Text = error.Message; }
    }
}

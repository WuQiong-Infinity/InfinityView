using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightView.Tests;

internal static class VisualResourceValidation
{
    public static int Run(string output, bool interactive = false)
    {
        Directory.CreateDirectory(output);
        var app = new Application();
        foreach (string name in new[] { "Colors", "Typography", "Controls", "Icons" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"/LightView;component/Styles/{name}.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "LightView · 控件验证", Style = (Style)app.FindResource("PageTitle") });
        var disabled = new Button { Content = "禁用按钮", IsEnabled = false, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal };
        buttonRow.Children.Add(new Button { Content = "普通按钮", Width = 140 }); buttonRow.Children.Add(disabled);
        buttonRow.Children.Add(new ToggleButton { Content = "已选中", IsChecked = true, Width = 140, Margin = new Thickness(4) });
        panel.Children.Add(buttonRow);
        panel.Children.Add(new CheckBox { Content = "选中复选框", IsChecked = true });
        panel.Children.Add(new CheckBox { Content = "未选中复选框" });
        panel.Children.Add(new TextBox { Text = "路径与文字 · C:\\样本", Margin = new Thickness(0, 8, 0, 8) });
        var combo = new ComboBox { ItemsSource = new[] { "自动改名，保留双方", "跳过", "每次询问" }, SelectedIndex = 0 };
        panel.Children.Add(combo);
        var slider = new Slider { Minimum = 0, Maximum = 3, Value = 1.2, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(slider);
        var list = new ListBox { ItemsSource = new[] { "外观与显示", "查看与操作" }, SelectedIndex = 0 };
        panel.Children.Add(list);
        var border = new Border { Background = (Brush)app.FindResource("WindowBrush"), Child = panel, Width = 600, Height = 560 };
        border.Measure(new Size(600, 560)); border.Arrange(new Rect(0, 0, 600, 560)); border.UpdateLayout();
        if (combo.Template.FindName("PART_Popup", combo) is not Popup || slider.Template.FindName("PART_Track", slider) is not Track)
            throw new InvalidOperationException("Interactive template parts are missing.");
        var bitmap = new RenderTargetBitmap(600, 560, 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "controls.png"))) encoder.Save(stream);
        var probe = new RenderTargetBitmap((int)disabled.ActualWidth, (int)disabled.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        probe.Render(disabled); byte[] pixels = new byte[probe.PixelWidth * probe.PixelHeight * 4]; probe.CopyPixels(pixels, probe.PixelWidth * 4, 0);
        int white = 0;
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 220 && pixels[i + 1] > 220 && pixels[i + 2] > 220) white++;
        if (white > probe.PixelWidth * probe.PixelHeight / 4) throw new InvalidOperationException("Disabled button regressed to a white surface.");
        var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/LightView;component/Assets/LightView.ico"), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int[] sizes = icon.Frames.Select(f => f.PixelWidth).OrderBy(value => value).ToArray();
        if (!sizes.SequenceEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })) throw new InvalidOperationException("ICO sizes lost.");
        Console.WriteLine("PASS resource loading, templates, dark disabled surface, nine ICO frames; controls.png rendered");
        if (interactive)
        {
            var window = new Window { Title = "LightView 控件验证 V02-01", Content = border, Width = 640, Height = 640,
                Foreground = (Brush)app.FindResource("TextBrush"), Background = (Brush)app.FindResource("WindowBrush"),
                FontFamily = (FontFamily)app.FindResource("UiFont"), FontSize = 14 };
            app.Run(window);
        }
        return 0;
    }
}

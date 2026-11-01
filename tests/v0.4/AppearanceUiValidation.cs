using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightView.App.Imaging;
using LightView.App.Organizing;
using LightView.App.Views;
using LightView.App.Views.Settings;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class AppearanceUiValidation
{
    public static int Run(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(output, "userdata"));
        var app = new LightView.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var page = new AppearancePage();
        AppearanceSettings? previewed = null; page.Changed += a => previewed = a;
        ((Slider)page.FindName("FontSlider")).Value = 21;
        if (previewed?.DetailsFontSize != 21) throw new InvalidOperationException("Appearance editor did not emit a draft snapshot.");
        foreach (var (width, height) in new[] { (900, 580), (680, 800) })
        {
            page.Measure(new Size(width, height)); page.Arrange(new Rect(0, 0, width, height)); page.UpdateLayout();
            var border = new Border { Width = width, Height = height, Background = (Brush)app.FindResource("WindowBrush"), Child = page };
            TextBlock.SetForeground(border, (Brush)app.FindResource("TextBrush")); TextBlock.SetFontSize(border, 14);
            border.Measure(new Size(width, height)); border.Arrange(new Rect(0, 0, width, height)); border.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, $"appearance-{width}.png"))) encoder.Save(stream);
            border.Child = null;
        }
        var main = new MainWindow(); var viewport = (ImageViewport)main.FindName("Viewport");
        byte[] bytes = new byte[32 * 32 * 4]; var source = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, bytes, 128); source.Freeze();
        var version = new FileSnapshot("artificial.png", "artificial-only", bytes.Length, 0, "artificial");
        viewport.Image = new LoadedImage("artificial.png", source, "artificial", 0, FileVersion: version);
        main.ApplyAppearance(new AppearanceSettings { DetailsFontSize = 24, DetailsOutlineThickness = 3, DetailsWidth = 600, DetailsFitSmallWindow = false, TransparencyCheckerboard = true, CanvasBackground = "midGray" });
        if (!ReferenceEquals(source, viewport.Source) || !ReferenceEquals(version, viewport.Image.FileVersion) ||
            ((Border)main.FindName("DetailsPanel")).IsHitTestVisible || ((Border)main.FindName("DetailsPanel")).Width != 600)
            throw new InvalidOperationException("Appearance changed source/version or interactive details semantics.");
        main.Close();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new SettingsWindow(new AppSettings(), _ => gate.Task, null, false);
        bool closed = false; editor.Closed += (_, _) => closed = true;
        ((Button)editor.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!editor.IsSaving || ((Grid)editor.FindName("EditorRoot")).IsEnabled) throw new InvalidOperationException("Save did not freeze all editor controls.");
        editor.Close(); if (closed) throw new InvalidOperationException("Editor closed during save.");
        gate.SetResult(null);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (editor.IsSaving && DateTime.UtcNow < deadline)
        { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { })); Thread.Sleep(5); }
        if (editor.IsSaving || !((Grid)editor.FindName("EditorRoot")).IsEnabled) throw new InvalidOperationException("Save did not unfreeze editor.");
        var client = (FrameworkElement)editor.Content; editor.Content = null;
        TextBlock.SetForeground(client, (Brush)app.FindResource("TextBrush")); TextBlock.SetFontSize(client, 14);
        client.Measure(new Size(944, 681)); client.Arrange(new Rect(0, 0, 944, 681)); client.UpdateLayout();
        if (((AppearancePage)editor.FindName("AppearanceEditor")).Foreground is not SolidColorBrush textBrush || textBrush.Color != Color.FromRgb(242, 245, 249))
            throw new InvalidOperationException("Selected tab leaked accent into page body.");
        var clientBitmap = new RenderTargetBitmap(944, 681, 96, 96, PixelFormats.Pbgra32); clientBitmap.Render(client);
        var clientEncoder = new PngBitmapEncoder(); clientEncoder.Frames.Add(BitmapFrame.Create(clientBitmap));
        using (var stream = File.Create(Path.Combine(output, "settings-client.png"))) clientEncoder.Save(stream);
        editor.Close(); if (!closed) throw new InvalidOperationException("Editor could not close after save.");
        Console.WriteLine("PASS appearance controls emit snapshots, wide/narrow offscreen previews rendered, source/version retained and details remain mouse-through; no test window shown");
        Console.WriteLine("PASS full editor disabled during save, close refused until completion, then reenabled; selected tab text is scoped to header");
        return 0;
    }
}

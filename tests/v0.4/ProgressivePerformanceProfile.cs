using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightView.App.Imaging;
using LightView.App.ViewModels;
using LightView.App.Views;

namespace LightView.Tests;

internal static class ProgressivePerformanceProfile
{
    public static int Run(string gallery, string output)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        Task task = MeasureAsync(gallery, output);
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); return 0;
    }
    private static async Task MeasureAsync(string gallery, string output)
    {
        string[] files = Directory.GetFiles(Path.GetFullPath(gallery), "*.jpg").OrderBy(value => value).Take(18).ToArray();
        if (files.Length != 18) throw new IOException("Expected 18 artificial JPEG fixtures");
        var rows = new List<object>();
        foreach (bool initialIdle in new[] { false, true })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var loader = new WicImageLoader(); var viewer = new ViewerViewModel(loader) { PreviewMaximumEdge = 1536 };
            var viewport = new ImageViewport { Width = 1000, Height = 650 };
            viewport.Measure(new Size(1000, 650)); viewport.Arrange(new Rect(0, 0, 1000, 650));
            var render = new RenderTargetBitmap(1000, 650, 96, 96, PixelFormats.Pbgra32);
            var samples = new List<object>(); var values = new List<double>(); var tasks = new List<Task>();
            long peakCache = 0, peakPrivate = 0;
            void Memory()
            {
                peakCache = Math.Max(peakCache, loader.CacheBytes);
                using var process = Process.GetCurrentProcess(); peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
            }
            var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            monitor.Tick += (_, _) => Memory(); monitor.Start();
            try
            {
                await viewer.OpenAsync(files[0]); await viewer.FolderReady;
                if (initialIdle) await viewer.PrefetchReady;
                for (int i = 1; i <= 16; i++)
                {
                    string expected = files[i]; var visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var watch = Stopwatch.StartNew(); bool recorded = false;
                    void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                    {
                        if (recorded || args.PropertyName != nameof(ViewerViewModel.DisplayedImage) || viewer.DisplayedImage?.FilePath != expected) return;
                        recorded = true; double commitMs = watch.Elapsed.TotalMilliseconds;
                        viewport.Image = viewer.DisplayedImage; render.Render(viewport);
                        double visibleMs = watch.Elapsed.TotalMilliseconds; values.Add(visibleMs); Memory();
                        samples.Add(new { index = i, commitMs, firstOffscreenRenderMs = visibleMs, preview = viewer.IsPreview });
                        visible.TrySetResult();
                    }
                    viewer.PropertyChanged += Changed;
                    try
                    {
                        Task open = viewer.NavigateAsync(1); tasks.Add(open);
                        await visible.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    finally { viewer.PropertyChanged -= Changed; }
                }
                var fullWatch = Stopwatch.StartNew(); await viewer.FullReady;
                await Task.WhenAll(tasks); Memory();
                if (viewer.DisplayedImage?.FilePath != files[16] || viewer.IsPreview || viewer.OrganizingImage is null || viewer.ErrorMessage.Length > 0)
                    throw new IOException("Final full readiness/version failed");
                double[] sorted = values.OrderBy(value => value).ToArray();
                var row = new { initialIdle, medianMs = sorted[8], p95Ms = sorted[15], finalUpgradeAfterVisibleMs = fullWatch.Elapsed.TotalMilliseconds,
                    cacheHits = loader.CacheHits, peakCacheBytes = peakCache, peakPrivateBytes = peakPrivate, samples };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
            }
            finally
            {
                monitor.Stop(); viewer.CancelPrefetch(); await viewer.PrefetchReady; viewport.Image = null;
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow,
            method = "16 consecutive first-visible forward switches on the WPF dispatcher; next request immediately follows offscreen rendering. 18 artificial 24MP JPEG copies. SHA256 retained. Initial idle group waits for initial preview prefetch once; cold group does not. 1000x650 offscreen composition, not physical display or HV comparison. Private memory sampled at 30ms plus commits; cache limit 256MiB, one full JPEG plus previews.", rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

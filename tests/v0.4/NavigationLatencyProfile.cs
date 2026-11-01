using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Organizing;
using LightView.App.ViewModels;

namespace LightView.Tests;

internal static class NavigationLatencyProfile
{
    // Diagnostic only: artificial JPEGs, unchanged product semantics. Preview
    // timing is a raw untagged-JPEG experiment, not a production ICC pipeline.
    public static int Run(string fixture, string output)
    {
        fixture = Path.GetFullPath(fixture); output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Preserve prior profile output; select a fresh directory.");
        Directory.CreateDirectory(output); string gallery = Path.Combine(output, "gallery"); Directory.CreateDirectory(gallery);
        string[] paths = Enumerable.Range(0, 18).Select(i => Path.Combine(gallery, $"{i:D4}.jpg")).ToArray();
        foreach (string path in paths) File.Copy(fixture, path);
        var rows = new List<object>();
        static object Summary(IReadOnlyList<double> values)
        {
            double[] sorted = values.OrderBy(value => value).ToArray();
            return new { count = sorted.Length, medianMs = sorted[sorted.Length / 2], p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] };
        }
        foreach (int budgetMiB in new[] { 256, 300, 512 })
        foreach (bool waitForPrefetch in new[] { false, true })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var loader = new WicImageLoader(() => DisplayColorTarget.Standard, budgetMiB * 1024L * 1024);
            var viewer = new ViewerViewModel(loader); var durations = new List<double>(); var commits = new List<double>();
            var render = new RenderTargetBitmap(1000, 650, 96, 96, PixelFormats.Pbgra32);
            var viewport = new LightView.App.Views.ImageViewport { Width = 1000, Height = 650 };
            viewport.Measure(new Size(1000, 650)); viewport.Arrange(new Rect(0, 0, 1000, 650));
            try
            {
                viewer.OpenAsync(paths[0]).GetAwaiter().GetResult(); viewer.FolderReady.GetAwaiter().GetResult();
                for (int i = 1; i <= 16; i++)
                {
                    if (waitForPrefetch) viewer.PrefetchReady.GetAwaiter().GetResult();
                    var watch = Stopwatch.StartNew(); viewer.NavigateAsync(1).GetAwaiter().GetResult();
                    if (viewer.DisplayedImage?.FilePath != paths[i] || viewer.ErrorMessage.Length != 0) throw new IOException("Navigation displayed the wrong file or reported an error.");
                    commits.Add(watch.Elapsed.TotalMilliseconds);
                    viewport.Source = viewer.DisplayedImage.Source; render.Render(viewport);
                    durations.Add(watch.Elapsed.TotalMilliseconds);
                }
                var row = new { budgetMiB, waitForPrefetch, commit = Summary(commits), firstOffscreenRender = Summary(durations),
                    cacheHits = loader.CacheHits, cacheCount = loader.CacheCount, cacheBytes = loader.CacheBytes, durations, commits };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
            }
            finally { viewer.CancelPrefetch(); viewer.PrefetchReady.GetAwaiter().GetResult(); viewport.Source = null; }
        }

        var components = new List<object>();
        foreach (string mode in new[] { "sha256-only", "full-pixels", "preview-1500" })
        {
            var values = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                var watch = Stopwatch.StartNew();
                using var stream = File.Open(fixture, FileMode.Open, FileAccess.Read, FileShare.Read);
                _ = FileSnapshot.Capture(fixture, stream);
                if (mode != "sha256-only")
                {
                    var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
                    source.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
                    if (mode == "preview-1500") source.DecodePixelWidth = 1500;
                    source.StreamSource = stream; source.EndInit(); source.Freeze();
                    var display = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                    int stride = display.PixelWidth * 4; byte[] pixels = new byte[stride * display.PixelHeight];
                    display.CopyPixels(pixels, stride, 0);
                    var detached = BitmapSource.Create(display.PixelWidth, display.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride); detached.Freeze();
                    GC.KeepAlive(detached);
                }
                if (i > 0) values.Add(watch.Elapsed.TotalMilliseconds);
            }
            var component = new { mode, timing = Summary(values), samples = values };
            components.Add(component); Console.WriteLine(JsonSerializer.Serialize(component));
        }
        var result = new { timestamp = DateTimeOffset.UtcNow, productAssembly = typeof(WicImageLoader).Assembly.Location,
            fixture, fixtureSha256 = FrameworkApi.Hex(FrameworkApi.Sha256(File.ReadAllBytes(fixture))),
            method = "Sixteen forward switches through eighteen artificial 24MP JPEG copies, with/without waiting for prefetch. Includes full version SHA256 and 1000x650 offscreen render; not real display latency. Preview component uses raw untagged-JPEG decoding with unchanged full SHA256; no production ICC/preview implementation claimed.",
            navigation = rows, components };
        File.WriteAllText(Path.Combine(output, "profile.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return 0;
    }
}

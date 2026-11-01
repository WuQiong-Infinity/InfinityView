using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.ViewModels;

namespace LightView.Tests;

internal static class PerformanceValidation
{
    public static int AdjacentNavigation(string directory, string output)
    {
        var loader = new WicImageLoader(); var viewer = new ViewerViewModel(loader);
        string[] paths = Directory.GetFiles(directory, "*.jpg").OrderBy(value => value).ToArray();
        viewer.OpenAsync(paths[0]).GetAwaiter().GetResult();
        var samples = new List<double>(); var renderTimes = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            viewer.PrefetchReady.GetAwaiter().GetResult();
            var watch = Stopwatch.StartNew(); viewer.NavigateAsync(i % 2 == 0 ? 1 : -1).GetAwaiter().GetResult();
            var view = new LightView.App.Views.ImageViewport { Source = viewer.DisplayedImage!.Source, Width = 1000, Height = 650 };
            view.Measure(new System.Windows.Size(1000, 650)); view.Arrange(new System.Windows.Rect(0, 0, 1000, 650));
            var frame = new RenderTargetBitmap(1000, 650, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            double load = watch.Elapsed.TotalMilliseconds; frame.Render(view);
            samples.Add(watch.Elapsed.TotalMilliseconds); renderTimes.Add(samples[samples.Count - 1] - load);
        }
        viewer.CancelPrefetch(); viewer.PrefetchReady.GetAwaiter().GetResult();
        double[] sorted = samples.OrderBy(value => value).ToArray();
        var result = new { method = "30 adjacent switches after idle prefetch; includes file-version SHA256 check and 1000x650 offscreen first render, not real screen latency", medianMs = sorted[15], p95Ms = sorted[28], cacheHits = loader.CacheHits, cacheBytes = loader.CacheBytes, samples, renderTimes };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
    }
    public static int FirstRender(string imagePath, string output)
    {
        var results = new List<object>();
        for (int i = 0; i < 8; i++)
        {
            var loader = new WicImageLoader(); var watch = Stopwatch.StartNew();
            var image = loader.Load(imagePath, CancellationToken.None); double load = watch.Elapsed.TotalMilliseconds;
            var viewport = new LightView.App.Views.ImageViewport { Source = image.Source, Width = 1000, Height = 650 };
            viewport.Measure(new System.Windows.Size(1000, 650)); viewport.Arrange(new System.Windows.Rect(0, 0, 1000, 650));
            var frame = new System.Windows.Media.Imaging.RenderTargetBitmap(1000, 650, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            watch.Restart(); frame.Render(viewport); double render = watch.Elapsed.TotalMilliseconds;
            results.Add(new { loadMs = load, firstRenderMs = render, source = image.Source.GetType().Name, format = image.Source.Format.ToString() });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(results)); return 0;
    }
    public static int Run(string largeImage, string gallery, string output, bool caching = false)
    {
        var loader = new WicImageLoader(() => DisplayColorTarget.Standard, caching ? 256L * 1024 * 1024 : 0);
        var decode = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            var watch = Stopwatch.StartNew(); var image = loader.Load(largeImage, CancellationToken.None);
            ForcePixels(image);
            decode.Add(watch.Elapsed.TotalMilliseconds);
            if (image.PixelWidth != 6000 || image.PixelHeight != 4000) throw new IOException("Expected synthetic 24MP fixture");
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long PrivateBytes() { using var process = Process.GetCurrentProcess(); return process.PrivateMemorySize64; }
        long baseline = PrivateBytes();
        var memory = new List<object> { new { iteration = 0, privateBytes = baseline } };
        string[] paths = Directory.GetFiles(gallery, "*.jpg").OrderBy(value => value).ToArray();
        var viewer = new ViewerViewModel(loader); var navigation = new List<double>(); long peak = baseline;
        for (int i = 0; i < 500; i++)
        {
            string path = paths[i % paths.Length]; var watch = Stopwatch.StartNew();
            viewer.OpenAsync(path).GetAwaiter().GetResult(); navigation.Add(watch.Elapsed.TotalMilliseconds);
            if (viewer.DisplayedImage?.FilePath != path) throw new IOException("Navigation benchmark displayed wrong file");
            ForcePixels(viewer.DisplayedImage);
            navigation[navigation.Count - 1] = watch.Elapsed.TotalMilliseconds;
            if ((i + 1) % 50 == 0) { long bytes = PrivateBytes(); peak = Math.Max(peak, bytes); memory.Add(new { iteration = i + 1, privateBytes = bytes }); }
            if ((i + 1) % 100 == 0) Console.WriteLine($"Performance navigation {i + 1}/500");
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long retained = PrivateBytes();
        object Summary(List<double> values)
        { double[] sorted = values.OrderBy(value => value).ToArray(); return new { samples = sorted.Length, medianMs = sorted[sorted.Length / 2], p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] }; }
        var result = new
        {
            timestamp = DateTimeOffset.UtcNow, environment = Environment.Version.ToString(),
            method = "Synthetic fixtures; WIC load/ViewerViewModel commit plus full CopyPixels to force raster materialization, no on-screen first paint. One process. No forced GC within 500 navigation loop; GC before/after for retained comparison.",
            caching, cacheBytes = loader.CacheBytes, cacheCount = loader.CacheCount, cacheHits = loader.CacheHits,
            source24MpBytes = new FileInfo(largeImage).Length, decode30 = Summary(decode), navigation500 = Summary(navigation),
            privateBaseline = baseline, privatePeakSampled = peak, privateRetainedAfterGc = retained, retainedDelta = retained - baseline,
            memory, decodeMs = decode, navigationMs = navigation
        };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { decode30 = Summary(decode), navigation500 = Summary(navigation), baseline, peak, retained, output }));
        return 0;
    }
    private static void ForcePixels(LoadedImage image)
    {
        int stride = checked((image.PixelWidth * image.Source.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[checked(stride * image.PixelHeight)];
        image.Source.CopyPixels(pixels, stride, 0);
        GC.KeepAlive(pixels);
    }
}

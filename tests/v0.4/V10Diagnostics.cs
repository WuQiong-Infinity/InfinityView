using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.ViewModels;
using LightView.Core.Settings;

namespace LightView.Tests;

// No Application, Window, rendering host, Explorer or production data. These
// natural process counters describe this instrumented test host only.
internal static class V10Diagnostics
{
    public static int PathsOnly(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Preserve prior path-check output.");
        Directory.CreateDirectory(output);
        bool passed = false; string? error = null;
        try { Paths(output); passed = true; Console.WriteLine("PASS read-only existence, missing/relative/empty path and stale refresh checks"); }
        catch (Exception failure) { error = failure.ToString(); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            passed, error, method = "Final assembly; path-row behavior only, no Window/Application/Explorer or image loop.",
            productAssembly = typeof(WicImageLoader).Assembly.Location,
            productSha256 = Hash(typeof(WicImageLoader).Assembly.Location), hostSha256 = Hash(typeof(V10Diagnostics).Assembly.Location)
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    public static int Run(string output, string fixture)
    {
        output = Path.GetFullPath(output); fixture = Path.GetFullPath(fixture);
        if (Directory.Exists(output)) throw new IOException("Preserve prior diagnostic output.");
        if (Hash(fixture) != "3249BE49B0E74B42EBEC95F759E9880E454D58ACFB6F12DB182D312DC3A20ABA")
            throw new IOException("Only the fixed artificial 24MP fixture is accepted.");
        Directory.CreateDirectory(output);
        var trials = new List<object>(); int failed = 0;
        void Trial(string name, Action test)
        {
            try { test(); trials.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; trials.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error); }
        }
        Trial("preset checks stay read-only and reject stale refresh results", () => Paths(output));
        string sample = Path.Combine(output, "sample.png"); Save(sample, new PngBitmapEncoder(), 64, 32, false);
        foreach (var (name, test) in CacheTests.Cases(output)) Trial(name, test);
        foreach (var (name, test) in PrefetchTests.Cases(output)) Trial(name, test);
        foreach (var (name, test) in ProgressivePreviewTests.Cases(output).Where(test =>
            test.Name.Contains("preview blocks organizing") || test.Name.Contains("changed file between") ||
            test.Name.Contains("late uncancellable") || test.Name.Contains("full decode failure") ||
            test.Name.Contains("shared JPEG cache"))) Trial(name, test);

        string gallery = Path.Combine(output, "gallery"); Directory.CreateDirectory(gallery);
        string[] images = { Path.Combine(gallery, "01.jpg"), Path.Combine(gallery, "02.png"), Path.Combine(gallery, "03.bmp"), Path.Combine(gallery, "04.jpeg") };
        File.Copy(fixture, images[0]);
        Save(images[1], new PngBitmapEncoder(), 2200, 1400, true);
        Save(images[2], new BmpBitmapEncoder(), 1920, 1080, false);
        var rotated = MetadataCompatibilityTests.Fixture(output, "v10-exif", 6, width: 2400, height: 1600);
        File.Copy(rotated.Broken, images[3]);
        string[] originalHashes = images.Select(Hash).ToArray();
        var rounds = new List<object>(); object? retentionDiagnostic = null;
        Trial("warmup and six identical bidirectional rounds preserve versions, bounds and handles", () =>
        {
            var loader = new WicImageLoader(); var viewer = new ViewerViewModel(loader);
            try
            {
                for (int round = 0; round < 7; round++)
                {
                    viewer.OpenAsync(images[0]).GetAwaiter().GetResult(); viewer.FolderReady.GetAwaiter().GetResult();
                    var samples = new List<object>();
                    foreach (int expected in new[] { 1, 2, 3, 2, 1, 0 })
                    {
                        viewer.PrefetchReady.GetAwaiter().GetResult();
                        string? oldPath = viewer.DisplayedImage?.FilePath;
                        int direction = Array.IndexOf(images, oldPath) < expected ? 1 : -1;
                        var watch = Stopwatch.StartNew(); viewer.NavigateAsync(direction).GetAwaiter().GetResult();
                        var image = viewer.OrganizingImage;
                        Check(viewer.ErrorMessage.Length == 0 && image is not null && image.FilePath == images[expected] &&
                            image.FileVersion?.Sha256.Equals(originalHashes[expected], StringComparison.OrdinalIgnoreCase) == true &&
                            image.Source.IsFrozen && !viewer.IsPreview, "Wrong final image/version or readiness.");
                        Check(loader.CacheBytes <= 256L * 1024 * 1024 && loader.CacheCount > 0, "Cache exceeded actual default budget.");
                        samples.Add(new { expected, elapsedMs = watch.Elapsed.TotalMilliseconds, loader.CacheBytes, loader.CacheCount, loader.CacheHits });
                    }
                    viewer.CancelPrefetch(); viewer.PrefetchReady.GetAwaiter().GetResult();
                    foreach (string path in images) { using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                    rounds.Add(new { round, warmup = round == 0, waitBeforeNavigation = true,
                        samples, tail = Counters(loader) });
                    Console.WriteLine("ROUND " + JsonSerializer.Serialize(rounds[round]));
                }
                // Separate ownership diagnostic after all natural samples. This
                // does not change product GC policy or replace a natural gate.
                var before = Counters(loader);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                retentionDiagnostic = new { method = "Diagnostic-only full collection after natural rounds with current viewer and cache deliberately alive; excluded from natural working-set observations.", before, after = Counters(loader) };
                GC.KeepAlive(viewer); GC.KeepAlive(loader);
            }
            finally { viewer.CancelPrefetch(); viewer.FullReady.GetAwaiter().GetResult(); viewer.PrefetchReady.GetAwaiter().GetResult(); }
            for (int i = 0; i < images.Length; i++) Check(Hash(images[i]) == originalHashes[i], "Artificial source changed.");
        });
        var report = new
        {
            checkedAt = DateTimeOffset.UtcNow, failed, passed = failed == 0, trials,
            method = "No-UI test host. Fixed artificial JPG/PNG/BMP/EXIF set; one warmup plus six identical bidirectional rounds following an earlier four-round growth signal. Prefetch is awaited before foreground in each round; every tail cancels and awaits prefetch. Separate controlled cases cover foreground/prefetch cancellation. No forced GC, working-set trimming or full-frame pixel probe in the loop. The optional ownership diagnostic after all natural rounds is separate.",
            limitation = "Process WS/private bytes and managed heap are instrumented no-UI host observations, not normal EXE stable working set, physical interaction latency, leak proof or a replacement performance gate.",
            fixture, fixtureSha256 = Hash(fixture), productAssembly = typeof(WicImageLoader).Assembly.Location,
            productSha256 = Hash(typeof(WicImageLoader).Assembly.Location), hostSha256 = Hash(typeof(V10Diagnostics).Assembly.Location),
            actualCacheBudgetBytes = 256L * 1024 * 1024,
            images = images.Select(path => new { path, sha256 = Hash(path) }), rounds, retentionDiagnostic
        };
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT {trials.Count - failed}/{trials.Count} diagnostics passed");
        return failed == 0 ? 0 : 1;
    }
    private static object Counters(WicImageLoader loader)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { loader.CacheBytes, loader.CacheCount, loader.CacheHits, workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64, managedHeapBytes = GC.GetTotalMemory(false),
            gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) } };
    }
    private static void Paths(string output)
    {
        string folder = Path.Combine(output, "existing-preset"); Directory.CreateDirectory(folder);
        string missing = Path.Combine(output, "missing-preset");
        var row = new FolderPresetRow(new FolderPreset { Id = "same-id", Path = folder });
        row.RefreshPathStatusAsync().GetAwaiter().GetResult();
        Check(row.PathStatus.Contains("目录存在") && Directory.GetFileSystemEntries(folder).Length == 0, "Read-only existence check wrote content or overstated readiness.");
        row.Path = missing; row.RefreshPathStatusAsync().GetAwaiter().GetResult();
        Check(!Directory.Exists(missing) && row.PathStatus == "不存在或不可访问", "Missing directory was created.");
        row.Path = folder; Task pending = row.RefreshPathStatusAsync(); row.Path = ""; pending.GetAwaiter().GetResult();
        Check(row.PathStatus == "未设置" && row.ToPreset().Id == "same-id" && row.ToPreset().Path == "", "Stale result changed empty path or identity.");
        row.Path = "relative"; row.RefreshPathStatusAsync().GetAwaiter().GetResult();
        Check(row.PathStatus == "需要绝对路径", "Relative preset accepted.");
    }
    private static string Hash(string path) => FrameworkApi.Hex(FrameworkApi.Sha256(File.ReadAllBytes(path)));
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Save(string path, BitmapEncoder encoder, int width, int height, bool alpha)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int p = (y * width + x) * 4; pixels[p] = (byte)(x % 256); pixels[p + 1] = (byte)(y % 256); pixels[p + 2] = 140; pixels[p + 3] = alpha ? (byte)(64 + x % 192) : (byte)255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}

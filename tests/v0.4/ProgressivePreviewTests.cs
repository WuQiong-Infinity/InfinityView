using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Organizing;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.App.Platform;

namespace LightView.Tests;

internal static class ProgressivePreviewTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("JPEG preview is detached with original dimensions and matching full version", () => Decode(root)),
        ("preview handles broken thumbnail rotation and refuses invalid ICC", () => Metadata(root)),
        ("preview size and display generation cache keys cannot reuse wrong pixels", () => Cache(root)),
        ("preview full replacement preserves original-coordinate fit and pointer anchor", Geometry),
        ("preview blocks organizing and explicit full request expedites idle upgrade", () => Readiness(root)),
        ("changed file between preview and full remains unavailable for organizing", () => Changed(root)),
        ("late uncancellable full frame cannot replace a newer image", () => Stale(root)),
        ("full decode failure retains labeled preview and refuses original-pixel readiness", () => Failure(root)),
        ("JPEG preview applies embedded ICC and display conversion consistently", () => Color(root)),
        ("shared JPEG cache bounds LRU and reuses a recent full for preview", () => SharedBudget(root))
    ];

    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private static string Save(string root, string name, bool profile = false)
    {
        string directory = Path.Combine(root, "progressive"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        const int width = 2400, height = 1600;
        byte[] pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 30; pixels[i + 1] = 110; pixels[i + 2] = 190; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(profile ? BitmapFrame.Create(bitmap, null, null,
            new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>(new[] { new ColorContext(PixelFormats.Bgra32) })) : BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream); return path;
    }
    private static void Decode(string root)
    {
        string path = Save(root, "decode.jpg"); var loader = new WicImageLoader();
        var preview = loader.LoadPreview(path, 768, CancellationToken.None);
        Check(preview.IsPreview && preview.PixelWidth == 2400 && preview.PixelHeight == 1600 && preview.Source.PixelWidth == 600, "Incorrect preview size or quality");
        Check(preview.Source.IsFrozen && preview.FileVersion is not null, "Preview not frozen/versioned");
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ReadPixels(preview.Source);
        var full = loader.Load(path, CancellationToken.None);
        Check(!full.IsPreview && full.Source.PixelWidth == 2400 && full.FileVersion!.SameVersion(preview.FileVersion!), "Full image changed file version or dimensions");
        Check(ReferenceEquals(loader.LoadPreview(path, 768, CancellationToken.None).Source, full.Source), "Cached full image was downgraded");
    }
    private static byte[] ReadPixels(BitmapSource source)
    {
        byte[] bytes = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(bytes, source.PixelWidth * 4, 0); return bytes;
    }
    private static void Metadata(string root)
    {
        var paths = MetadataCompatibilityTests.Fixture(root, "preview-metadata", 6, width: 2400, height: 1600);
        var loader = new WicImageLoader(); var preview = loader.LoadPreview(paths.Broken, 768, CancellationToken.None);
        var full = loader.Load(paths.Broken, CancellationToken.None);
        Check(preview.IsPreview && preview.PixelWidth == 1600 && preview.PixelHeight == 2400 && preview.Source.PixelHeight == 600 && full.Source.PixelHeight == 2400, "Preview lost rotation or compatibility decoding");
        var bad = MetadataCompatibilityTests.Fixture(root, "preview-bad-icc", invalidIcc: true, width: 2400, height: 1600);
        bool refused = false;
        try { loader.LoadPreview(bad.Broken, 768, CancellationToken.None); } catch (NotSupportedException) { refused = true; }
        Check(refused, "Invalid declared ICC ignored for preview");
        using var exclusive = new FileStream(bad.Broken, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static void Cache(string root)
    {
        string path = Save(root, "cache.jpg"); var target = DisplayColorTarget.Standard;
        var loader = new WicImageLoader(() => target);
        var small = loader.LoadPreview(path, 512, CancellationToken.None);
        var large = loader.LoadPreview(path, 1024, CancellationToken.None);
        Check(large.Source.PixelWidth == 600 && small.Source.PixelWidth == 300 && !ReferenceEquals(small.Source, large.Source), "Wrong preview size reused");
        target = target with { Key = "preview-new-display" };
        var changed = loader.LoadPreview(path, 512, CancellationToken.None);
        Check(changed.DisplayTargetKey == target.Key && !ReferenceEquals(changed.Source, small.Source), "Old target preview reused");
        DateTime stamp = File.GetLastWriteTimeUtc(path); byte[] bytes = File.ReadAllBytes(path);
        // Harmless trailing byte changes the SHA while restoring timestamps.
        File.WriteAllBytes(path, bytes.Concat(new byte[] { 0 }).ToArray()); File.SetLastWriteTimeUtc(path, stamp);
        var modified = loader.LoadPreview(path, 512, CancellationToken.None);
        Check(modified.FileVersion!.Sha256 != changed.FileVersion!.Sha256, "Preview accepted stale file version");
    }
    private static LoadedImage Artificial(string path, bool preview, FileSnapshot version)
    {
        int width = preview ? 60 : 600, height = preview ? 40 : 400;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, new byte[width * height * 4], width * 4); source.Freeze();
        return new(path, source, "Test", 0, FileVersion: version, IsPreview: preview, OriginalWidth: 600, OriginalHeight: 400);
    }
    private static void Geometry()
    {
        var version = new FileSnapshot("test.jpg", "id", 1, 1, "hash");
        var viewport = new ImageViewport { Width = 300, Height = 200, Image = Artificial("test.jpg", true, version) };
        viewport.Measure(new Size(300, 200)); viewport.Arrange(new Rect(0, 0, 300, 200));
        viewport.Transform.Resize(300, 200, 1);
        Check(viewport.Transform.ImageWidth == 600 && Math.Abs(viewport.Transform.Zoom - .5) < .00001, "Fit used preview pixels as original dimensions");
        viewport.ActualPixelsAt(new Point(80, 70)); viewport.Transform.Pan(17, -9);
        double x = viewport.Transform.OffsetX, y = viewport.Transform.OffsetY;
        viewport.Image = Artificial("test.jpg", false, version);
        Check(!viewport.Transform.IsFit && viewport.Transform.Zoom == 1 && Math.Abs(x - viewport.Transform.OffsetX) < .00001 && Math.Abs(y - viewport.Transform.OffsetY) < .00001, "Quality replacement changed original-pixel pointer anchor");
    }
    private sealed class Controlled(string path, bool changed = false, bool fail = false) : IProgressiveImageLoader
    {
        public readonly ManualResetEventSlim FullEntered = new();
        public readonly ManualResetEventSlim Release = new();
        public LoadedImage LoadPreview(string input, int edge, CancellationToken token) => Artificial(input, true, Version(input));
        public bool TryPrefetchPreview(string input, int edge, CancellationToken token) => true;
        private FileSnapshot Version(string input) => new(input, "id", 1, 1, "before");
        public LoadedImage Load(string input, CancellationToken token)
        {
            if (input == path)
            {
                FullEntered.Set(); if (!Release.Wait(5000)) throw new IOException("Controlled test timed out");
                if (fail) throw new IOException("Full frame failed");
            }
            return Artificial(input, false, Version(input) with { Sha256 = input == path && changed ? "after" : "before" });
        }
    }
    private static (ViewerViewModel Viewer, Controlled Loader, Task Open) Begin(string root, string name, bool changed = false, bool fail = false)
    {
        string path = Save(root, name); var loader = new Controlled(path, changed, fail); var viewer = new ViewerViewModel(loader);
        Task open = viewer.OpenAsync(path);
        Check(SpinWait.SpinUntil(() => viewer.IsPreview, 5000), "Preview not published");
        Check(viewer.OrganizingImage is null && viewer.IsLoading && viewer.LoadStatus.Contains("预览"), "Preview could be organized");
        return (viewer, loader, open);
    }
    private static void Readiness(string root)
    {
        var (viewer, loader, open) = Begin(root, "readiness.jpg");
        Task<bool> ready = viewer.EnsureFullAsync(); Check(loader.FullEntered.Wait(5000), "Full request did not begin");
        Check(!ready.IsCompleted && viewer.OrganizingImage is null, "Original pixels or organizing claimed before full frame");
        loader.Release.Set(); open.GetAwaiter().GetResult();
        Check(ready.GetAwaiter().GetResult() && viewer.OrganizingImage is { IsPreview: false }, "Full frame did not enable organizing");
    }
    private static void Changed(string root)
    {
        var (viewer, loader, open) = Begin(root, "changed.jpg", changed: true); loader.Release.Set(); open.GetAwaiter().GetResult();
        Check(viewer.IsPreview && viewer.OrganizingImage is null && viewer.ErrorMessage.Contains("变化") && !viewer.EnsureFullAsync().GetAwaiter().GetResult(), "Changed full version enabled organizing");
    }
    private static void Stale(string root)
    {
        var (viewer, loader, open) = Begin(root, "stale-old.jpg");
        Task<bool> readiness = viewer.EnsureFullAsync(); Check(loader.FullEntered.Wait(5000), "Old full request not held");
        string next = Save(root, "stale-new.jpg"); viewer.OpenAsync(next).GetAwaiter().GetResult();
        loader.Release.Set(); open.GetAwaiter().GetResult();
        Check(viewer.DisplayedImage!.FilePath == next && !readiness.GetAwaiter().GetResult(), "Late full or pixel command affected newer file");
    }
    private static void Failure(string root)
    {
        var (viewer, loader, open) = Begin(root, "failure.jpg", fail: true); loader.Release.Set(); open.GetAwaiter().GetResult();
        Check(viewer.IsPreview && !viewer.IsLoading && viewer.OrganizingImage is null && !viewer.EnsureFullAsync().GetAwaiter().GetResult() && viewer.ErrorMessage.Length > 0, "Failed full frame claimed safe readiness");
    }
    private static void Color(string root)
    {
        string path = Save(root, "color.jpg", true);
        // Nonstandard target exercises the same LittleCMS transform for both resolutions.
        byte[] p3 = LittleCmsTransform.ProfileForPng(new(false, null, [.3127, .3290, .68, .32, .265, .69, .15, .06]));
        var target = DisplayColorTarget.FromProfile(p3, "人工P3显示目标");
        var loader = new WicImageLoader(() => target);
        var preview = loader.LoadPreview(path, 768, CancellationToken.None); var full = loader.Load(path, CancellationToken.None);
        byte[] a = ReadPixels(preview.Source), b = ReadPixels(full.Source);
        Check(preview.EmbeddedProfileCount > 0 && preview.ColorDescription!.Contains("嵌入 ICC"), "Preview skipped embedded source profile");
        Check(a.AsSpan(0, 4).SequenceEqual(b.AsSpan(0, 4)), "Solid color differs between preview/full ICC conversion");
        byte[] unconverted = ReadPixels(new WicImageLoader().Load(path, CancellationToken.None).Source);
        Check(!a.AsSpan(0, 3).SequenceEqual(unconverted.AsSpan(0, 3)), "Test target did not exercise a color conversion");
    }
    private static void SharedBudget(string root)
    {
        var loader = new WicImageLoader(() => DisplayColorTarget.Standard, 24 * 1024 * 1024);
        string first = Save(root, "budget1.jpg"), second = Save(root, "budget2.jpg");
        loader.LoadPreview(first, 768, CancellationToken.None);
        loader.Load(first, CancellationToken.None); var full = loader.Load(second, CancellationToken.None);
        int hits = loader.CacheHits;
        var again = loader.LoadPreview(second, 768, CancellationToken.None);
        Check(!again.IsPreview && ReferenceEquals(again.Source, full.Source) && loader.CacheHits > hits && loader.CacheBytes <= 24 * 1024 * 1024, "Recent full was not reused for preview or LRU budget exceeded");
    }
}

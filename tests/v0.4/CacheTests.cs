using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;

namespace LightView.Tests;

internal static class CacheTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("cached image hit remains frozen and releases the source handle", () => Hit(root)),
        ("same path modified with restored timestamp cannot reuse cached pixels", () => Changed(root)),
        ("display target generation changes invalidate cached presentation", () => Target(root)),
        ("bounded image cache evicts old entries and releases cache for oversized image", () => Budget(root))
    ];
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static string Save(string directory, string name, byte channel = 30, int width = 64, int height = 32)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name);
        byte[] data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = channel; data[i + 1] = 100; data[i + 2] = 200; data[i + 3] = 255; }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var file = File.Create(path); encoder.Save(file); return path;
    }
    private static void Hit(string root)
    {
        string path = Save(Path.Combine(root, "cache-hit"), "image.png"); var loader = new WicImageLoader();
        var first = loader.Load(path, CancellationToken.None); var second = loader.Load(path, CancellationToken.None);
        Check(loader.CacheHits == 1 && ReferenceEquals(first.Source, second.Source) && second.Source.IsFrozen, "Cache missed or mutable image");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static void Changed(string root)
    {
        string folder = Path.Combine(root, "cache-changed"), path = Save(folder, "image.png"); var loader = new WicImageLoader();
        var first = loader.Load(path, CancellationToken.None); DateTime time = File.GetLastWriteTimeUtc(path);
        Save(folder, "image.png", 240); File.SetLastWriteTimeUtc(path, time); var second = loader.Load(path, CancellationToken.None);
        Check(loader.CacheHits == 0 && !ReferenceEquals(first.Source, second.Source) && first.FileVersion!.Sha256 != second.FileVersion!.Sha256, "Stale cached picture accepted");
    }
    private static void Target(string root)
    {
        string path = Save(Path.Combine(root, "cache-target"), "image.png"); var target = DisplayColorTarget.Standard;
        var loader = new WicImageLoader(() => target); var first = loader.Load(path, CancellationToken.None);
        target = target with { Key = "new-display-generation" }; var second = loader.Load(path, CancellationToken.None);
        Check(loader.CacheHits == 0 && !ReferenceEquals(first.Source, second.Source), "Display change reused old target pixels");
    }
    private static void Budget(string root)
    {
        string folder = Path.Combine(root, "cache-budget"); string one = Save(folder, "1.png"), two = Save(folder, "2.png"), three = Save(folder, "3.png");
        var loader = new WicImageLoader(() => DisplayColorTarget.Standard, 20000);
        loader.Load(one, CancellationToken.None); loader.Load(two, CancellationToken.None); loader.Load(one, CancellationToken.None);
        Check(loader.CacheHits == 1, "Recent cache entry not found"); loader.Load(three, CancellationToken.None); loader.Load(two, CancellationToken.None);
        Check(loader.CacheHits == 1 && loader.CacheCount == 2 && loader.CacheBytes <= 20000, "Cache unbounded or wrong LRU eviction");
        var small = new WicImageLoader(() => DisplayColorTarget.Standard, 5000);
        small.Load(Save(folder, "small.png", width: 8, height: 8), CancellationToken.None); Check(small.CacheCount == 1, "Small item not cached");
        small.Load(one, CancellationToken.None); Check(small.CacheCount == 0 && small.CacheBytes == 0, "Oversized current image retained other cache items");
    }
}

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.ViewModels;

namespace LightView.Tests;

internal static class PrefetchTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("only immediate neighbors prefetch without changing displayed version", () => Neighbors(root)),
        ("stale prefetch cancels without replacing a newer image", () => CancelStale(root)),
        ("bad neighbors do not replace current view or its error status", () => BadNeighbor(root)),
        ("oversized neighbor is not prefetched or allowed to clear useful cache", () => Budget(root))
    ];
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string Gallery(string root, string name)
    {
        string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
        foreach (string file in new[] { "1.png", "2.png", "3.png", "4.png" }) File.Copy(Path.Combine(root, "sample.png"), Path.Combine(directory, file));
        return directory;
    }
    private static void Neighbors(string root)
    {
        string directory = Gallery(root, "prefetch-neighbors"); var loader = new WicImageLoader(); var viewer = new ViewerViewModel(loader);
        viewer.OpenAsync(Path.Combine(directory, "2.png")).GetAwaiter().GetResult(); var displayed = viewer.OrganizingImage;
        viewer.PrefetchReady.GetAwaiter().GetResult();
        Check(ReferenceEquals(displayed, viewer.OrganizingImage) && !viewer.IsLoading && loader.CacheCount == 3, "Prefetch changed view/loading or fetched beyond neighbors");
        int hits = loader.CacheHits; viewer.NavigateAsync(1).GetAwaiter().GetResult(); viewer.CancelPrefetch();
        Check(loader.CacheHits == hits + 1 && Path.GetFileName(viewer.DisplayedImage!.FilePath) == "3.png", "Next image was not prefetched");
        using var exclusive = new FileStream(Path.Combine(directory, "3.png"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private static void CancelStale(string root)
    {
        string directory = Gallery(root, "prefetch-stale"); var loader = new ControlledPrefetchLoader(); var viewer = new ViewerViewModel(loader);
        viewer.OpenAsync(Path.Combine(directory, "1.png")).GetAwaiter().GetResult(); Task prior = viewer.PrefetchReady;
        Check(loader.Entered.Wait(5000), "Prefetch did not begin");
        viewer.OpenAsync(Path.Combine(directory, "4.png")).GetAwaiter().GetResult(); viewer.CancelPrefetch(); prior.GetAwaiter().GetResult();
        Check(loader.Cancelled && Path.GetFileName(viewer.DisplayedImage!.FilePath) == "4.png" && viewer.ErrorMessage == "", "Stale prefetch affected new view");
    }
    private static void BadNeighbor(string root)
    {
        string directory = Gallery(root, "prefetch-bad"); File.WriteAllText(Path.Combine(directory, "2.png"), "bad image");
        var viewer = new ViewerViewModel(new WicImageLoader()); viewer.OpenAsync(Path.Combine(directory, "1.png")).GetAwaiter().GetResult();
        viewer.PrefetchReady.GetAwaiter().GetResult();
        Check(Path.GetFileName(viewer.DisplayedImage!.FilePath) == "1.png" && viewer.ErrorMessage == "" && !viewer.IsLoading, "Bad prefetch leaked into real view status");
    }
    private static void Budget(string root)
    {
        string directory = Gallery(root, "prefetch-budget"); var loader = new WicImageLoader(() => DisplayColorTarget.Standard, 5000);
        Check(!loader.TryPrefetch(Path.Combine(directory, "1.png"), CancellationToken.None) && loader.CacheCount == 0, "Oversize prefetch entered cache");
    }
    private sealed class ControlledPrefetchLoader : IImageLoader, IImagePrefetcher
    {
        public ManualResetEventSlim Entered { get; } = new();
        public bool Cancelled;
        public LoadedImage Load(string path, CancellationToken token)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4); image.Freeze();
            return new(path, image, "Test", 0);
        }
        public bool TryPrefetch(string path, CancellationToken token)
        {
            Entered.Set();
            if (!token.WaitHandle.WaitOne(5000)) throw new IOException("Test timeout");
            Cancelled = true; token.ThrowIfCancellationRequested(); return false;
        }
    }
}

using System.IO;
using LightView.App.Platform;
using LightView.App.Imaging;
using LightView.App.ViewModels;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightView.Tests;

internal static class FolderTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("folder uses natural order and restricts file formats", () => NaturalOrder(root)),
        ("navigation does not wrap and skips deleted neighbors", () => DeletedNeighbor(root)),
        ("rapid next keys follow requested order and discard late image", () => RapidNavigation(root))
    ];

    private static void NaturalOrder(string root)
    {
        string directory = Path.Combine(root, "natural-order");
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "photo10.png", "photo2.JPG", "photo1.jpeg", "unsupported.gif" })
            File.WriteAllBytes(Path.Combine(directory, name), []);
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllBytes(Path.Combine(directory, "nested", "hidden.png"), []);
        var folder = ImageFolder.Read(directory);
        string[] expected = ["photo1.jpeg", "photo2.JPG", "photo10.png"];
        if (!folder.Files.Select(Path.GetFileName).SequenceEqual(expected))
            throw new InvalidOperationException("Wrong natural order, formats or recursion.");
    }

    private static void DeletedNeighbor(string root)
    {
        string directory = Path.Combine(root, "neighbors");
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "1.png", "2.png", "3.png" })
            File.WriteAllBytes(Path.Combine(directory, name), []);
        var folder = ImageFolder.Read(directory);
        string one = Path.Combine(directory, "1.png"), three = Path.Combine(directory, "3.png");
        File.Delete(Path.Combine(directory, "2.png"));
        if (folder.Neighbor(one, 1) != three || folder.Neighbor(one, -1) is not null || folder.Neighbor(three, 1) is not null)
            throw new InvalidOperationException("Navigation wrapped or selected a missing file.");
    }

    private static void RapidNavigation(string root)
    {
        string directory = Path.Combine(root, "rapid-navigation");
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "1.png", "2.png", "3.png" })
            File.WriteAllBytes(Path.Combine(directory, name), []);
        var loader = new NavigationLoader();
        var viewer = new ViewerViewModel(loader);
        viewer.OpenAsync(Path.Combine(directory, "1.png")).GetAwaiter().GetResult();
        viewer.FolderReady.GetAwaiter().GetResult();
        Task firstNext = viewer.NavigateAsync(1);
        if (!loader.SecondStarted.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Second image did not start.");
        Task secondNext = viewer.NavigateAsync(1);
        secondNext.GetAwaiter().GetResult();
        loader.ReleaseSecond.Set();
        firstNext.GetAwaiter().GetResult();
        if (viewer.DisplayedImage?.FilePath != Path.Combine(directory, "3.png"))
            throw new InvalidOperationException("Rapid navigation displayed the wrong file.");
    }

    private sealed class NavigationLoader : IImageLoader
    {
        public ManualResetEventSlim SecondStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseSecond { get; } = new(false);
        public LoadedImage Load(string path, CancellationToken cancellationToken)
        {
            if (Path.GetFileName(path) == "2.png")
            {
                SecondStarted.Set();
                if (!ReleaseSecond.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Second image not released.");
            }
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
            bitmap.Freeze();
            return new LoadedImage(path, bitmap, "Test", 0);
        }
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Views;

namespace LightView.Tests;

internal static class ViewportReloadTests
{
    public static (string Name, Action Test)[] Cases =>
    [
        ("same-file color reload preserves manual zoom", SameFile),
        ("different file resets view even with matching dimensions", DifferentFile)
    ];
    private static LoadedImage Picture(string path)
    {
        var bitmap = BitmapSource.Create(800, 400, 96, 96, PixelFormats.Bgra32, null, new byte[800 * 400 * 4], 800 * 4);
        bitmap.Freeze(); return new(path, bitmap, "Test", 0);
    }
    private static ImageViewport Viewport()
    {
        var viewport = new ImageViewport { Image = Picture("photo-a.png"), Width = 400, Height = 200 };
        viewport.Measure(new Size(400, 200)); viewport.Arrange(new Rect(0, 0, 400, 200));
        viewport.ActualPixels(); return viewport;
    }
    private static void SameFile()
    {
        var viewport = Viewport(); viewport.Image = Picture("photo-a.png");
        if (viewport.Transform.IsFit || viewport.Transform.Zoom != 1)
            throw new InvalidOperationException("Color reload lost the manual viewport.");
    }
    private static void DifferentFile()
    {
        var viewport = Viewport(); viewport.Image = Picture("photo-b.png");
        if (!viewport.Transform.IsFit || Math.Abs(viewport.Transform.Zoom - 0.5) > 0.000001)
            throw new InvalidOperationException("New file retained previous zoom.");
    }
}

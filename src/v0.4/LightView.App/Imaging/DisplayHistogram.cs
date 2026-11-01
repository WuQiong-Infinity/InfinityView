using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightView.App.Imaging;

internal sealed record DisplayHistogram(long[] Red, long[] Green, long[] Blue, long[] Lightness, long Pixels);

internal static class HistogramCalculator
{
    internal static DisplayHistogram Calculate(BitmapSource source, CancellationToken cancellation)
    {
        if (source.Format != PixelFormats.Pbgra32) throw new NotSupportedException("直方图需要显示图像像素。");
        int stride = checked(source.PixelWidth * 4);
        int rows = Math.Min(source.PixelHeight, Math.Max(1, 1024 * 1024 / stride));
        byte[] bytes = new byte[checked(stride * rows)];
        long[] red = new long[256], green = new long[256], blue = new long[256], lightness = new long[256]; long pixels = 0;
        for (int y = 0; y < source.PixelHeight; y += rows)
        {
            cancellation.ThrowIfCancellationRequested(); int count = Math.Min(rows, source.PixelHeight - y);
            source.CopyPixels(new Int32Rect(0, y, source.PixelWidth, count), bytes, stride, 0);
            int end = checked(stride * count);
            for (int at = 0; at < end; at += 4)
            {
                int alpha = bytes[at + 3]; if (alpha == 0) continue;
                static int Straight(int value, int alpha) => alpha == 255 ? value : Math.Min(255, (value * 255 + alpha / 2) / alpha);
                int b = Straight(bytes[at], alpha), g = Straight(bytes[at + 1], alpha), r = Straight(bytes[at + 2], alpha);
                blue[b]++; green[g]++; red[r]++;
                // Display RGB weighted luma; not linear luminance or CIE L*.
                lightness[(2126 * r + 7152 * g + 722 * b + 5000) / 10000]++; pixels++;
            }
        }
        cancellation.ThrowIfCancellationRequested(); return new(red, green, blue, lightness, pixels);
    }
}

// Identity numbers originate in the weak Source table. Values contain only counts.
internal sealed class HistogramCache
{
    internal readonly record struct Key(long SourceId, string? Target, bool Preview);
    private readonly LinkedList<(Key Key, DisplayHistogram Value)> lru = new();
    internal int Count => lru.Count;
    internal bool TryGet(Key key, out DisplayHistogram? value)
    {
        var node = lru.First;
        while (node is not null)
        {
            if (node.Value.Key == key) { value = node.Value.Value; lru.Remove(node); lru.AddFirst(node); return true; }
            node = node.Next;
        }
        value = null; return false;
    }
    internal void Add(Key key, DisplayHistogram value)
    {
        var existing = lru.First;
        while (existing is not null) { var next = existing.Next; if (existing.Value.Key == key) lru.Remove(existing); existing = next; }
        lru.AddFirst((key, value)); while (lru.Count > 5) lru.RemoveLast();
    }
}

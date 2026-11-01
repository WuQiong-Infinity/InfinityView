using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Platform;

namespace LightView.App.Imaging;

// One independent final surface and bounded stripe buffers, including orientation.
internal static class StripeBitmapWriter
{
    internal const int InputStripeBytes = 1024 * 1024;
    // Input, LCMS output, WPF premultiplication surfaces, mapping and managed buffers.
    internal static long ScratchEstimate(int width) => checked(12L * Math.Max(InputStripeBytes, (long)width * 8));
    internal static bool HighPrecision(BitmapSource input) => input.Format.BitsPerPixel > 32 || input.Format == PixelFormats.Gray16 ||
        input.Format == PixelFormats.Gray32Float || input.Format == PixelFormats.Bgr101010;

    internal static BitmapSource Convert(BitmapSource input, ushort orientation, LittleCmsTransform.Session? color,
        bool highPrecision, CancellationToken cancellation)
    {
        int width = input.PixelWidth, height = input.PixelHeight;
        bool swapped = orientation is >= 5 and <= 8;
        var output = new WriteableBitmap(swapped ? height : width, swapped ? width : height, 96, 96, PixelFormats.Pbgra32, null);
        int inputStride = checked(width * (color is null ? 4 : highPrecision ? 8 : 4));
        int outputStride = checked(width * 4);
        int rows = Math.Min(height, Math.Max(1, InputStripeBytes / inputStride));
        var read = new FormatConvertedBitmap(input, color is null ? PixelFormats.Pbgra32 : highPrecision ? PixelFormats.Rgba64 : PixelFormats.Bgra32, null, 0);
        if (color is null && orientation is < 2 or > 8)
        {
            output.Lock();
            try
            {
                int stride = output.BackBufferStride;
                for (int y = 0; y < height; y += rows)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = Math.Min(rows, height - y);
                    int offset = checked(y * stride), size = checked((count - 1) * stride + outputStride);
                    read.CopyPixels(new Int32Rect(0, y, width, count), IntPtr.Add(output.BackBuffer, offset), size, stride);
                }
                output.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally { output.Unlock(); }
            cancellation.ThrowIfCancellationRequested(); output.Freeze(); return output;
        }
        byte[] sourceBytes = new byte[checked(inputStride * rows)];
        byte[] premultiplied = color is null ? sourceBytes : new byte[checked(outputStride * rows)];
        byte[] bgra = color is null ? [] : new byte[checked(outputStride * rows)];
        byte[] mapped = orientation is >= 2 and <= 8 ? new byte[checked(outputStride * rows)] : [];
        uint[] sourcePixels = orientation is 2 or 3 or >= 5 and <= 8 ? new uint[checked(width * rows)] : [];
        uint[] mappedPixels = sourcePixels.Length > 0 ? new uint[sourcePixels.Length] : [];
        WriteableBitmap? stripe = color is null ? null : new(width, rows, 96, 96, PixelFormats.Bgra32, null);
        var premultiply = stripe is null ? null : new FormatConvertedBitmap(stripe, PixelFormats.Pbgra32, null, 0);
        for (int y = 0; y < height; y += rows)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = Math.Min(rows, height - y);
            read.CopyPixels(new Int32Rect(0, y, width, count), sourceBytes, inputStride, 0);
            if (color is not null)
            {
                color.Convert(sourceBytes, bgra, checked(width * count));
                // Reuse a small WPF conversion surface so rounding matches the old pipeline.
                stripe!.WritePixels(new Int32Rect(0, 0, width, count), bgra, outputStride, 0);
                premultiply!.CopyPixels(new Int32Rect(0, 0, width, count), premultiplied, outputStride, 0);
            }
            if (orientation is < 2 or > 8)
                output.WritePixels(new Int32Rect(0, y, width, count), premultiplied, outputStride, 0);
            else
            {
                Int32Rect rect = orientation switch
                {
                    2 => new(0, y, width, count),
                    3 or 4 => new(0, height - y - count, width, count),
                    5 or 8 => new(y, 0, count, width),
                    _ => new(height - y - count, 0, count, width)
                };
                int activeBytes = checked(outputStride * count);
                if (orientation == 4)
                {
                    for (int sy = 0; sy < count; sy++)
                        Buffer.BlockCopy(premultiplied, sy * outputStride, mapped, (count - 1 - sy) * outputStride, outputStride);
                }
                else
                {
                    Buffer.BlockCopy(premultiplied, 0, sourcePixels, 0, activeBytes);
                    if (orientation is 2 or 3)
                    {
                        for (int sy = 0; sy < count; sy++)
                        {
                            int from = sy * width + width - 1, to = (orientation == 3 ? count - 1 - sy : sy) * width;
                            for (int x = 0; x < width; x++) mappedPixels[to++] = sourcePixels[from--];
                        }
                    }
                    else
                    {
                        // Every checked stripe contains exactly width*count pixels;
                        // precomputed starts/steps keep all inner offsets in that range.
                        int step = orientation is 7 or 8 ? -count : count;
                        int start = step < 0 ? checked((width - 1) * count) : 0;
                        bool reverseColumn = orientation is 6 or 7;
                        for (int sy = 0; sy < count; sy++)
                        {
                            int from = sy * width, to = start + (reverseColumn ? count - 1 - sy : sy);
                            for (int x = 0; x < width; x++, to += step) mappedPixels[to] = sourcePixels[from++];
                        }
                    }
                    Buffer.BlockCopy(mappedPixels, 0, mapped, 0, activeBytes);
                }
                output.WritePixels(rect, mapped, checked(rect.Width * 4), 0);
            }
        }
        cancellation.ThrowIfCancellationRequested(); output.Freeze(); return output;
    }
}

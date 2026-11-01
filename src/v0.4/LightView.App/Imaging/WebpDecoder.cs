using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightView.App.Imaging;

// Official libwebp 1.6.0, BSD; see native/libwebp/source.json and COPYING.
internal static class WebpDecoder
{
    internal sealed record Header(int Width, int Height, byte[]? Profile, ExifReadResult Exif, ushort Orientation, bool Animated);
    internal static Header Inspect(byte[] bytes)
    {
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) + 8L != bytes.Length) throw new FileFormatException("WebP 文件头或长度无效。");
        byte[]? profile = null, exif = null; bool animated = false;
        for (long offset = 12; offset < bytes.Length;)
        {
            if (offset + 8 > bytes.Length) throw new FileFormatException("WebP 分段头无效。");
            var id = bytes.AsSpan((int)offset, 4); uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset + 4));
            long next = offset + 8 + size + (size & 1);
            if (next > bytes.Length) throw new FileFormatException("WebP 分段长度无效。");
            if (id.SequenceEqual("ICCP"u8) || id.SequenceEqual("EXIF"u8))
            {
                if (size > 1024 * 1024) throw new NotSupportedException("WebP 色彩或拍摄信息超过 1 MiB 限制。");
                byte[] value = bytes.AsSpan((int)offset + 8, (int)size).ToArray();
                if (id.SequenceEqual("ICCP"u8)) { if (profile is not null || value.Length == 0) throw new FileFormatException("WebP ICC 无效。"); profile = value; }
                else exif = value;
            }
            if (id.SequenceEqual("VP8X"u8) && size >= 1)
            {
                byte flags = bytes[(int)offset + 8]; animated = (flags & 2) != 0;
                if ((flags & 0x20) != 0 && profile is null) { /* Verify declaration after all chunks. */ }
            }
            offset = next;
        }
        if (bytes.Length >= 30 && bytes.AsSpan(12, 4).SequenceEqual("VP8X"u8) && (bytes[20] & 0x20) != 0 && profile is null)
            throw new FileFormatException("WebP 声明了 ICC，但配置缺失。");
        if (WebPGetInfo(bytes, (UIntPtr)(uint)bytes.Length, out int width, out int height) == 0) throw new FileFormatException("无法读取 WebP 像素信息。");
        if (exif is { Length: >= 6 } && exif.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8)) exif = exif.AsSpan(6).ToArray();
        return new(width, height, profile, exif is null ? new([], null) : ExifReader.ParseTiff(exif), Orientation(exif), animated);
    }
    private static ushort Orientation(byte[]? data)
    {
        if (data is not { Length: >= 8 }) return 1;
        bool little = data[0] == 'I' && data[1] == 'I';
        if (!little && !(data[0] == 'M' && data[1] == 'M')) return 1;
        ushort U16(int p) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
        uint U32(int p) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
        if (U16(2) != 42) return 1;
        long offset = U32(4); if (offset < 8 || offset + 2 > data.Length) return 1;
        int count = U16((int)offset);
        for (long p = offset + 2, i = 0; i < count && p + 12 <= data.Length; ++i, p += 12)
            if (U16((int)p) == 274 && U16((int)p + 2) == 3 && U32((int)p + 4) == 1)
            { ushort value = U16((int)p + 8); return value is >= 1 and <= 8 ? value : (ushort)1; }
        return 1;
    }
    internal static BitmapSource Decode(byte[] bytes, Header header)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned); IntPtr decoder = IntPtr.Zero;
        try
        {
            var data = new NativeData { Bytes = pin.AddrOfPinnedObject(), Size = (UIntPtr)(uint)bytes.Length };
            var options = new Options { Padding = new uint[7] };
            if (WebPAnimDecoderOptionsInitInternal(ref options, 0x0107) == 0) throw new NotSupportedException("WebP 解码器版本不兼容。");
            options.ColorMode = 3; // Straight BGRA; the existing stripe pipeline premultiplies after color conversion.
            decoder = WebPAnimDecoderNewInternal(ref data, ref options, 0x0107);
            if (decoder == IntPtr.Zero || WebPAnimDecoderGetInfo(decoder, out var info) == 0 ||
                info.Width != header.Width || info.Height != header.Height || WebPAnimDecoderGetNext(decoder, out var pixels, out _) == 0)
                throw new FileFormatException("WebP 图像内容损坏或无法解码。");
            int stride = checked(header.Width * 4);
            var bitmap = BitmapSource.Create(header.Width, header.Height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(stride * header.Height), stride);
            bitmap.Freeze(); return bitmap;
        }
        finally { if (decoder != IntPtr.Zero) WebPAnimDecoderDelete(decoder); pin.Free(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeData { public IntPtr Bytes; public UIntPtr Size; }
    [StructLayout(LayoutKind.Sequential)] private struct Options { public int ColorMode, Threads; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public uint[] Padding; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public uint Width, Height, Loop, Background, Frames; private uint p0, p1, p2, p3; }
    [DllImport("ivwebp.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int WebPGetInfo(byte[] bytes, UIntPtr size, out int width, out int height);
    [DllImport("ivwebp.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int WebPAnimDecoderOptionsInitInternal(ref Options options, int version);
    [DllImport("ivwebp.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr WebPAnimDecoderNewInternal(ref NativeData data, ref Options options, int version);
    [DllImport("ivwebp.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int WebPAnimDecoderGetInfo(IntPtr decoder, out Info info);
    [DllImport("ivwebp.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int WebPAnimDecoderGetNext(IntPtr decoder, out IntPtr pixels, out int timestamp);
    [DllImport("ivwebp.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void WebPAnimDecoderDelete(IntPtr decoder);
}

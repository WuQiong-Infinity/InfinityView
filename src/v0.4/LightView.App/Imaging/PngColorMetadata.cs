using System.Buffers.Binary;
using System.IO;

namespace LightView.App.Imaging;

internal sealed record PngColorMetadata(bool Srgb, double? FileGamma, double[]? Chromaticities)
{
    public static PngColorMetadata Empty { get; } = new(false, null, null);
    public bool NeedsProfile => !Srgb && (FileGamma.HasValue || Chromaticities is not null);

    public static PngColorMetadata Read(Stream stream)
    {
        long position = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != 8 || !signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return Empty;
            bool srgb = false;
            double? gamma = null;
            double[]? chroma = null;
            Span<byte> header = stackalloc byte[8];
            Span<byte> data = stackalloc byte[32];
            while (stream.Position + 12 <= stream.Length)
            {
                stream.ReadExactly(header);
                uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (size > stream.Length - stream.Position - 4) throw new FileFormatException("PNG 元数据长度无效。");
                long end = stream.Position + size + 4;
                if (header.Slice(4).SequenceEqual("IDAT"u8) || header.Slice(4).SequenceEqual("IEND"u8)) break;
                if (header.Slice(4).SequenceEqual("gAMA"u8))
                {
                    if (size != 4 || gamma.HasValue) throw new FileFormatException("PNG gAMA 元数据无效或重复。");
                    stream.ReadExactly(data.Slice(0, 4));
                    uint value = BinaryPrimitives.ReadUInt32BigEndian(data);
                    if (value == 0) throw new FileFormatException("PNG gAMA 值不能为零。");
                    gamma = value / 100000.0;
                }
                else if (header.Slice(4).SequenceEqual("sRGB"u8))
                {
                    if (size != 1 || srgb || stream.ReadByte() is < 0 or > 3) throw new FileFormatException("PNG sRGB 元数据无效。");
                    srgb = true;
                }
                else if (header.Slice(4).SequenceEqual("cHRM"u8))
                {
                    if (size != 32 || chroma is not null) throw new FileFormatException("PNG cHRM 元数据无效或重复。");
                    stream.ReadExactly(data);
                    chroma = new double[8];
                    for (int i = 0; i < 8; i++) chroma[i] = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i * 4)) / 100000.0;
                    for (int i = 0; i < 8; i += 2)
                        if (chroma[i] < 0 || chroma[i + 1] <= 0 || chroma[i] + chroma[i + 1] > 1.00001)
                            throw new NotSupportedException("暂不支持此 PNG 色度坐标。");
                }
                stream.Position = end;
            }
            return new(srgb, gamma, chroma);
        }
        finally { stream.Position = position; }
    }
}

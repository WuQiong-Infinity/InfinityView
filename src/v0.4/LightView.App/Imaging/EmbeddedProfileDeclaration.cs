using System.Buffers.Binary;
using System.IO;

namespace LightView.App.Imaging;

internal static class EmbeddedProfileDeclaration
{
    public static bool IsPresent(Stream stream)
    {
        long position = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != signature.Length) return false;
            if (signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                Span<byte> header = stackalloc byte[8];
                while (stream.Position + 12 <= stream.Length)
                {
                    stream.ReadExactly(header);
                    uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (size > stream.Length - stream.Position - 4)
                        throw new FileFormatException("PNG 元数据长度无效。");
                    if (header.Slice(4).SequenceEqual("iCCP"u8)) return true;
                    if (header.Slice(4).SequenceEqual("IDAT"u8) || header.Slice(4).SequenceEqual("IEND"u8)) return false;
                    stream.Seek(size + 4L, SeekOrigin.Current);
                }
                return false;
            }
            if (signature[0] == 0xFF && signature[1] == 0xD8)
            {
                stream.Position = 2;
                Span<byte> lengthBytes = stackalloc byte[2];
                Span<byte> marker = stackalloc byte[12];
                while (stream.Position + 4 < stream.Length)
                {
                    if (stream.ReadByte() != 0xFF) return false;
                    int code;
                    do { code = stream.ReadByte(); } while (code == 0xFF);
                    if (code is < 0 or 0xDA or 0xD9) return false;
                    if (code is >= 0xD0 and <= 0xD7 or 0x01) continue;
                    stream.ReadExactly(lengthBytes);
                    int size = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes) - 2;
                    if (size < 0 || size > stream.Length - stream.Position)
                        throw new FileFormatException("JPEG 元数据长度无效。");
                    long end = stream.Position + size;
                    if (code == 0xE2 && size >= marker.Length)
                    {
                        stream.ReadExactly(marker);
                        if (marker.SequenceEqual("ICC_PROFILE\0"u8)) return true;
                    }
                    stream.Position = end;
                }
            }
            return false;
        }
        finally { stream.Position = position; }
    }
}

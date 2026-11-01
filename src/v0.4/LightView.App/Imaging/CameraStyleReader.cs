using System.Buffers.Binary;
using System.Text;

namespace LightView.App.Imaging;

// Independent, bounded metadata adapter. Only the verified Nikon type-2,
// PictureControl 0310 / 108-byte layout is enabled. No external parser code used.
// Format facts: exiftool/exiftool commit 980409314fddbd83d9092b5c160fafab7f67fd52,
// Nikon.pm PictureControl3 and MakerNotes.pm MakerNoteNikon; evidence sources/format-source.json.
internal static class CameraStyleReader
{
    internal const int MaximumMakerNoteBytes = 64 * 1024;
    internal static CameraStyle Status(CameraStyleState state) => new("", "", state);
    public static CameraStyle Read(byte[] data, long start, long length, string? make)
    {
        if (!string.Equals(make?.Trim(), "NIKON CORPORATION", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(make?.Trim(), "NIKON", StringComparison.OrdinalIgnoreCase)) return Status(CameraStyleState.Unsupported);
        if (length > MaximumMakerNoteBytes) return Status(CameraStyleState.OverBudget);
        if (start < 0 || length < 18 || start > data.Length || length > data.Length - start) return Status(CameraStyleState.Malformed);
        var note = data.AsSpan((int)start, (int)length);
        if (!note.Slice(0, 10).SequenceEqual(new byte[] { 78, 105, 107, 111, 110, 0, 2, 17, 0, 0 })) return Status(CameraStyleState.Unsupported);
        byte[] tiff = note.Slice(10).ToArray();
        if (tiff[0] != tiff[1] || tiff[0] is not (0x49 or 0x4d)) return Status(CameraStyleState.Malformed);
        bool little = tiff[0] == 0x49;
        ushort U16(int p) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(p, 2)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(p, 2));
        uint U32(int p) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(p, 4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(p, 4));
        if (U16(2) != 42) return Status(CameraStyleState.Malformed);
        uint directory = U32(4);
        if (directory < 8 || directory > tiff.Length - 2) return Status(CameraStyleState.Malformed);
        int at = (int)directory, count = U16(at);
        if (count > 512) return Status(CameraStyleState.OverBudget);
        if (at + 2L + count * 12L + 4 > tiff.Length) return Status(CameraStyleState.Malformed);
        int picture = -1;
        for (int i = 0; i < count; i++)
        {
            int entry = at + 2 + i * 12;
            if (U16(entry) != 0x0023) continue;
            if (picture != -1 || U16(entry + 2) != 7) return Status(CameraStyleState.Malformed);
            if (U32(entry + 4) != 108) return Status(CameraStyleState.Unsupported);
            uint offset = U32(entry + 8);
            if (offset < at + 2L + count * 12L + 4 || (long)offset + 108 > tiff.Length) return Status(CameraStyleState.Malformed);
            picture = (int)offset;
        }
        if (picture < 0) return Status(CameraStyleState.Missing);
        if (!tiff.AsSpan(picture, 4).SequenceEqual("0310"u8)) return Status(CameraStyleState.Unsupported);
        string? Field(int offset)
        {
            int size = 20;
            while (size > 0 && tiff[picture + offset + size - 1] is 0 or 32) size--;
            for (int i = 0; i < size; i++) if (tiff[picture + offset + i] is < 32 or > 126) return null;
            return Encoding.ASCII.GetString(tiff, picture + offset, size);
        }
        string? name = Field(8), basis = Field(28);
        if (name is null || basis is null) return Status(CameraStyleState.Malformed);
        if (name.Length == 0 && basis.Length == 0) return Status(CameraStyleState.Missing);
        return new(name, basis);
    }
}

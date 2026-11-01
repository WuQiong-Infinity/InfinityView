using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;

namespace LightView.Tests;

internal static class MetadataCompatibilityTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("truncated thumbnail metadata retries without changing pixels or retaining source", () => Compatible(root)),
        ("compatible metadata reading preserves EXIF rotation", () => Orientation(root)),
        ("metadata retry does not ignore invalid declared ICC", () => InvalidProfile(root)),
        ("metadata compatibility retry accepts only the exact WIC error", ErrorScope)
    ];

    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private static byte[] Pixels(BitmapSource source)
    {
        var display = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        byte[] bytes = new byte[display.PixelWidth * display.PixelHeight * 4];
        display.CopyPixels(bytes, display.PixelWidth * 4, 0); return bytes;
    }

    internal static (string Base, string Broken) Fixture(string root, string name, ushort orientation = 1, bool invalidIcc = false, int width = 32, int height = 16)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        string original = Path.Combine(folder, "base.jpg"), broken = Path.Combine(folder, "thumbnail.jpg");
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int i = (y * width + x) * 3; pixels[i] = (byte)(x * 7); pixels[i + 1] = (byte)(y * 13); pixels[i + 2] = 190; }
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3)));
        using (var file = File.Create(original)) encoder.Save(file);

        // Artificial big-endian TIFF: valid primary orientation, then a thumbnail
        // IFD declaring six entries but containing only four and a partial fifth.
        // No user photo bytes or metadata are included in this fixture.
        using var tiff = new MemoryStream();
        void U16(ushort value) { tiff.WriteByte((byte)(value >> 8)); tiff.WriteByte((byte)value); }
        void U32(uint value) { U16((ushort)(value >> 16)); U16((ushort)value); }
        void Entry(ushort tag, ushort type, uint value) { U16(tag); U16(type); U32(1); U32(value); }
        tiff.Write("MM"u8); U16(42); U32(8); U16(1); Entry(274, 3, (uint)orientation << 16); U32(26);
        U16(6); Entry(259, 3, 6u << 16); Entry(282, 5, 200); Entry(283, 5, 208); Entry(296, 3, 2u << 16);
        tiff.Write(new byte[8]);
        byte[] jpeg = File.ReadAllBytes(original);
        using var output = File.Create(broken); output.Write(jpeg.AsSpan(0, 2));
        void Segment(byte marker, byte[] data)
        {
            int size = data.Length + 2;
            output.Write(new byte[] { 255, marker, (byte)(size >> 8), (byte)size }); output.Write(data);
        }
        Segment(0xE1, "Exif\0\0"u8.ToArray().Concat(tiff.ToArray()).ToArray());
        if (invalidIcc) Segment(0xE2, "ICC_PROFILE\0"u8.ToArray().Concat(new byte[] { 1, 1, 0, 1, 2, 3 }).ToArray());
        output.Write(jpeg.AsSpan(2)); return (original, broken);
    }

    private static void Compatible(string root)
    {
        var paths = Fixture(root, "metadata-compatible");
        byte[] before = FrameworkApi.Sha256(File.ReadAllBytes(paths.Broken));
        bool failedEager = false;
        try
        {
            using var file = File.OpenRead(paths.Broken);
            _ = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        }
        catch (Exception error) when (WicImageLoader.IsBadMetadataHeader(error)) { failedEager = true; }
        Check(failedEager, "Fixture no longer reproduces the exact eager metadata failure on this WIC version");
        var loader = new WicImageLoader(); var expected = loader.Load(paths.Base, CancellationToken.None);
        var actual = loader.Load(paths.Broken, CancellationToken.None);
        Check(actual.Source.IsFrozen && Pixels(actual.Source).SequenceEqual(Pixels(expected.Source)), "Compatible read changed pixels");
        Check(actual.Metadata!.Warning?.Contains("兼容读取") == true, "Missing metadata compatibility notice");
        Check(loader.Load(paths.Broken, CancellationToken.None).Source == actual.Source && loader.CacheHits == 1, "Compatible read not cached");
        using (var exclusive = new FileStream(paths.Broken, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Pixels(actual.Source).Length == 32 * 16 * 4, "Cached presentation still depends on the source stream");
        Check(before.SequenceEqual(FrameworkApi.Sha256(File.ReadAllBytes(paths.Broken))), "Loader modified source bytes");
    }

    private static void Orientation(string root)
    {
        var paths = Fixture(root, "metadata-orientation", 6); var loader = new WicImageLoader();
        var original = loader.Load(paths.Base, CancellationToken.None); var rotated = loader.Load(paths.Broken, CancellationToken.None);
        var expected = new TransformedBitmap(original.Source, new RotateTransform(90));
        Check(rotated.PixelWidth == 16 && rotated.PixelHeight == 32 && Pixels(rotated.Source).SequenceEqual(Pixels(expected)), "EXIF rotation lost during retry");
    }

    private static void InvalidProfile(string root)
    {
        var paths = Fixture(root, "metadata-invalid-icc", invalidIcc: true); bool refused = false;
        try { new WicImageLoader().Load(paths.Broken, CancellationToken.None); }
        catch (NotSupportedException) { refused = true; }
        Check(refused, "Malformed declared ICC silently accepted by compatibility read");
        using var exclusive = new FileStream(paths.Broken, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void ErrorScope()
    {
        Check(WicImageLoader.IsBadMetadataHeader(new ArgumentException("wrapped", new COMException("metadata", unchecked((int)0x88982F63)))), "Nested metadata error not recognized");
        foreach (Exception error in new Exception[] { new IOException(), new ArgumentException(), new OperationCanceledException(),
            new COMException("bad pixels", unchecked((int)0x88982F60)), new COMException("bad header", unchecked((int)0x88982F61)) })
            Check(!WicImageLoader.IsBadMetadataHeader(error), "Compatibility retry widened to unrelated error");
    }
}

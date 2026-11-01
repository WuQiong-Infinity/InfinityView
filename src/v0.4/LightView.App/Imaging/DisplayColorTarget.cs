using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;

namespace LightView.App.Imaging;

internal sealed record DisplayColorTarget(byte[] Profile, bool IsStandardRgb, string Description, string Key)
{
    private static readonly Lazy<DisplayColorTarget> standard = new(() =>
    {
        using var stream = new ColorContext(PixelFormats.Bgra32).OpenProfileStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new(buffer.ToArray(), true, "sRGB", "srgb");
    });
    public static DisplayColorTarget Standard => standard.Value;
    public static DisplayColorTarget FromProfile(byte[] bytes, string description)
    {
        if (bytes.Length < 128 || bytes.Length > 16 * 1024 * 1024)
            throw new NotSupportedException("显示器色彩配置尺寸无效。");
        bool same = bytes.AsSpan().SequenceEqual(Standard.Profile);
        return new((byte[])bytes.Clone(), same, description, FrameworkApi.Hex(FrameworkApi.Sha256(bytes)));
    }
}

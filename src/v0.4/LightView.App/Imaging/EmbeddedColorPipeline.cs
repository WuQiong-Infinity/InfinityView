using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Platform;

namespace LightView.App.Imaging;

internal static class EmbeddedColorPipeline
{
    public static (BitmapSource Source, string Description) Convert(BitmapFrame frame, DisplayColorTarget target,
        PngColorMetadata png, bool declaresIcc, BitmapSource? decodedPixels = null, ushort orientation = 1, CancellationToken cancellation = default)
    {
        BitmapSource input = decodedPixels ?? frame;
        var contexts = frame.ColorContexts;
        bool embedded = contexts is { Count: > 0 };
        byte[] sourceProfile;
        string description;
        if (declaresIcc || embedded && !png.Srgb && !png.NeedsProfile)
        {
            using var sourceBytes = new MemoryStream();
            using var sourceStream = contexts![0].OpenProfileStream();
            sourceStream.CopyTo(sourceBytes);
            sourceProfile = sourceBytes.ToArray();
            description = declaresIcc ? "嵌入 ICC" : "图片色彩标记";
        }
        else if (png.Srgb) { sourceProfile = DisplayColorTarget.Standard.Profile; description = "PNG sRGB 标记"; }
        else if (png.NeedsProfile)
        {
            sourceProfile = LittleCmsTransform.ProfileForPng(png);
            description = png.FileGamma.HasValue ? "PNG gAMA/cHRM" : "PNG 色度，按 sRGB 曲线假设";
            if (png.Chromaticities is null) description += "，按 sRGB 色度假设";
        }
        else { sourceProfile = DisplayColorTarget.Standard.Profile; description = "无色彩标记，按 sRGB 假设"; }
        bool highPrecision = StripeBitmapWriter.HighPrecision(input);
        using var transform = target.IsStandardRgb && sourceProfile.AsSpan().SequenceEqual(target.Profile)
            ? null : new LittleCmsTransform.Session(sourceProfile, target.Profile, highPrecision);
        return (StripeBitmapWriter.Convert(input, orientation, transform, highPrecision, cancellation), description);
    }
}

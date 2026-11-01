using System.IO;
using LightView.App.Platform;

namespace LightView.App.Imaging;

public sealed partial class WicImageLoader
{
    private LoadedImage? LoadWebp(string path, FileStream stream, Organizing.FileSnapshot version,
        DisplayColorTarget target, bool prefetch, long generation, CancellationToken cancellation)
    {
        using var working = ComparisonPolicy.BeginWorking(stream.Length);
        stream.Position = 0; byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        cancellation.ThrowIfCancellationRequested(); var header = WebpDecoder.Inspect(bytes);
        long pixels = (long)header.Width * header.Height;
        if (header.Width <= 0 || header.Height <= 0 || pixels > MaximumPixels) throw new NotSupportedException("WebP 超过 1 亿像素限制。");
        ComparisonPolicy.CheckPresentation(ComparisonBudget.Presentation(header.Width, header.Height, 32));
        if (prefetch && pixels * 4 > cache.Budget) return null;
        long estimate = checked(bytes.LongLength + pixels * 16 + StripeBitmapWriter.ScratchEstimate(header.Width));
        if (estimate > 768L * 1024 * 1024) throw new NotSupportedException("WebP 解码的预计内存超过当前安全预算。");
        working.Resize(estimate);
        var decoded = WebpDecoder.Decode(bytes, header); cancellation.ThrowIfCancellationRequested();
        byte[] profile = header.Profile ?? DisplayColorTarget.Standard.Profile;
        using var transform = target.IsStandardRgb && profile.AsSpan().SequenceEqual(target.Profile) ? null : new LittleCmsTransform.Session(profile, target.Profile, false);
        var source = StripeBitmapWriter.Convert(decoded, header.Orientation, transform, false, cancellation);
        var loaded = new LoadedImage(path, source, header.Animated ? "WebP（动画首帧）" : "WebP", header.Profile is null ? 0 : 1,
            $"色彩：{(header.Profile is null ? "无色彩标记，按 sRGB 假设" : "嵌入 ICC")} → {target.Description}", version,
            new(header.Width, header.Height, 96, 96, "BGRA32", header.Exif.Fields,
                header.Animated ? "动画 WebP 显示首帧。" + header.Exif.Warning : header.Exif.Warning, header.Exif.Style),
            false, source.PixelWidth, source.PixelHeight, target.Key);
        var result = working.Complete(loaded, () => cache.Add(version, target.Key, loaded, generation), prefetch || cancellation.IsCancellationRequested);
        try { cancellation.ThrowIfCancellationRequested(); return result; }
        catch { ComparisonPolicy.ReleaseDecoded(result); throw; }
    }
}

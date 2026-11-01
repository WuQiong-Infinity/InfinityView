using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.Core.Viewing;

namespace LightView.App.Imaging;

public sealed partial class WicImageLoader : IImageLoader, IImagePrefetcher, IProgressiveImageLoader
{
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static int foregroundWaiters;
    private const long MaximumFileBytes = 128L * 1024 * 1024;
    private const long MaximumPixels = 100_000_000;
    private readonly Func<DisplayColorTarget> targetProvider;
    private readonly ImageMemoryCache cache;
    internal long CacheBytes => cache.Bytes;
    internal int CacheCount => cache.Count;
    internal int CacheFullCount => cache.FullCount;
    private int cacheHits;
    internal int CacheHits => Volatile.Read(ref cacheHits);
    private int decodeCount;
    internal int DecodeCount => Volatile.Read(ref decodeCount);
    internal ComparisonLoadPolicy ComparisonPolicy { get; } = new();
    internal void SetComparisonMode(bool enabled) => ComparisonPolicy.Enable(enabled);
    public WicImageLoader() : this(() => DisplayColorTarget.Standard) { }
    internal WicImageLoader(Func<DisplayColorTarget> targetProvider, long cacheBudget = 512L * 1024 * 1024)
    { this.targetProvider = targetProvider; cache = new(cacheBudget, ComparisonPolicy.SyncRoot); ComparisonPolicy.AttachCache(cache); }

    public LoadedImage Load(string path, CancellationToken cancellationToken) =>
        Read(path, cancellationToken, 0, false)!;

    public LoadedImage LoadPreview(string path, int maximumEdge, CancellationToken cancellationToken) =>
        Read(path, cancellationToken, PreviewEdge(path, maximumEdge), false)!;

    public bool TryPrefetch(string path, CancellationToken cancellationToken) =>
        Read(path, cancellationToken, 0, true) is not null;

    public bool TryPrefetchPreview(string path, int maximumEdge, CancellationToken cancellationToken) =>
        Read(path, cancellationToken, PreviewEdge(path, maximumEdge), true) is not null;

    private static int PreviewEdge(string path, int maximumEdge) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? FrameworkApi.Clamp(maximumEdge, 512, 2560) : 0;

    private LoadedImage? Read(string path, CancellationToken cancellationToken, int edge, bool prefetch)
    {
        FrameworkApi.ThrowIfNullOrWhiteSpace(path);
        if (!ImageFormats.IsSupported(path))
            throw new NotSupportedException("当前仅支持 JPG、JPEG、PNG、BMP 和 WebP 图片。");
        if (prefetch && (Volatile.Read(ref foregroundWaiters) > 0 || DecodeGate.CurrentCount == 0)) return null;
        long cacheGeneration = cache.Generation;
        string fullPath = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes)
            throw new NotSupportedException("图片文件超过 128 MiB 安全限制，请选择较小的文件。");
        var fileVersion = LightView.App.Organizing.FileSnapshot.Capture(fullPath, stream);
        var target = targetProvider();
        string cacheKey = target.Key + (edge > 0 ? $":preview:{edge}" : "");
        bool TryCached(out LoadedImage? image)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (ComparisonPolicy.SyncRoot)
            {
                if (edge > 0 && cache.TryGet(fileVersion, target.Key, out image) || cache.TryGet(fileVersion, cacheKey, out image))
                {
                    ComparisonPolicy.CheckPresentation(ComparisonBudget.Presentation(image!.PixelWidth, image.PixelHeight, image.Source.Format.BitsPerPixel));
                    if (prefetch) ComparisonPolicy.CheckWorking(0);
                    else image = ComparisonPolicy.RegisterDecoded(image);
                    Interlocked.Increment(ref cacheHits); return true;
                }
                return false;
            }
        }
        // Frozen cache hits only need their fresh file snapshot, never the heavy
        // decoder gate. The cache itself serializes LRU updates.
        LoadedImage? cached;
        try { if (TryCached(out cached)) return cached; }
        catch (ComparisonBudgetExceededException) when (prefetch) { return null; }
        if (prefetch)
        {
            if (Volatile.Read(ref foregroundWaiters) > 0 || !DecodeGate.Wait(0)) return null;
        }
        else
        {
            Interlocked.Increment(ref foregroundWaiters);
            try { DecodeGate.Wait(cancellationToken); }
            finally { Interlocked.Decrement(ref foregroundWaiters); }
        }
        try
        {
            if (TryCached(out cached)) return cached;
            if (prefetch && Volatile.Read(ref foregroundWaiters) > 0) return null;
            return LoadCore(fullPath, stream, fileVersion, target, cacheKey, edge, prefetch, cacheGeneration, cancellationToken);
        }
        catch (ComparisonBudgetExceededException) when (prefetch) { return null; }
        finally { DecodeGate.Release(); }
    }

    private LoadedImage? LoadCore(string fullPath, FileStream stream,
        LightView.App.Organizing.FileSnapshot fileVersion, DisplayColorTarget target, string cacheKey,
        int edge, bool prefetch, long cacheGeneration, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref decodeCount);
        if (Path.GetExtension(fullPath).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return LoadWebp(fullPath, stream, fileVersion, target, prefetch, cacheGeneration, cancellationToken);
        var exif = ExifReader.Read(stream);
        bool declaresIcc = EmbeddedProfileDeclaration.IsPresent(stream);
        var png = PngColorMetadata.Read(stream);

        var header = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnDemand);
        var dimensions = header.Frames[0];
        if (dimensions.Format == PixelFormats.Cmyk32)
            throw new NotSupportedException("当前不支持 CMYK 图片，请另存为带色彩配置的 RGB 副本后打开，保留原图。");
        if (dimensions.PixelWidth <= 0 || dimensions.PixelHeight <= 0 ||
            (long)dimensions.PixelWidth * dimensions.PixelHeight > MaximumPixels)
            throw new NotSupportedException("图片超过 1 亿像素安全限制，请另存较小的副本后打开，保留原图。");
        int originalEdge = Math.Max(dimensions.PixelWidth, dimensions.PixelHeight);
        ComparisonPolicy.CheckPresentation(ComparisonBudget.Presentation(dimensions.PixelWidth, dimensions.PixelHeight, dimensions.Format.BitsPerPixel));
        bool preview = edge > 0 && originalEdge > edge;
        // JPEG's native 1/2, 1/4 and 1/8 sizes avoid decoding the next larger
        // level just to resize a slightly smaller temporary preview afterward.
        int decodeEdge = originalEdge;
        if (preview)
            foreach (int divisor in new[] { 2, 4, 8 })
            {
                decodeEdge = (originalEdge + divisor - 1) / divisor;
                if (decodeEdge <= edge) break;
            }
        double scale = preview ? (double)decodeEdge / originalEdge : 1;
        long pixels = preview ? (long)Math.Ceiling(dimensions.PixelWidth * scale) * (long)Math.Ceiling(dimensions.PixelHeight * scale)
            : (long)dimensions.PixelWidth * dimensions.PixelHeight;
        long cacheCost = pixels * Math.Max(4, (dimensions.Format.BitsPerPixel + 7) / 8);
        if (prefetch && cacheCost > cache.Budget) return null;
        int inputBytes = Math.Max(StripeBitmapWriter.HighPrecision(dimensions) ? 8 : 4, (dimensions.Format.BitsPerPixel + 7) / 8);
        long workingEstimate = checked(pixels * (inputBytes * 2 + 4) + StripeBitmapWriter.ScratchEstimate((int)Math.Ceiling(dimensions.PixelWidth * scale)));
        if (workingEstimate > 768L * 1024 * 1024)
            throw new NotSupportedException("图片转换的预计内存超过当前安全预算，请使用较小的图片。");
        using var working = ComparisonPolicy.BeginWorking(workingEstimate);

        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        BitmapDecoder decoder;
        bool metadataCompatibilityRead = false;
        BitmapSource? previewPixels = null;
        if (preview)
        {
            // Decode the actual JPEG at a smaller size; the header frame remains
            // the authoritative source of ICC, EXIF and original dimensions.
            decoder = header;
            var small = new BitmapImage();
            small.BeginInit();
            small.StreamSource = stream;
            small.CacheOption = BitmapCacheOption.OnDemand;
            small.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
            if (dimensions.PixelWidth >= dimensions.PixelHeight) small.DecodePixelWidth = decodeEdge;
            else small.DecodePixelHeight = decodeEdge;
            small.EndInit();
            small.Freeze();
            previewPixels = small;
        }
        else
        {
        try
        {
            decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        }
        catch (Exception error) when (IsBadMetadataHeader(error))
        {
            // Some JPEG metadata blocks fail WIC's eager metadata cache even when
            // pixels, orientation and color contexts can be read correctly. Retry
            // only that exact error, keep ICC validation, and detach all pixels
            // before the original stream can close.
            if (workingEstimate + pixels * 8 > 768L * 1024 * 1024)
                throw new NotSupportedException("此图片需要兼容读取，但预计内存超过当前安全预算。", error);
            working.Resize(workingEstimate + pixels * 8);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
            metadataCompatibilityRead = true;
        }
        }
        BitmapFrame frame = decoder.Frames[0];
        frame.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        if (declaresIcc && frame.ColorContexts is not { Count: > 0 })
            throw new NotSupportedException("图片声明了嵌入 ICC，但无法读取有效配置。未忽略此色彩错误。");

        ushort orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value) orientation = value;
        (BitmapSource Source, string Description) color;
        try { color = EmbeddedColorPipeline.Convert(frame, target, png, declaresIcc, previewPixels, orientation, cancellationToken); }
        catch (NullReferenceException error) when ((metadataCompatibilityRead || preview) && declaresIcc)
        {
            // WIC can expose a ColorContext whose invalid profile has no native
            // handle. Refuse it explicitly rather than assuming an sRGB profile.
            throw new NotSupportedException("图片声明了嵌入 ICC，但无法读取有效配置。未忽略此色彩错误。", error);
        }
        BitmapSource source = color.Source;

        var loaded = new LoadedImage(fullPath, source,
            decoder.CodecInfo.FriendlyName ?? decoder.GetType().Name,
            frame.ColorContexts?.Count ?? 0,
            $"色彩：{color.Description} → {target.Description}", fileVersion,
            new(frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY, frame.Format.ToString(), exif.Fields,
                metadataCompatibilityRead ? "部分图片信息采用兼容读取。" + (exif.Warning is null ? "" : " " + exif.Warning) : exif.Warning, exif.Style),
            preview, orientation is >= 5 and <= 8 ? frame.PixelHeight : frame.PixelWidth,
            orientation is >= 5 and <= 8 ? frame.PixelWidth : frame.PixelHeight, target.Key);
        var result = working.Complete(loaded,
            () => cache.Add(fileVersion, preview ? cacheKey : target.Key, loaded, cacheGeneration),
            prefetch || cancellationToken.IsCancellationRequested);
        try { cancellationToken.ThrowIfCancellationRequested(); return result; }
        catch { ComparisonPolicy.ReleaseDecoded(result); throw; }
    }
    internal static bool IsBadMetadataHeader(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (unchecked((uint)current.HResult) == 0x88982F63) return true;
        return false;
    }
}

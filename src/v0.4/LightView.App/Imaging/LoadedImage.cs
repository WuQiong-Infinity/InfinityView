using System.Windows.Media.Imaging;

namespace LightView.App.Imaging;

public enum ImageViewIntent { Open, Adjacent, Refresh }

public sealed record LoadedImage(
    string FilePath,
    BitmapSource Source,
    string DecoderName,
    int EmbeddedProfileCount,
    string? ColorDescription = null,
    LightView.App.Organizing.FileSnapshot? FileVersion = null,
    ImageMetadata? Metadata = null,
    bool IsPreview = false,
    int OriginalWidth = 0,
    int OriginalHeight = 0,
    string? DisplayTargetKey = null,
    ImageViewIntent ViewIntent = ImageViewIntent.Open)
{
    // Record copies share the idempotent lease for one foreground delivery.
    internal IDisposable? PendingLease { get; init; }
    public int PixelWidth => OriginalWidth > 0 ? OriginalWidth : Source.PixelWidth;
    public int PixelHeight => OriginalHeight > 0 ? OriginalHeight : Source.PixelHeight;
}

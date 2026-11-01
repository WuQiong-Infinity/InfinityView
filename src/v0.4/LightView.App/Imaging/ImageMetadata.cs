namespace LightView.App.Imaging;

public sealed record ImageDetail(string Label, string Value, string? Id = null);
public sealed record ImageDetailSection(string Title, IReadOnlyList<ImageDetail> Rows, string? Id = null);
public enum CameraStyleState { Missing, Unsupported, Malformed, OverBudget, Parsed }
public sealed record CameraStyle(string Name, string BaseName, CameraStyleState State = CameraStyleState.Parsed)
{
    public IReadOnlyList<ImageDetail> Rows => (State == CameraStyleState.Parsed ? new[]
    {
        new ImageDetail("滤镜名称", Name, "style.name"),
        new ImageDetail("基础风格", BaseName == "FLEXIBLE COLOR" ? "灵活颜色（FLEXIBLE COLOR）" : BaseName, "style.base")
    } : Array.Empty<ImageDetail>()).Where(row => !FrameworkApi.IsNullOrWhiteSpace(row.Value) && (row.Label != "基础风格" || BaseName != Name)).ToArray();
}
internal sealed record ExifReadResult(IReadOnlyList<ImageDetail> Fields, string? Warning, CameraStyle? Style = null);
public sealed record ImageMetadata(int OriginalWidth, int OriginalHeight, double DpiX, double DpiY,
    string PixelFormat, IReadOnlyList<ImageDetail> Shooting, string? Warning = null, CameraStyle? Style = null);

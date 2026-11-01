namespace LightView.Core.Viewing;

public static class ImageFormats
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    public const string DialogFilter =
        "图片 (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp";

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path));
}

using System.Text.Json;
using System.Text.Json.Serialization;
using LightView.Core.Commands;

namespace LightView.Core.Settings;

public enum FileConflictPolicy { Rename, Skip, Ask }

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 3;
    public double ZoomStep { get; set; } = 1.2;
    public bool ShowColorInfo { get; set; }
    public bool UndoJumpBack { get; set; } = true;
    public bool EdgeNavigationEnabled { get; set; } = true;
    public string MouseNavigationRegion { get; set; } = "halves";
    public string MousePanButton { get; set; } = "right";
    public bool KeepViewOnImageChange { get; set; }
    public FileConflictPolicy ConflictPolicy { get; set; } = FileConflictPolicy.Rename;
    public AppearanceSettings Appearance { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public List<ShortcutBinding> Bindings { get; set; } = CommandCatalog.DefaultBindings();
    public List<FolderPreset> Presets { get; set; } = FolderPreset.Defaults();
    [JsonExtensionData] public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}

public static class SettingsValidation
{
    public static string? Error(AppSettings settings)
    {
        if (settings.SchemaVersion != 3) return "此设置版本暂不支持，不能覆盖原配置。";
        if (settings.MouseNavigationRegion is not ("halves" or "edges") || settings.MousePanButton is not ("left" or "right"))
            return "鼠标切图区域或拖动按钮无效。";
        if (settings.Appearance is not { } appearance || settings.Window is not { } window) return "外观或窗口设置无效。";
        if (DetailsCatalog.Error(appearance.DetailsLayout) is string detailsError) return detailsError;
        if (appearance.CanvasBackground is not ("darkGray" or "midGray" or "black")) return "画布背景选项无效。";
        if (!InRange(appearance.DetailsFontSize, 12, 24) || !InRange(appearance.DetailsOutlineThickness, 0, 3) ||
            !InRange(appearance.DetailsLineGap, 0, 12) || !InRange(appearance.DetailsWidth, 320, 600)) return "详情字号、描边、行距或列宽超出允许范围。";
        if (window.SavedDpiScale is double dpi && !InRange(dpi, 0.5, 8)) return "保存的窗口缩放比例无效。";
        if (window.RestoreBounds is { } bounds && (!FrameworkApi.IsFinite(bounds.Left) || !FrameworkApi.IsFinite(bounds.Top) ||
            !InRange(bounds.Width, 1, 100000) || !InRange(bounds.Height, 1, 100000))) return "保存的窗口位置无效。";
        if (!FrameworkApi.IsEnumDefined(settings.ConflictPolicy)) return "同名冲突策略无效。";
        if (!FrameworkApi.IsFinite(settings.ZoomStep) || settings.ZoomStep < 1.01 || settings.ZoomStep > 4)
            return "每档缩放倍数必须在 1.01 到 4 之间。";
        if (settings.Bindings is null || settings.Bindings.Count > 2000) return "快捷键列表无效或过大。";
        if (settings.Presets is null || settings.Presets.Count > 1000) return "目标文件夹列表无效或过大。";
        var presetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in settings.Presets)
        {
            if (preset is null || FrameworkApi.IsNullOrWhiteSpace(preset.Id) || preset.Id.Length > 64 ||
                !preset.Id.All(c => FrameworkApi.IsAsciiLetterOrDigit(c) || c is '-' or '_') || !presetIds.Add(preset.Id))
                return "目标文件夹的标识无效或重复。";
            if (FrameworkApi.IsNullOrWhiteSpace(preset.Name)) return "请为目标文件夹填写名称。";
            if (!FrameworkApi.IsNullOrWhiteSpace(preset.Path))
            {
                try
                {
                    if (!FrameworkApi.IsPathFullyQualified(preset.Path)) return $"「{preset.Name}」需要绝对路径。";
                    _ = Path.GetFullPath(preset.Path);
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
                { return $"「{preset.Name}」的路径无效。"; }
            }
        }
        var gestures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in settings.Bindings)
        {
            if (binding is null) return "快捷键列表中存在空记录。";
            if (!binding.Enabled || FrameworkApi.IsNullOrWhiteSpace(binding.Gesture)) continue;
            var command = CommandCatalog.Find(binding.CommandId, settings.Presets);
            if (command is null) continue;
            string normalized;
            try { normalized = ShortcutGesture.Normalize(binding.Gesture); }
            catch (FormatException error) { return error.Message; }
            if (gestures.TryGetValue(normalized, out string? previous))
                return $"{ShortcutGesture.Display(normalized)} 同时绑定了「{previous}」和「{command.Name}」。请替换或清空其中一项。";
            gestures.Add(normalized, command.Name);
        }
        return null;
    }
    private static bool InRange(double value, double minimum, double maximum) => FrameworkApi.IsFinite(value) && value >= minimum && value <= maximum;
}

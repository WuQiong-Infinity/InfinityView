using System.IO;
using System.Text.Json;
using LightView.Core.Settings;

namespace LightView.App.Persistence;

[Flags] internal enum SettingsGroups { None = 0, Appearance = 1, Viewing = 2, Shortcuts = 4, Organizing = 8, All = 15 }
internal sealed record SettingsImportPreview(AppSettings Settings, bool IncludesTargetPaths, IReadOnlyList<string> Warnings);
internal sealed class SettingsPackage
{
    public string Format { get; set; } = "LightView.settings";
    public int PackageVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IncludesTargetPaths { get; set; }
    public AppSettings Settings { get; set; } = new();
}

internal static class SettingsTransfer
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Export(string path, AppSettings applied, bool includePaths, string activeSettingsPath)
    {
        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) throw new FormatException("配置包请使用 .json 文件名。");
        if (path.Equals(Path.GetFullPath(activeSettingsPath), StringComparison.OrdinalIgnoreCase)) throw new IOException("导出目标不能是正在使用的设置文件。");
        if (SettingsValidation.Error(applied) is string error) throw new FormatException(error);
        var supported = new AppSettings
        {
            ZoomStep = applied.ZoomStep, ShowColorInfo = applied.ShowColorInfo, UndoJumpBack = applied.UndoJumpBack,
            KeepViewOnImageChange = applied.KeepViewOnImageChange, EdgeNavigationEnabled = applied.EdgeNavigationEnabled, MouseNavigationRegion = applied.MouseNavigationRegion, MousePanButton = applied.MousePanButton, ConflictPolicy = applied.ConflictPolicy,
            Appearance = applied.Appearance.Clone(),
            Window = new() { RememberPlacement = applied.Window.RememberPlacement, FullScreenAutoHide = applied.Window.FullScreenAutoHide },
            Bindings = applied.Bindings.ToList(),
            Presets = applied.Presets.Select(p => new FolderPreset { Id = p.Id, Name = p.Name, Path = includePaths ? p.Path : "", Enabled = p.Enabled, CopyAdvance = p.CopyAdvance, MoveAdvance = p.MoveAdvance }).ToList()
        };
        // Export only supported configuration. Machine placement, unknown extensions,
        // logs, photo bytes, registration and execution/environment paths are excluded.
        supported.Appearance.UnknownFields = null;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new SettingsPackage { IncludesTargetPaths = includePaths, Settings = supported }, Options);
        if (bytes.Length > MaximumBytes) throw new IOException("配置包超过 1 MiB，未导出。");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path); // Never overwrite an existing backup or another file.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static SettingsImportPreview Read(string path)
    {
        try { return ReadCore(path); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new FormatException("配置包结构或字段类型无效，未导入。", error); }
    }
    private static SettingsImportPreview ReadCore(string path)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaximumBytes) throw new IOException("配置包超过 1 MiB，已停止读取。");
            bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        }
        ReadOnlySpan<byte> input = bytes;
        if (input.StartsWith(new byte[] { 239, 187, 191 })) input = input.Slice(3);
        using var document = JsonDocument.Parse(input.ToArray());
        CheckDuplicates(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var marker) || marker.GetString() != "LightView.settings" ||
            !root.TryGetProperty("packageVersion", out var version) || !version.TryGetInt32(out int packageVersion) || packageVersion != 1 ||
            !root.TryGetProperty("includesTargetPaths", out var paths) || paths.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("settings", out var settingsNode) || settingsNode.ValueKind != JsonValueKind.Object)
            throw new FormatException("这不是支持的 LightView 配置备份。");
        Require(settingsNode, "schemaVersion", "zoomStep", "showColorInfo", "undoJumpBack", "edgeNavigationEnabled", "conflictPolicy", "appearance", "window", "bindings", "presets");
        if (!settingsNode.GetProperty("schemaVersion").TryGetInt32(out int schema) || schema != 3) throw new FormatException("配置版本不受支持，未导入。");
        Require(settingsNode.GetProperty("appearance"), "canvasBackground", "transparencyCheckerboard", "detailsFontSize", "detailsOutlineThickness", "detailsLineGap", "detailsWidth", "detailsFitSmallWindow", "reduceMotion");
        Require(settingsNode.GetProperty("window"), "rememberPlacement", "fullScreenAutoHide");
        foreach (var binding in settingsNode.GetProperty("bindings").EnumerateArray())
        {
            Require(binding, "commandId", "gesture", "enabled");
            if (binding.GetProperty("commandId").ValueKind != JsonValueKind.String || FrameworkApi.IsNullOrWhiteSpace(binding.GetProperty("commandId").GetString()) || binding.GetProperty("gesture").ValueKind != JsonValueKind.String)
                throw new FormatException("配置包中的快捷键记录无效。");
        }
        foreach (var preset in settingsNode.GetProperty("presets").EnumerateArray())
            Require(preset, "id", "name", "path", "enabled", "copyAdvance", "moveAdvance");
        var settings = settingsNode.Deserialize<AppSettings>(Options) ?? throw new FormatException("配置为空。");
        if (SettingsValidation.Error(settings) is string error) throw new FormatException(error);
        var warnings = new List<string>();
        foreach (var preset in settings.Presets)
        {
            if (!paths.GetBoolean()) preset.Path = "";
            if (FrameworkApi.IsNullOrWhiteSpace(preset.Path) || !Directory.Exists(preset.Path))
            {
                preset.Enabled = false;
                warnings.Add($"「{preset.Name}」路径未设置、不存在或不可访问，导入后将停用，需修复后手动启用。");
            }
        }
        return new(settings, paths.GetBoolean(), warnings);
    }

    public static AppSettings Merge(AppSettings currentApplied, SettingsImportPreview incoming, SettingsGroups groups)
    {
        if (groups == SettingsGroups.None || (groups & ~SettingsGroups.All) != 0) throw new FormatException("请至少选择一组可导入设置。");
        var result = currentApplied.Clone(); var source = incoming.Settings;
        if (groups.HasFlag(SettingsGroups.Appearance))
        { var unknown = result.Appearance.UnknownFields; result.Appearance = source.Appearance.Clone(); result.Appearance.UnknownFields = unknown; }
        if (groups.HasFlag(SettingsGroups.Viewing))
        {
            result.KeepViewOnImageChange = source.KeepViewOnImageChange; result.ZoomStep = source.ZoomStep; result.ShowColorInfo = source.ShowColorInfo; result.EdgeNavigationEnabled = source.EdgeNavigationEnabled;
            result.MouseNavigationRegion = source.MouseNavigationRegion; result.MousePanButton = source.MousePanButton;
            result.Window.RememberPlacement = source.Window.RememberPlacement; result.Window.FullScreenAutoHide = source.Window.FullScreenAutoHide;
        }
        if (groups.HasFlag(SettingsGroups.Shortcuts)) result.Bindings = source.Bindings.ToList();
        if (groups.HasFlag(SettingsGroups.Organizing))
        { result.Presets = source.Clone().Presets; result.ConflictPolicy = source.ConflictPolicy; result.UndoJumpBack = source.UndoJumpBack; }
        if (SettingsValidation.Error(result) is string error) throw new FormatException(error);
        return result;
    }
    private static void Require(JsonElement node, params string[] names)
    {
        if (node.ValueKind != JsonValueKind.Object || names.Any(name => !node.TryGetProperty(name, out _))) throw new FormatException("配置包缺少必要字段，未导入。");
    }
    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new FormatException("配置包存在重复字段，未导入。"); CheckDuplicates(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) CheckDuplicates(child);
    }
}

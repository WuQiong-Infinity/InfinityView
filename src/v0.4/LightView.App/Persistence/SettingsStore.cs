using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightView.Core.Settings;
using LightView.Core.Commands;

namespace LightView.App.Persistence;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning, bool IsReadOnly);

public sealed class SettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "settings.json");
    private string? expectedFingerprint;
    private bool corruptPrimary, readOnly;
    private byte[]? migrationOriginal;

    public SettingsLoadResult Load()
    {
        corruptPrimary = readOnly = false;
        migrationOriginal = null;
        byte[]? primary = Read(FilePath);
        expectedFingerprint = Fingerprint(primary);
        if (primary is null) return new(new AppSettings(), null, false);
        try
        {
            var settings = Parse(primary, out bool legacyCollision, out bool migrated);
            readOnly = settings.SchemaVersion > 3 || legacyCollision;
            if (migrated && !legacyCollision) migrationOriginal = primary;
            string? collisionWarning = null;
            if (legacyCollision)
            {
                try { PreserveLegacy(primary, true); collisionWarning = "旧配置存在与新版外观或窗口设置同名的字段。已保留升级前完整备份并禁用保存，请先处理冲突。"; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { collisionWarning = "旧配置存在同名字段冲突，已禁用保存。未能创建升级备份；原文件保持不变：" + error.Message; }
            }
            return new(settings, legacyCollision ? collisionWarning
                : readOnly ? "设置来自较新版本，此窗口只读。" : migrated ? "旧配置已在内存中转换，应用时会先保留升级前原件。" : null, readOnly);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            corruptPrimary = true;
            try
            {
                byte[]? backup = Read(FilePath + ".bak");
                if (backup is not null)
                {
                    var settings = Parse(backup, out bool legacyCollision, out bool migrated);
                    readOnly = settings.SchemaVersion > 3 || legacyCollision;
                    if (migrated && !legacyCollision) migrationOriginal = backup;
                    return new(settings, "主设置文件异常，已读取上一份备份。原文件会保留。", readOnly);
                }
            }
            catch (Exception backupError) when (backupError is JsonException or FormatException) { }
            return new(new AppSettings(), "设置及备份无法读取，暂用默认值。应用前会保留原文件。", false);
        }
    }

    public void Save(AppSettings settings) => SaveCore(settings, false);
    public void SaveImported(AppSettings settings) => SaveCore(settings, true);
    private void SaveCore(AppSettings settings, bool imported)
    {
        if (readOnly) throw new InvalidOperationException("较新版本的设置处于只读状态，不能覆盖。");
        if (SettingsValidation.Error(settings) is string error) throw new FormatException(error);
        string root = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(root);
        using var ownership = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[]? current = Read(FilePath);
        if (Fingerprint(current) != expectedFingerprint)
            throw new IOException("设置已被另一个窗口或程序更改，请关闭设置后重新打开再修改。");
        if (imported && current is not null)
            WriteBackup(current, Path.Combine(root, "settings.pre-import." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N") + ".json"));
        if (migrationOriginal is { } original)
        {
            PreserveLegacy(original, false);
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(FilePath))
            {
                string backup = corruptPrimary
                    ? FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")
                    : FilePath + ".bak";
                File.Replace(temporary, FilePath, backup);
            }
            else File.Move(temporary, FilePath);
            expectedFingerprint = Fingerprint(bytes);
            corruptPrimary = false;
            migrationOriginal = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[]? Read(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("设置文件超过 1 MiB，已停止读取。");
        return File.ReadAllBytes(path);
    }
    private static AppSettings Parse(byte[] bytes, out bool legacyCollision, out bool migrated)
    {
        ReadOnlySpan<byte> input = bytes;
        if (input.StartsWith(new byte[] { 239, 187, 191 })) input = input.Slice(3);
        var node = JsonNode.Parse(input) as JsonObject ?? throw new JsonException("设置为空。");
        var versionNode = node.FirstOrDefault(pair => pair.Key.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)).Value;
        int version = versionNode is null ? 2 : versionNode is JsonValue value && value.TryGetValue<int>(out int number) ? number : throw new JsonException("设置版本无效。");
        migrated = version is 1 or 2;
        legacyCollision = migrated && node.Any(pair => pair.Key.Equals("appearance", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("window", StringComparison.OrdinalIgnoreCase));
        if (version > 3) return new AppSettings { SchemaVersion = version };
        if (migrated)
        {
            // Reserved-field collisions cannot silently become a new typed value.
            foreach (string key in node.Select(pair => pair.Key).Where(key => key.Equals("appearance", StringComparison.OrdinalIgnoreCase) || key.Equals("window", StringComparison.OrdinalIgnoreCase)).ToArray()) node.Remove(key);
            if (!node.Any(pair => pair.Key.Equals("showColorInfo", StringComparison.OrdinalIgnoreCase))) node["showColorInfo"] = true;
        }
        AppSettings settings = node.Deserialize<AppSettings>(Options) ?? throw new JsonException("设置为空。");
        if (migrated) settings.SchemaVersion = version;
        // Schema 1 had no target presets. Preserve its explicit bindings;
        // newly introduced preset actions are shown unbound until selected.
        if (settings.SchemaVersion == 1) settings.SchemaVersion = 2;
        if (settings.SchemaVersion == 2 && settings.Bindings is { } bindings &&
            !bindings.Any(binding => binding?.CommandId == CommandCatalog.Details) &&
            !bindings.Any(binding => binding is { Enabled: true } && IsTab(binding.Gesture)))
            bindings.Add(new(CommandCatalog.Details, "TAB"));
        if (settings.SchemaVersion == 2 && settings.Bindings is { } pointerBindings &&
            !pointerBindings.Any(binding => binding?.CommandId == CommandCatalog.ActualPixelsAtPointer) &&
            !pointerBindings.Any(binding => binding is { Enabled: true } && IsMouseSide2(binding.Gesture)))
            pointerBindings.Add(new(CommandCatalog.ActualPixelsAtPointer, "MOUSEX2"));
        if (migrated) settings.SchemaVersion = 3;
        if (settings.SchemaVersion <= 3 && settings.Bindings is { } currentBindings && currentBindings.All(b => b is not null) && settings.Presets is { } currentPresets && currentPresets.All(p => p is not null))
        {
            var priorDefaults = CommandCatalog.DefaultBindings(currentPresets).Where(b => b.CommandId != CommandCatalog.Delete)
                .Concat(new[] { new ShortcutBinding(CommandCatalog.FocusLeft, "W"), new ShortcutBinding(CommandCatalog.FocusRight, "S") });
            if (currentBindings.OrderBy(b => b.CommandId).ThenBy(b => b.Gesture).SequenceEqual(priorDefaults.OrderBy(b => b.CommandId).ThenBy(b => b.Gesture)))
                currentBindings.RemoveAll(b => b.CommandId == CommandCatalog.FocusLeft && b.Gesture == "W" || b.CommandId == CommandCatalog.FocusRight && b.Gesture == "S");
            if (!currentBindings.Any(b => b.CommandId == CommandCatalog.Delete) &&
                !currentBindings.Any(b => b.Enabled && IsDelete(b.Gesture))) currentBindings.Add(new(CommandCatalog.Delete, "DELETE"));
        }
        if (settings.SchemaVersion <= 3 && SettingsValidation.Error(settings) is string error) throw new FormatException(error);
        return settings;
    }
    private void PreserveLegacy(byte[] bytes, bool conflict)
    {
        string root = Path.GetDirectoryName(FilePath)!;
        string suffix = conflict ? File.GetLastWriteTimeUtc(FilePath).ToString("yyyyMMddHHmmss") + ".conflict-" + Fingerprint(bytes)!.Substring(0, 16)
            : DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(root, "settings.pre-v0.2." + suffix + ".json");
        WriteBackup(bytes, backup);
    }
    private static void WriteBackup(byte[] bytes, string backup)
    {
        if (File.Exists(backup))
        {
            if (Fingerprint(Read(backup)) != Fingerprint(bytes)) throw new IOException("升级前备份校验失败，未覆盖任何设置。");
            return;
        }
        string temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool IsTab(string gesture)
    { try { return ShortcutGesture.Normalize(gesture) == "TAB"; } catch (FormatException) { return false; } }
    private static bool IsDelete(string gesture)
    { try { return ShortcutGesture.Normalize(gesture) == "DELETE"; } catch (FormatException) { return false; } }
    private static bool IsMouseSide2(string gesture)
    { try { return ShortcutGesture.Normalize(gesture) == "MOUSEX2"; } catch (FormatException) { return false; } }
    private static string? Fingerprint(byte[]? bytes) => bytes is null ? null : FrameworkApi.Hex(FrameworkApi.Sha256(bytes));
}

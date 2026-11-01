namespace LightView.Core.Commands;

public static class ShortcutGesture
{
    private static readonly string[] ModifierOrder = ["CTRL", "SHIFT", "ALT"];
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CONTROL"] = "CTRL", ["鼠标中键"] = "MOUSEMIDDLE", ["鼠标侧键1"] = "MOUSEX1",
        ["鼠标侧键2"] = "MOUSEX2", ["滚轮向上"] = "WHEELUP", ["滚轮向下"] = "WHEELDOWN",
        ["ESC"] = "ESCAPE", ["空格"] = "SPACE", ["`"] = "OEM3", ["OEMTILDE"] = "OEM3"
    };

    public static string Normalize(string? text)
    {
        if (FrameworkApi.IsNullOrWhiteSpace(text)) return "";
        string[] parts = text.Split('+').Select(part => part.Trim().ToUpperInvariant())
            .Select(part => Aliases.GetValueOrDefault(part, part)).ToArray();
        if (parts.Any(FrameworkApi.IsNullOrEmpty) || parts.Length > 4)
            throw new FormatException("快捷键格式无效，请使用录制或类似 Ctrl+K 的写法。");
        string key = parts[parts.Length - 1];
        if (key.Length == 1 && FrameworkApi.IsAsciiDigit(key[0])) key = "D" + key;
        if (ModifierOrder.Contains(key) || key is "LWIN" or "RWIN" ||
            !key.All(c => FrameworkApi.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new FormatException("请指定一个完整按键或鼠标动作。");
        string[] modifiers = parts.Take(parts.Length - 1).ToArray();
        if (modifiers.Distinct().Count() != modifiers.Length || modifiers.Any(item => !ModifierOrder.Contains(item)))
            throw new FormatException("只支持 Ctrl、Shift、Alt 修饰键，且不能重复。");
        string result = string.Join("+", ModifierOrder.Where(modifiers.Contains).Append(key));
        if (result is "ALT+F4" or "ALT+TAB" or "CTRL+ALT+DELETE")
            throw new FormatException("此组合保留给 Windows，不能设置为查看器快捷键。");
        return result;
    }

    public static string Display(string text) => text.Replace("MOUSEMIDDLE", "鼠标中键", StringComparison.Ordinal)
        .Replace("MOUSEX1", "鼠标侧键1", StringComparison.Ordinal).Replace("MOUSEX2", "鼠标侧键2", StringComparison.Ordinal)
        .Replace("WHEELUP", "滚轮向上", StringComparison.Ordinal).Replace("WHEELDOWN", "滚轮向下", StringComparison.Ordinal)
        .Replace("CTRL", "Ctrl", StringComparison.Ordinal).Replace("SHIFT", "Shift", StringComparison.Ordinal)
        .Replace("ALT", "Alt", StringComparison.Ordinal).Replace("OEM3", "`", StringComparison.Ordinal);
}

public sealed class ShortcutMap
{
    private readonly Dictionary<string, CommandDefinition> commands = new(StringComparer.Ordinal);
    public ShortcutMap(IEnumerable<ShortcutBinding> bindings, IReadOnlyList<LightView.Core.Settings.FolderPreset>? presets = null)
    {
        foreach (var binding in bindings)
        {
            if (!binding.Enabled || FrameworkApi.IsNullOrWhiteSpace(binding.Gesture)) continue;
            var command = CommandCatalog.Find(binding.CommandId, presets);
            if (command is null) continue;
            commands.Add(ShortcutGesture.Normalize(binding.Gesture), command);
        }
    }

    public CommandDefinition? Resolve(string gesture, bool isRepeat = false, bool textEntry = false)
    {
        if (textEntry) return null;
        CommandDefinition? command = commands.GetValueOrDefault(gesture);
        return command is not null && (!isRepeat || command.AllowRepeat) ? command : null;
    }
}

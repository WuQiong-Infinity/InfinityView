using LightView.Core.Settings;

namespace LightView.Core.Commands;

public sealed record CommandDefinition(string Id, string Name, bool AllowRepeat = false);
public sealed record ShortcutBinding(string CommandId, string Gesture, bool Enabled = true);

public static class CommandCatalog
{
    public const string Open = "image.open", Previous = "image.previous", Next = "image.next";
    public const string Fit = "view.fit", ActualPixels = "view.actualPixels", ToggleFit = "view.toggleFit";
    public const string ActualPixelsAtPointer = "view.actualPixelsAtPointer";
    public const string FullScreen = "view.fullScreen", ExitFullScreen = "view.exitFullScreen";
    public const string Settings = "settings.open", ZoomIn = "view.zoomIn", ZoomOut = "view.zoomOut";
    public const string Undo = "organizing.undo";
    public const string Details = "image.details";
    public const string Histogram = "image.histogram";
    public const string Delete = "image.delete";
    public const string Comparison = "compare.toggle", LinkComparison = "compare.link";
    public const string FocusLeft = "pane.focusLeft", FocusRight = "pane.focusRight";
    public const string CopyShootingInfo = "image.copyShootingInfo";
    public const string Recovery = "organizing.recovery", Reveal = "image.reveal", About = "app.about";

    public static IReadOnlyList<CommandDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new CommandDefinition(Open, "打开图片"), new(Previous, "上一张", true), new(Next, "下一张", true),
        new(Fit, "适配窗口"), new(ActualPixels, "100% 显示"), new(ActualPixelsAtPointer, "以光标为中心缩放到 100%"), new(ToggleFit, "适配 / 100% 切换"),
        new(FullScreen, "进入 / 退出全屏"), new(ExitFullScreen, "退出全屏"), new(Settings, "打开设置"),
        new(ZoomIn, "放大", true), new(ZoomOut, "缩小", true), new(Undo, "撤销本次会话文件操作"), new(Details, "显示 / 隐藏图片详情"),
        new(Comparison, "进入 / 退出图片对比"), new(LinkComparison, "联动缩放与位置"), new(Histogram, "显示 / 隐藏直方图"),
        new(FocusLeft, "激活左侧窗格"), new(FocusRight, "激活右侧窗格"), new(Delete, "删除图片（移入回收站）"), new(CopyShootingInfo, "复制拍摄信息"), new(Recovery, "恢复记录"), new(Reveal, "在资源管理器中显示"), new(About, "关于 InfinityView")
    });

    public static string CopyTo(string id) => "preset.copy/" + id;
    public static string MoveTo(string id) => "preset.move/" + id;
    public static bool TryPresetAction(string? command, out string presetId, out bool move)
    {
        move = command?.StartsWith("preset.move/", StringComparison.Ordinal) == true;
        bool copy = command?.StartsWith("preset.copy/", StringComparison.Ordinal) == true;
        presetId = move || copy ? command!.Substring(12) : "";
        return presetId.Length > 0 && (move || copy);
    }
    public static IReadOnlyList<CommandDefinition> Definitions(IReadOnlyList<FolderPreset> presets) =>
        All.Concat(presets.SelectMany(preset => new[]
        {
            new CommandDefinition(CopyTo(preset.Id), $"复制到「{preset.Name}」"),
            new CommandDefinition(MoveTo(preset.Id), $"移动到「{preset.Name}」")
        })).ToArray();
    public static CommandDefinition? Find(string? id, IReadOnlyList<FolderPreset>? presets = null)
    {
        var viewer = All.FirstOrDefault(item => item.Id == id);
        if (viewer is not null || presets is null) return viewer;
        return Definitions(presets).FirstOrDefault(item => item.Id == id);
    }
    public static List<ShortcutBinding> DefaultBindings(IReadOnlyList<FolderPreset>? presets = null)
    {
        List<ShortcutBinding> bindings =
    [
        new(Open, "F"), new(Previous, "LEFT"), new(Next, "RIGHT"),
        new(Fit, "MOUSEX2"), new(ActualPixels, ""), new(ActualPixelsAtPointer, "MOUSEX1"), new(ToggleFit, ""),
        new(FullScreen, "MOUSEMIDDLE"), new(ExitFullScreen, "ESCAPE"), new(Settings, "HOME"),
        new(ZoomIn, "WHEELUP"), new(ZoomOut, "WHEELDOWN"), new(Undo, "CTRL+Z"), new(Details, "TAB"),
        new(Comparison, "C"), new(LinkComparison, "X"), new(Histogram, "OEM3"), new(Reveal, "R"), new(CopyShootingInfo, "CTRL+C"), new(FocusLeft, "A"), new(FocusRight, "D"), new(Delete, "DELETE")
    ];
        foreach (var preset in presets ?? FolderPreset.Defaults())
        {
            bindings.Add(new(CopyTo(preset.Id), preset.Id == "favorites" ? "E" : ""));
            bindings.Add(new(MoveTo(preset.Id), preset.Id == "rejects" ? "Q" : ""));
        }
        return bindings;
    }
}

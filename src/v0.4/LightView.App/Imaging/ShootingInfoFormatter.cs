using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LightView.App.Imaging;

internal static class ShootingInfoFormatter
{
    private const int MaximumCharacters = 8192;
    private static readonly string[][] Groups =
    [
        ["相机厂商", "相机型号", "镜头厂商", "镜头型号"],
        ["快门", "光圈", "ISO", "焦距", "等效35mm焦距", "曝光补偿", "曝光程序", "测光模式", "闪光灯", "白平衡"]
    ];
    public static string? Format(LoadedImage? snapshot)
    {
        if (snapshot?.Metadata is not { } metadata) return null;
        var lines = new List<string>();
        for (int i = 0; i < Groups.Length; i++)
        {
            var rows = Groups[i].Select(label => metadata.Shooting.FirstOrDefault(row => row.Label == label))
                .Where(row => row is not null && Safe(row.Value)).Select(row => row!.Label + ": " + row.Value).ToArray();
            if (rows.Length > 0) { lines.Add(i == 0 ? "设备" : "曝光"); lines.AddRange(rows); }
        }
        if (metadata.Style?.Rows.Where(row => Safe(row.Value)).ToArray() is { Length: > 0 } style)
        { lines.Add("照片风格"); lines.AddRange(style.Select(row => row.Label + ": " + row.Value)); }
        string result = string.Join("\r\n", lines);
        return result.Length is > 0 and <= MaximumCharacters ? result : null;
    }
    private static bool Safe(string text) => !FrameworkApi.IsNullOrWhiteSpace(text) && text.Length <= 512 &&
        text.All(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format);

    // A replaceable writer allows tests to cover busy clipboard behavior without
    // touching the user's clipboard. The UI performs one write on its STA thread.
    internal static string Copy(LoadedImage? snapshot, Action<string> write)
    {
        string? text = Format(snapshot);
        if (text is null) return "没有可复制的拍摄信息。";
        try { write(text); return "已复制拍摄信息。"; }
        catch (Exception error) when (error is ExternalException or System.Threading.ThreadStateException)
        { return "剪贴板暂不可用，请稍后重试。"; }
    }
}

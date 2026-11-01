namespace LightView.Core.Settings;

public sealed class DetailFieldChoice
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
public sealed class DetailGroupChoice
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<DetailFieldChoice> Fields { get; set; } = new();
}
public sealed class DetailsLayout
{
    public List<DetailGroupChoice> Groups { get; set; } = new();
}
public sealed record DetailFieldDefinition(string Id, string Name);
public sealed record DetailGroupDefinition(string Id, string Name, IReadOnlyList<DetailFieldDefinition> Fields);

public static class DetailsCatalog
{
    public static IReadOnlyList<DetailGroupDefinition> Groups { get; } = new[]
    {
        new DetailGroupDefinition("file", "文件", new DetailFieldDefinition[] { new("file.name","文件名"), new("file.directory","位置"),new("file.dimensions","像素尺寸"),new("file.size","文件大小"),new("file.modified","修改时间") }),
        new DetailGroupDefinition("camera", "拍摄设备与时间", new DetailFieldDefinition[] { new("camera.model","相机型号"),new("lens.make","镜头厂商"),new("lens.model","镜头型号"),new("capture.time","拍摄时间") }),
        new DetailGroupDefinition("exposure", "曝光与拍摄参数", new DetailFieldDefinition[] { new("exposure.shutter","快门"),new("exposure.aperture","光圈"),new("exposure.iso","ISO"),new("exposure.focal","焦距（含可用等效值）"),new("exposure.compensation","曝光补偿"),new("exposure.program","曝光程序"),new("exposure.metering","测光模式"),new("exposure.flash","闪光灯"),new("exposure.whitebalance","白平衡") }),
        new DetailGroupDefinition("style", "照片风格", new DetailFieldDefinition[] {new("style.name","滤镜名称"),new("style.base","基础风格")})
    };
    public static bool IsRetired(string? id) => id is "file.format" or "camera.make";
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    public static string? Error(DetailsLayout? layout)
    {
        if (layout is null) return null;
        if (layout.Groups is null || layout.Groups.Count > 16) return "详情布局组无效或超过16组。";
        var groups = new HashSet<string>(StringComparer.Ordinal); int count = 0;
        foreach (var group in layout.Groups)
        {
            if (group is null || !ValidId(group.Id) || !groups.Add(group.Id) || group.Fields is null) return "详情布局组ID无效、重复或条目为空。";
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in group.Fields)
            {
                if (++count > 128 || field is null || !ValidId(field.Id) || !fields.Add(field.Id)) return "详情字段ID无效、重复或超过128项。";
                var owner = Groups.FirstOrDefault(g => g.Fields.Any(f => f.Id == field.Id));
                if (owner is not null && owner.Id != group.Id) return "详情字段不能放入其他组。";
            }
        }
        int effectiveGroups = layout.Groups.Count + Groups.Count(g => !groups.Contains(g.Id));
        int effectiveFields = count + Groups.Sum(g => g.Fields.Count(f => !layout.Groups.Any(actual => actual.Id == g.Id && actual.Fields.Any(af => af.Id == f.Id))));
        return effectiveGroups > 16 || effectiveFields > 128 ? "补齐内置条目后，详情布局超过数量上限。" : null;
    }
    public static DetailsLayout Normalize(DetailsLayout? source)
    {
        if (Error(source) is string error) throw new FormatException(error);
        var result = new DetailsLayout();
        if (source is not null) foreach (var group in source.Groups)
            result.Groups.Add(new() { Id=group.Id, Enabled=group.Enabled, Fields=group.Fields.Where(f=>!IsRetired(f.Id)).Select(f=>new DetailFieldChoice{Id=f.Id,Enabled=f.Enabled}).ToList() });
        foreach (var group in Groups)
        {
            var actual = result.Groups.FirstOrDefault(g=>g.Id==group.Id);
            if (actual is null) { actual=new(){Id=group.Id}; result.Groups.Add(actual); }
            foreach(var field in group.Fields) if(!actual.Fields.Any(f=>f.Id==field.Id)) actual.Fields.Add(new(){Id=field.Id});
        }
        return result;
    }
}

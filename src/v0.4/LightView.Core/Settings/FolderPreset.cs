namespace LightView.Core.Settings;

public sealed class FolderPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool CopyAdvance { get; set; }
    public bool MoveAdvance { get; set; } = true;

    public static List<FolderPreset> Defaults() =>
    [
        new() { Id = "favorites", Name = "收藏" },
        new() { Id = "rejects", Name = "废片" }
    ];
}

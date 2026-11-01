using System.ComponentModel;
using System.Runtime.CompilerServices;
using LightView.Core.Settings;
using System.IO;

namespace LightView.App.ViewModels;

public sealed class FolderPresetRow(FolderPreset source) : INotifyPropertyChanged
{
    private string name = source.Name, path = source.Path;
    private bool enabled = source.Enabled, copyAdvance = source.CopyAdvance, moveAdvance = source.MoveAdvance;
    private string pathStatus = FrameworkApi.IsNullOrWhiteSpace(source.Path) ? "未设置" : "未检查";
    public string Id { get; } = source.Id;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get => name; set { name = value; Changed(); } }
    public string Path { get => path; set { path = value; pathStatus = FrameworkApi.IsNullOrWhiteSpace(value) ? "未设置" : "未检查"; Changed(); Changed(nameof(PathStatus)); } }
    public string PathStatus => pathStatus;
    public async Task RefreshPathStatusAsync()
    {
        string snapshot = Path;
        string status;
        if (FrameworkApi.IsNullOrWhiteSpace(snapshot)) status = "未设置";
        else if (!FrameworkApi.IsPathFullyQualified(snapshot)) status = "需要绝对路径";
        else
        {
            try { status = await Task.Run(() => Directory.Exists(snapshot) ? "目录存在" : "不存在或不可访问"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { status = "不可访问"; }
        }
        if (Path != snapshot) return;
        pathStatus = status; Changed(nameof(PathStatus));
    }
    public bool Enabled { get => enabled; set { enabled = value; Changed(); } }
    public bool CopyAdvance { get => copyAdvance; set { copyAdvance = value; Changed(); } }
    public bool MoveAdvance { get => moveAdvance; set { moveAdvance = value; Changed(); } }
    public FolderPreset ToPreset() => new() { Id = Id, Name = Name, Path = Path, Enabled = Enabled,
        CopyAdvance = CopyAdvance, MoveAdvance = MoveAdvance };
    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

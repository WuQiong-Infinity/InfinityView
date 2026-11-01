using System.ComponentModel;
using LightView.Core.Commands;

namespace LightView.App.ViewModels;

public sealed class ShortcutRow(ShortcutBinding binding, CommandDefinition? definition = null) : INotifyPropertyChanged
{
    private string gesture = DisplaySafely(binding.Gesture ?? "");
    private bool enabled = binding.Enabled;
    private string name = (definition ?? CommandCatalog.Find(binding.CommandId))?.Name ?? "暂不可用的操作";
    public bool IsKnown { get; private set; } = (definition ?? CommandCatalog.Find(binding.CommandId)) is not null;
    public string CommandId { get; } = binding.CommandId;
    public bool IsAlternate { get; init; }
    public string Name => name + (IsAlternate ? "（备用）" : "");
    public bool Enabled
    {
        get => enabled;
        set { enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Gesture
    {
        get => gesture;
        set { gesture = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gesture))); }
    }
    public ShortcutBinding ToBinding() => new(CommandId,
        Enabled && IsKnown ? ShortcutGesture.Normalize(Gesture) : Gesture, Enabled);
    public void SetDefinition(CommandDefinition? definition)
    {
        IsKnown = definition is not null;
        name = definition?.Name ?? "暂不可用的操作";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
    }
    private static string DisplaySafely(string gesture)
    { try { return ShortcutGesture.Display(gesture); } catch (FormatException) { return gesture; } }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LightView.Core.Commands;

namespace LightView.App.Input;

internal static class InputRouter
{
    public static string? KeyboardGesture(KeyEventArgs e)
    {
        return KeyboardGesture(ActualKey(e), Keyboard.Modifiers);
    }

    internal static Key ActualKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

    internal static string? KeyboardGesture(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or
            Key.KanaMode or Key.JunjaMode or Key.FinalMode or Key.HanjaMode or
            Key.ImeConvert or Key.ImeNonConvert or Key.ImeAccept or Key.ImeModeChange) return null;
        // Keep standard IME toggles available even outside an editor.
        if (key == Key.Space && modifiers is ModifierKeys.Control or ModifierKeys.Shift or (ModifierKeys.Control | ModifierKeys.Shift)) return null;
        return MakeGesture(key.ToString(), modifiers);
    }

    public static string? MouseGesture(MouseButton button) => button switch
    {
        MouseButton.Middle => MakeGesture("MOUSEMIDDLE"),
        MouseButton.XButton1 => MakeGesture("MOUSEX1"),
        MouseButton.XButton2 => MakeGesture("MOUSEX2"),
        _ => null
    };

    public static string? MakeGesture(string key) => MakeGesture(key, Keyboard.Modifiers);

    internal static string? MakeGesture(string key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Windows) != 0) return null;
        string text = ((modifiers & ModifierKeys.Control) != 0 ? "CTRL+" : "") +
                      ((modifiers & ModifierKeys.Shift) != 0 ? "SHIFT+" : "") +
                      ((modifiers & ModifierKeys.Alt) != 0 ? "ALT+" : "") + key;
        try { return ShortcutGesture.Normalize(text); }
        catch (FormatException) { return null; }
    }

    public static bool IsTextEntry => IsTextEntryElement(Keyboard.FocusedElement as DependencyObject);
    internal static bool IsTextEntryElement(DependencyObject? element)
    {
        for (var current = element; current is not null; current = Parent(current))
            if (current is TextBoxBase or PasswordBox or ComboBox) return true;
        return false;
    }
    private static DependencyObject? Parent(DependencyObject element) => element switch
    {
        FrameworkContentElement content => content.Parent,
        System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D =>
            System.Windows.Media.VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element),
        _ => LogicalTreeHelper.GetParent(element)
    };
    internal static bool IsBareTextKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return false;
        Key key = ActualKey(e);
        return key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9;
    }
    public static bool IsSupported(string gesture)
    {
        string normalized;
        try { normalized = ShortcutGesture.Normalize(gesture); } catch (FormatException) { return false; }
        if (normalized.Length == 0) return true;
        string key = normalized.Split('+').Last();
        ModifierKeys modifiers = (normalized.Contains("CTRL+", StringComparison.Ordinal) ? ModifierKeys.Control : ModifierKeys.None) |
            (normalized.Contains("SHIFT+", StringComparison.Ordinal) ? ModifierKeys.Shift : ModifierKeys.None) |
            (normalized.Contains("ALT+", StringComparison.Ordinal) ? ModifierKeys.Alt : ModifierKeys.None);
        return key is "MOUSEMIDDLE" or "MOUSEX1" or "MOUSEX2" or "WHEELUP" or "WHEELDOWN" ||
               Enum.TryParse<Key>(key, true, out Key value) && FrameworkApi.IsEnumDefined(value) &&
               value.ToString().Equals(key, StringComparison.OrdinalIgnoreCase) &&
               KeyboardGesture(value, modifiers) is not null;
    }
}

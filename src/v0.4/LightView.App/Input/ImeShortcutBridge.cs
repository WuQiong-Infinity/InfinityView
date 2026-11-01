using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace LightView.App.Input;

// Window-scoped WPF input staging, not a system/global keyboard hook.
// Claim command keys before TSF; execute only on the routed preview event,
// after WPF has established IsRepeat. In particular, held Q stays one-shot.
internal sealed class ImeShortcutBridge : IDisposable
{
    private readonly Window window;
    private readonly Func<bool> commandContext;
    private readonly Func<KeyEventArgs, bool, bool> isBound;
    private readonly KeyEventHandler execute;
    private readonly InputManager manager;
    private sealed record Claim(bool Repeat);
    private ConditionalWeakTable<KeyEventArgs, Claim> claimed = new();
    private readonly HashSet<Key> heldCommands = [];
    private bool composing, disposed;

    public ImeShortcutBridge(Window window, Func<bool> commandContext, Func<KeyEventArgs, bool, bool> isBound, KeyEventHandler execute)
    {
        this.window = window; this.commandContext = commandContext; this.isBound = isBound; this.execute = execute;
        manager = InputManager.Current;
        manager.PreProcessInput += BeforeIme;
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(RoutedKeyDown), true);
        window.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused), true);
        window.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(CompositionStarted), true);
        window.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(CompositionStarted), true);
        window.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(CompositionFinished), true);
        window.Deactivated += Deactivated;
        window.Activated += Activated;
        window.Closed += Closed;
    }
    private void BeforeIme(object sender, PreProcessInputEventArgs args)
    {
        if (args.StagingItem.Input is KeyEventArgs key)
            Protect(key, Keyboard.FocusedElement as DependencyObject, window.IsActive && window.IsEnabled);
    }
    internal bool Protect(KeyEventArgs key, DependencyObject? focus, bool active)
    {
        if (disposed) return false;
        if (key.RoutedEvent == Keyboard.PreviewKeyUpEvent)
        { heldCommands.Remove(InputRouter.ActualKey(key)); return false; }
        if (disposed || key.Handled || key.RoutedEvent != Keyboard.PreviewKeyDownEvent || !active ||
            focus is null || Window.GetWindow(focus) != window || InputRouter.IsTextEntryElement(focus) ||
            composing || !commandContext()) return false;
        bool bound = isBound(key, false);
        if (!bound && !InputRouter.IsBareTextKey(key)) return false;
        if (bound)
        {
            bool repeat = !heldCommands.Add(InputRouter.ActualKey(key));
            claimed.GetValue(key, _ => new Claim(repeat));
        }
        key.Handled = true;
        return true;
    }
    private void RoutedKeyDown(object sender, KeyEventArgs key)
    {
        if (!claimed.TryGetValue(key, out var claim)) return;
        claimed.Remove(key);
        if (isBound(key, key.IsRepeat || claim.Repeat)) execute(sender, key);
    }
    private void Focused(object sender, KeyboardFocusChangedEventArgs args) => EnableFocusedIme(args.NewFocus as DependencyObject);
    internal void EnableFocusedIme(DependencyObject? focus)
    {
        if (disposed || focus is null || Window.GetWindow(focus) != window || InputRouter.IsTextEntryElement(focus) ||
            InputMethod.GetIsInputMethodSuspended(focus)) return;
        // ButtonBase disables IME by default. Keep its text service available so
        // Shift/Ctrl+Space and layout switching work without leaving LightView.
        // Do not select a keyboard layout or force Chinese/English/open state.
        InputMethod.SetIsInputMethodEnabled(focus, true);
    }
    private void CompositionStarted(object sender, TextCompositionEventArgs args) => composing = true;
    private void CompositionFinished(object sender, TextCompositionEventArgs args) => composing = false;
    private void Deactivated(object? sender, EventArgs args) => composing = false;
    private void Activated(object? sender, EventArgs args) => heldCommands.RemoveWhere(Keyboard.IsKeyUp);
    private void Closed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        manager.PreProcessInput -= BeforeIme;
        window.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(RoutedKeyDown));
        window.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused));
        window.RemoveHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(CompositionStarted));
        window.RemoveHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(CompositionStarted));
        window.RemoveHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(CompositionFinished));
        window.Deactivated -= Deactivated; window.Activated -= Activated; window.Closed -= Closed;
        claimed = new(); heldCommands.Clear();
    }
}

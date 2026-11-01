using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LightView.App.Input;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class ImeInputValidation
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var cases = new (string Name, Action Test)[]
        {
            ("language-neutral logical keys and IME/system switching chords", Gestures),
            ("routed WPF staging retains Q one-shot and D repeat behavior", Repeats),
            ("text/password/combobox/rich text ancestors keep edit keys", Editors),
            ("composition owns keys until its completion event", Composition),
            ("inactive foreign modal and menu contexts never claim viewer keys", Scope),
            ("unbound plain letters do not start targetless composition", Unbound),
            ("nontext focus enables IME without forcing its preferred state", Focus),
            ("recording context captures only its active scope and close detaches", Recording)
        };
        int failures = 0;
        foreach (var (name, test) in cases)
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
        Console.WriteLine($"RESULT {cases.Length - failures}/{cases.Length} IME routing checks; hidden windows only");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private sealed class Harness : IDisposable
    {
        public readonly Window Window;
        public readonly StackPanel Panel = new();
        public readonly Border Canvas = new() { Focusable = true };
        public readonly ImeShortcutBridge Bridge;
        public readonly List<(Key Key, bool Repeat, string? Command)> Events = [];
        public bool Context = true;
        public bool Record;
        private readonly HwndSource source;
        private readonly ShortcutMap map;
        public Harness()
        {
            var settings = new AppSettings(); map = new(settings.Bindings, settings.Presets);
            Panel.Children.Add(Canvas); Window = new() { Content = Panel };
            source = HwndSource.FromHwnd(new WindowInteropHelper(Window).EnsureHandle())!;
            Bridge = new(Window, () => Context, (e, repeat) => Record ? !repeat : Gesture(e) is string gesture && map.Resolve(gesture, repeat) is not null,
                (_, e) => Events.Add((InputRouter.ActualKey(e), e.IsRepeat, Gesture(e) is string gesture ? map.Resolve(gesture, e.IsRepeat)?.Id : null)));
        }
        private static string? Gesture(KeyEventArgs key) => InputRouter.KeyboardGesture(InputRouter.ActualKey(key), ModifierKeys.None);
        public KeyEventArgs Key(Key key, bool down = true, DependencyObject? focus = null, bool active = true)
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent, Source = Canvas };
            Bridge.Protect(e, focus ?? Canvas, active);
            InputManager.Current.ProcessInput(e);
            return e;
        }
        public T Add<T>(T element) where T : UIElement { Panel.Children.Add(element); return element; }
        public void Dispose() { Bridge.Dispose(); Window.Close(); }
    }
    private static void Gestures()
    {
        foreach (Key key in new[] { Key.A, Key.D, Key.E, Key.Q, Key.F, Key.Home, Key.Tab })
            Check(InputRouter.KeyboardGesture(key, ModifierKeys.None) is not null, "Logical viewing key disappeared");
        foreach (var (key, mods) in new[] { (Key.LeftShift, ModifierKeys.Shift), (Key.RightCtrl, ModifierKeys.Control),
            (Key.Space, ModifierKeys.Windows), (Key.Space, ModifierKeys.Control), (Key.Space, ModifierKeys.Shift),
            (Key.Space, ModifierKeys.Control | ModifierKeys.Shift), (Key.Tab, ModifierKeys.Alt), (Key.F4, ModifierKeys.Alt), (Key.ImeConvert, ModifierKeys.None) })
            Check(InputRouter.KeyboardGesture(key, mods) is null, "System/IME chord was claimed: " + key + " " + mods);
        foreach (string gesture in new[] { "Ctrl+Space", "Shift+Space", "Ctrl+Shift+Space", "ImeConvert" })
            Check(!InputRouter.IsSupported(gesture), "Settings accepted a nonfunctional IME-reserved binding");
        Check(InputRouter.IsSupported("D") && InputRouter.IsSupported("Ctrl+K") && InputRouter.IsSupported("Shift+鼠标中键"), "Valid custom bindings were lost");
    }
    private static void Repeats()
    {
        using var h = new Harness();
        h.Key(System.Windows.Input.Key.Q, false); h.Key(System.Windows.Input.Key.Q); h.Key(System.Windows.Input.Key.Q); h.Key(System.Windows.Input.Key.Q); h.Key(System.Windows.Input.Key.Q, false); h.Key(System.Windows.Input.Key.Q);
        Check(h.Events.Count == 2 && h.Events.All(e => e.Command == CommandCatalog.MoveTo("rejects")), "Held Q bypassed one-shot after early IME protection");
        h.Key(System.Windows.Input.Key.A); h.Key(System.Windows.Input.Key.A, false); h.Key(System.Windows.Input.Key.Q);
        Check(h.Events.Count(e => e.Command == CommandCatalog.MoveTo("rejects")) == 2, "Another key-up rearmed a still-held Q");
        h.Events.Clear(); h.Key(System.Windows.Input.Key.D, false); h.Key(System.Windows.Input.Key.D); h.Key(System.Windows.Input.Key.D);
        Check(h.Events.Count(e => e.Command == CommandCatalog.Next) == 2 && h.Events[1].Repeat, "D repeat was lost or dispatched twice");
    }
    private static void Editors()
    {
        using var h = new Harness();
        var text = h.Add(new TextBox()); var password = h.Add(new PasswordBox()); var combo = h.Add(new ComboBox { IsEditable = true });
        var rich = h.Add(new RichTextBox()); var run = new Run("fixture"); rich.Document.Blocks.Add(new Paragraph(run));
        foreach (DependencyObject editor in new DependencyObject[] { text, password, combo, rich, run })
        foreach (Key key in new[] { System.Windows.Input.Key.A, System.Windows.Input.Key.E, System.Windows.Input.Key.Q, System.Windows.Input.Key.Home })
        {
            Check(InputRouter.IsTextEntryElement(editor), "Text ancestor missed: " + editor.GetType().Name);
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, HwndSource.FromHwnd(new WindowInteropHelper(h.Window).Handle)!, 1, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Check(!h.Bridge.Protect(e, editor, true) && !e.Handled, "Editing key claimed as viewer command");
        }
    }
    private static void Composition()
    {
        using var h = new Harness(); var editor = h.Add(new TextBox());
        var composition = new TextComposition(InputManager.Current, editor, "ni");
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
        var key = h.Key(System.Windows.Input.Key.Q);
        Check(!key.Handled && h.Events.Count == 0, "Pending composition dispatched Q after a focus transition");
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
        h.Key(System.Windows.Input.Key.Q, false); h.Key(System.Windows.Input.Key.Q);
        Check(h.Events.Count == 1, "Completed composition left shortcuts blocked");
    }
    private static void Scope()
    {
        using var h = new Harness(); using var other = new Harness();
        Check(!h.Key(System.Windows.Input.Key.D, active: false).Handled, "Inactive window claimed input");
        Check(!h.Key(System.Windows.Input.Key.D, focus: other.Canvas).Handled, "Foreign window claimed input");
        h.Context = false; Check(!h.Key(System.Windows.Input.Key.D).Handled, "Modal/menu context claimed input");
        Check(h.Events.Count == 0, "Blocked context dispatched an action");
    }
    private static void Unbound()
    {
        using var h = new Harness(); Check(h.Key(System.Windows.Input.Key.Z).Handled && h.Events.Count == 0, "Unbound plain typing started composition or an action");
    }
    private static void Focus()
    {
        using var h = new Harness(); var button = h.Add(new Button()); var editor = h.Add(new TextBox());
        Check(!InputMethod.GetIsInputMethodEnabled(button), "Fixture no longer reflects WPF button behavior");
        h.Bridge.EnableFocusedIme(button);
        Check(InputMethod.GetIsInputMethodEnabled(button) && InputMethod.GetPreferredImeState(button) == InputMethodState.DoNotCare && !InputMethod.GetIsInputMethodSuspended(button), "Focus forced language state or suspended IME");
        InputMethod.SetIsInputMethodEnabled(editor, false); h.Bridge.EnableFocusedIme(editor);
        Check(!InputMethod.GetIsInputMethodEnabled(editor), "Explicit editor input policy overwritten");
    }
    private static void Recording()
    {
        using var h = new Harness { Record = true, Context = false };
        h.Key(System.Windows.Input.Key.K); Check(h.Events.Count == 0, "Inactive recorder took a key");
        h.Context = true; h.Key(System.Windows.Input.Key.K, false); h.Key(System.Windows.Input.Key.K);
        Check(h.Events.Count == 1 && h.Events[0].Key == System.Windows.Input.Key.K, "Active recorder missed a letter");
        h.Bridge.Dispose(); h.Key(System.Windows.Input.Key.K, false); Check(!h.Key(System.Windows.Input.Key.K).Handled && h.Events.Count == 1, "Disposed bridge retained input ownership");
    }

    // Explicit synthetic-only UI harness. Records IME/focus/image state, never
    // typed text or key history. Self-closes within one minute.
    public static int Interactive(string fixture, string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(root, "userdata"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Colors", "Typography", "Controls", "Icons" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/LightView;component/Styles/{name}.xaml") });
        var window = new MainWindow(); var frame = new DispatcherFrame(); var states = new List<object>(); string? previous = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var started = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            var focus = Keyboard.FocusedElement as DependencyObject; var viewer = (ViewerViewModel)window.DataContext;
            var state = new { language = InputLanguageManager.Current.CurrentInputLanguage.Name, ime = InputMethod.Current.ImeState.ToString(),
                focus = focus?.GetType().Name, enabled = focus is not null && InputMethod.GetIsInputMethodEnabled(focus),
                windowActive = window.IsActive, image = Path.GetFileName(viewer.DisplayedImage?.FilePath), viewer.ErrorMessage };
            string key = JsonSerializer.Serialize(state);
            if (key != previous) { previous = key; states.Add(new { time = DateTimeOffset.UtcNow, state }); File.WriteAllText(Path.Combine(root, "states.json"), JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true })); }
            if ((DateTime.UtcNow - started).TotalSeconds > 55) window.Close();
        };
        window.Closed += (_, _) => { timer.Stop(); frame.Continue = false; };
        window.Show(); timer.Start();
        window.Dispatcher.InvokeAsync(async () => await window.OpenExternalAsync(Path.GetFullPath(fixture)));
        Dispatcher.PushFrame(frame); return 0;
    }
}

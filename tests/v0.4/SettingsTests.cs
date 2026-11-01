using System.IO;
using System.Text.Json;
using LightView.App.Persistence;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class SettingsTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("specified default keys resolve to the intended actions", Defaults),
        ("pointer 100 percent default migrates without replacing user bindings", () => PointerDefault(root)),
        ("gesture aliases and modifier order normalize consistently", Normalization),
        ("reserved system combinations cannot be assigned", Reserved),
        ("text entry and held one-shot keys do not dispatch", ContextAndRepeat),
        ("rebinding removes the old shortcut immediately", Rebinding),
        ("duplicate enabled gestures are rejected", Conflicts),
        ("settings editing clone does not change live state", EditingClone),
        ("settings persist and reload custom bindings", () => Persist(root)),
        ("corrupt primary recovers backup and preserves originals", () => Recovery(root)),
        ("future schema cannot be overwritten", () => FutureSchema(root)),
        ("external changes prevent stale settings overwrite", () => ExternalChange(root)),
        ("unknown fields and unavailable commands survive saving", () => UnknownData(root)),
        ("UTF8 BOM settings remain readable", () => Bom(root))
    ];
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static string DirectoryFor(string root, string name)
    {
        string directory = Path.Combine(root, "settings-" + name);
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static void Defaults()
    {
        var map = new ShortcutMap(CommandCatalog.DefaultBindings());
        Check(map.Resolve("A")?.Id == CommandCatalog.Previous, "A default differs");
        Check(map.Resolve("D")?.Id == CommandCatalog.Next, "D default differs");
        Check(map.Resolve("F")?.Id == CommandCatalog.Open, "F default differs");
        Check(map.Resolve("HOME")?.Id == CommandCatalog.Settings, "Home default differs");
        Check(map.Resolve("MOUSEMIDDLE")?.Id == CommandCatalog.FullScreen, "middle default differs");
        Check(map.Resolve("MOUSEX2")?.Id == CommandCatalog.ActualPixelsAtPointer, "side2 default differs");
    }
    private static void PointerDefault(string root)
    {
        foreach(string mode in new[]{"missing","occupied","cleared","custom"})
        {
            string dir=DirectoryFor(root,"pointer-"+mode);var settings=new AppSettings();
            settings.Bindings.RemoveAll(b=>b.CommandId==CommandCatalog.ActualPixelsAtPointer);
            if(mode=="occupied")settings.Bindings.Add(new(CommandCatalog.Next,"鼠标侧键2"));
            if(mode=="cleared")settings.Bindings.Add(new(CommandCatalog.ActualPixelsAtPointer,""));
            if(mode=="custom")settings.Bindings.Add(new(CommandCatalog.ActualPixelsAtPointer,"I"));
            File.WriteAllText(Path.Combine(dir,"settings.json"),LegacyJson(settings));
            var loaded=new SettingsStore(dir).Load();Check(!loaded.IsReadOnly,"Migration invalidated settings");
            var rows=loaded.Settings.Bindings.Where(b=>b.CommandId==CommandCatalog.ActualPixelsAtPointer).ToArray();
            Check(mode=="occupied"?rows.Length==0:rows.Length==1&&rows[0].Gesture==(mode=="missing"?"MOUSEX2":mode=="custom"?"I":""),"Replaced explicit binding or stole Side2");
        }
        var bindings=CommandCatalog.DefaultBindings();var map=new ShortcutMap(bindings);
        Check(map.Resolve("MOUSEX2",true)==null&&map.Resolve("MOUSEX2",textEntry:true)==null,"Repeat/text-entry dispatch not suppressed");
        bindings.RemoveAll(b=>b.CommandId==CommandCatalog.ActualPixelsAtPointer);bindings.Add(new(CommandCatalog.ActualPixelsAtPointer,"I"));map=new(bindings);
        Check(map.Resolve("MOUSEX2")==null&&map.Resolve("I")?.Id==CommandCatalog.ActualPixelsAtPointer,"Pointer action not rebindable");
    }
    private static void Normalization()
    {
        Check(ShortcutGesture.Normalize("Alt+ctrl+鼠标中键") == "CTRL+ALT+MOUSEMIDDLE", "alias/order differs");
        Check(ShortcutGesture.Normalize("Ctrl+1") == "CTRL+D1", "number row mapping differs");
        Check(ShortcutGesture.Normalize("Esc") == "ESCAPE", "escape alias differs");
    }
    private static void Reserved()
    {
        foreach (string input in new[] { "Alt+F4", "Alt+Tab", "Ctrl+Alt+Delete", "Ctrl+Ctrl+A", "Ctrl" })
        {
            bool failed = false;
            try { ShortcutGesture.Normalize(input); } catch (FormatException) { failed = true; }
            Check(failed, "reserved/invalid input accepted: " + input);
        }
    }
    private static void ContextAndRepeat()
    {
        var map = new ShortcutMap(CommandCatalog.DefaultBindings());
        Check(map.Resolve("A", textEntry: true) is null, "typing dispatched navigation");
        Check(map.Resolve("HOME", isRepeat: true) is null, "held Home repeated settings");
        Check(map.Resolve("F", isRepeat: true) is null, "held F repeated open");
        Check(map.Resolve("D", isRepeat: true)?.Id == CommandCatalog.Next, "held navigation was disabled");
    }
    private static void Rebinding()
    {
        var map = new ShortcutMap([new(CommandCatalog.Open, "K")]);
        Check(map.Resolve("F") is null && map.Resolve("K")?.Id == CommandCatalog.Open, "old binding still active");
    }
    private static void Conflicts()
    {
        var settings = new AppSettings { Bindings = [new(CommandCatalog.Previous, "A"), new(CommandCatalog.Open, "a")] };
        Check(SettingsValidation.Error(settings) is not null, "duplicate gesture accepted");
    }
    private static void EditingClone()
    {
        var original = new AppSettings(); var copy = original.Clone();
        copy.Bindings.Clear(); copy.ZoomStep = 2;
        Check(original.Bindings.Count > 0 && original.ZoomStep == 1.2, "editor changed original");
    }
    private static void Persist(string root)
    {
        string directory = DirectoryFor(root, "persist");
        var store = new SettingsStore(directory);
        var settings = store.Load().Settings;
        settings.Bindings = [new(CommandCatalog.Open, "K")]; settings.ZoomStep = 1.5;
        store.Save(settings);
        var loaded = new SettingsStore(directory).Load();
        Check(loaded.Settings.ZoomStep == 1.5 && new ShortcutMap(loaded.Settings.Bindings).Resolve("K")?.Id == CommandCatalog.Open, "saved binding was lost");
    }
    private static void Recovery(string root)
    {
        string directory = DirectoryFor(root, "recovery");
        var store = new SettingsStore(directory); var settings = store.Load().Settings;
        settings.ZoomStep = 1.5; store.Save(settings);
        settings.ZoomStep = 2; store.Save(settings);
        File.WriteAllText(store.FilePath, "broken JSON");
        var recoveredStore = new SettingsStore(directory); var recovered = recoveredStore.Load();
        Check(recovered.Warning is not null && recovered.Settings.ZoomStep == 1.5, "good backup not recovered");
        recoveredStore.Save(recovered.Settings);
        Check(Directory.EnumerateFiles(directory, "settings.json.corrupt-*").Any(path => File.ReadAllText(path) == "broken JSON"), "corrupt original was discarded");
        Check(new SettingsStore(directory).Load().Settings.ZoomStep == 1.5, "recovered save failed");
    }
    private static void FutureSchema(string root)
    {
        string directory = DirectoryFor(root, "future"); string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":99,\"futureOption\":true}");
        string before = File.ReadAllText(path);
        var store = new SettingsStore(directory); var result = store.Load();
        bool failed = false; try { store.Save(new AppSettings()); } catch (InvalidOperationException) { failed = true; }
        Check(result.IsReadOnly && failed && File.ReadAllText(path) == before, "future schema was overwritten");
    }
    private static void ExternalChange(string root)
    {
        string directory = DirectoryFor(root, "external"); var seed = new SettingsStore(directory);
        seed.Save(seed.Load().Settings);
        var first = new SettingsStore(directory); var second = new SettingsStore(directory);
        var a = first.Load().Settings; var b = second.Load().Settings;
        a.ZoomStep = 1.8; first.Save(a); b.ZoomStep = 2;
        bool failed = false; try { second.Save(b); } catch (IOException) { failed = true; }
        Check(failed && new SettingsStore(directory).Load().Settings.ZoomStep == 1.8, "stale editor overwrote external change");
    }
    private static void UnknownData(string root)
    {
        string directory = DirectoryFor(root, "unknown"); string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"futureOption\":{\"enabled\":true},\"bindings\":[{\"commandId\":\"future.action\",\"gesture\":\"A\",\"enabled\":true}]}");
        var store = new SettingsStore(directory); var loaded = store.Load();
        Check(new ShortcutMap(loaded.Settings.Bindings).Resolve("A") is null, "unknown command executed");
        store.Save(loaded.Settings.Clone());
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Check(document.RootElement.GetProperty("futureOption").GetProperty("enabled").GetBoolean(), "unknown field was removed");
        Check(document.RootElement.GetProperty("bindings")[0].GetProperty("commandId").GetString() == "future.action", "unknown binding was removed");
    }
    internal static string LegacyJson(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node["schemaVersion"] = 2; node.Remove("appearance"); node.Remove("window");
        return node.ToJsonString();
    }
    private static void Bom(string root)
    {
        string directory = DirectoryFor(root, "bom");
        File.WriteAllBytes(Path.Combine(directory, "settings.json"), [239, 187, 191, .. System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"zoomStep\":1.5}")]);
        Check(new SettingsStore(directory).Load().Settings.ZoomStep == 1.5, "BOM configuration rejected");
    }
}

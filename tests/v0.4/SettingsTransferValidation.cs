using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Persistence;
using LightView.App.ViewModels;
using LightView.App.Views.Settings;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class SettingsTransferValidation
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var app = new LightView.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var cases = new (string Name, Action Test)[]
        {
            ("export only supported configuration with optional target paths", () => Export(root)),
            ("export refuses existing backup and active settings without changing bytes", () => NoOverwrite(root)),
            ("invalid future duplicate missing and oversized packages are rejected", () => Invalid(root)),
            ("unavailable targets are disabled without creating directories", () => Targets(root)),
            ("selected groups replace exactly and same names never retarget preset IDs", () => Groups(root)),
            ("import stores byte-exact pre-import and normal backups", () => Backup(root)),
            ("failed import preserves disk draft preview and last applied baseline", () => Failure(root)),
            ("cancelled import preview writes nothing and can require group selection", () => Cancel(root)),
            ("unavailable future gesture remains visible without execution", UnknownGesture)
            ,("settings editor import commit reloads controls and failed retry preserves draft", () => Editor(root))
        };
        int failures = 0;
        foreach (var item in cases)
        {
            try { item.Test(); Console.WriteLine("PASS " + item.Name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        }
        Console.WriteLine($"RESULT {cases.Length - failures}/{cases.Length} settings transfer checks passed");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    private static string Dir(string root, string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static AppSettings Sample(string root)
    {
        var result = new AppSettings { ZoomStep = 1.7, Bindings = [new(CommandCatalog.Open, "K"), new(CommandCatalog.CopyTo("new-trip"), "E")],
            Presets = [new FolderPreset { Id = "new-trip", Name = "旅行", Path = Path.Combine(root, "missing-target") }] };
        result.Appearance.DetailsFontSize = 22;
        result.UnknownFields = new() { ["testExecutionPath"] = JsonDocument.Parse("\"NEVER-EXPORT-EXTENSION\"").RootElement.Clone() };
        result.Window.RestoreBounds = new(100, 100, 800, 600); result.Window.MonitorDeviceName = "NEVER-EXPORT-MONITOR"; result.Window.SavedDpiScale = 1.5;
        return result;
    }
    private static string Package(string directory, string name = "package.json")
    {
        string path = Path.Combine(directory, name); SettingsTransfer.Export(path, Sample(directory), true, Path.Combine(directory, "active-settings.json")); return path;
    }
    private static void Export(string root)
    {
        string dir = Dir(root, "export"); var settings = Sample(dir); string path = Path.Combine(dir, "full.json");
        SettingsTransfer.Export(path, settings, true, Path.Combine(dir, "settings.json"));
        string json = File.ReadAllText(path);
        Check(json.Contains("LightView.settings") && !json.Contains("NEVER-EXPORT") && !json.Contains("testExecutionPath"), "Unknown machine data leaked.");
        Check(SettingsTransfer.Read(path).Settings.Appearance.DetailsFontSize == 22 && settings.Presets[0].Enabled, "Export mutated source or lost settings.");
        string publicPath = Path.Combine(dir, "no-paths.json"); SettingsTransfer.Export(publicPath, settings, false, Path.Combine(dir, "settings.json"));
        var preview = SettingsTransfer.Read(publicPath);
        Check(!preview.IncludesTargetPaths && preview.Settings.Presets[0].Path == "" && !preview.Settings.Presets[0].Enabled, "Path-free package kept a target.");
    }
    private static void NoOverwrite(string root)
    {
        string dir = Dir(root, "no-overwrite"), path = Path.Combine(dir, "settings.json"); File.WriteAllText(path, "keep-active");
        foreach (string active in new[] { path, Path.Combine(dir, "other.json") })
        {
            bool refused = false; try { SettingsTransfer.Export(path, Sample(dir), true, active); } catch (IOException) { refused = true; }
            Check(refused && File.ReadAllText(path) == "keep-active", "Export overwrote an existing file.");
        }
        Check(!Directory.EnumerateFiles(dir, "*.tmp").Any(), "Export leaked a temporary.");
    }
    private static void Invalid(string root)
    {
        string dir = Dir(root, "invalid"), valid = Package(dir); string original = File.ReadAllText(valid);
        var changes = new Action<JsonObject>[]
        {
            n => n["packageVersion"] = 9,
            n => n["settings"]!["schemaVersion"] = 99,
            n => n["settings"]!["appearance"]!["detailsFontSize"] = 25,
            n => n["settings"]!["presets"]![0]!.AsObject().Remove("id"),
            n => n["settings"]!["appearance"] = null,
            n => n["settings"]!["bindings"]![1]!["gesture"] = "K",
            n => n["settings"]!["presets"]!.AsArray().Add(n["settings"]!["presets"]![0]!.DeepClone())
        };
        int index = 0;
        foreach (var change in changes)
        {
            var node = JsonNode.Parse(original)!.AsObject(); change(node); string path = Path.Combine(dir, $"invalid-{index++}.json"); File.WriteAllText(path, node.ToJsonString());
            bool rejected = false; try { SettingsTransfer.Read(path); } catch (FormatException) { rejected = true; }
            Check(rejected, "Invalid package accepted: " + path);
        }
        string duplicate = Path.Combine(dir, "duplicate.json"); File.WriteAllText(duplicate, original.Replace("\"format\": \"LightView.settings\"", "\"format\": \"LightView.settings\", \"FORMAT\": \"LightView.settings\""));
        bool duplicateRejected = false; try { SettingsTransfer.Read(duplicate); } catch (FormatException) { duplicateRejected = true; } Check(duplicateRejected, "Duplicate JSON field accepted.");
        string huge = Path.Combine(dir, "huge.json"); File.WriteAllBytes(huge, new byte[1024 * 1024 + 1]);
        bool hugeRejected = false; try { SettingsTransfer.Read(huge); } catch (IOException) { hugeRejected = true; } Check(hugeRejected, "Oversized input accepted.");
    }
    private static void Targets(string root)
    {
        string dir = Dir(root, "targets"), path = Package(dir); var preview = SettingsTransfer.Read(path);
        Check(preview.Warnings.Count == 1 && !preview.Settings.Presets[0].Enabled && !Directory.Exists(preview.Settings.Presets[0].Path), "Unavailable target was executed or left enabled.");
    }
    private static void Groups(string root)
    {
        string dir = Dir(root, "groups"); var incoming = SettingsTransfer.Read(Package(dir));
        var baseline = new AppSettings { ZoomStep = 2, Presets = [new FolderPreset { Id = "old-trip", Name = "旅行" }], Bindings = [new(CommandCatalog.CopyTo("old-trip"), "E")] };
        baseline.Window.RestoreBounds = new(20, 30, 900, 700);
        baseline.UnknownFields = new() { ["keepRoot"] = JsonDocument.Parse("1").RootElement.Clone() };
        var appearance = SettingsTransfer.Merge(baseline, incoming, SettingsGroups.Appearance);
        Check(appearance.Appearance.DetailsFontSize == 22 && appearance.ZoomStep == 2 && appearance.Bindings[0].CommandId.EndsWith("old-trip") && appearance.UnknownFields!.ContainsKey("keepRoot"), "Unselected group changed.");
        var shortcuts = SettingsTransfer.Merge(baseline, incoming, SettingsGroups.Shortcuts);
        Check(new ShortcutMap(shortcuts.Bindings, shortcuts.Presets).Resolve("E") is null, "Same-name preset was retargeted.");
        var view = SettingsTransfer.Merge(baseline, incoming, SettingsGroups.Viewing);
        Check(view.Window.RestoreBounds == baseline.Window.RestoreBounds, "Import restored machine-specific coordinates.");
    }
    private static void Backup(string root)
    {
        string dir = Dir(root, "backup"); var seed = new SettingsStore(dir); seed.Save(seed.Load().Settings);
        byte[] original = [239, 187, 191, .. File.ReadAllBytes(seed.FilePath)]; File.WriteAllBytes(seed.FilePath, original);
        var store = new SettingsStore(dir); var current = store.Load().Settings; current.Appearance.DetailsFontSize = 22; store.SaveImported(current);
        Check(Directory.GetFiles(dir, "settings.pre-import.*.json").Any(p => File.ReadAllBytes(p).SequenceEqual(original)) && File.ReadAllBytes(seed.FilePath + ".bak").SequenceEqual(original), "Pre-import original bytes not retained.");
        Check(new SettingsStore(dir).Load().Settings.Appearance.DetailsFontSize == 22, "Imported data not committed.");
    }
    private static void Failure(string root)
    {
        string dir = Dir(root, "failure"); var store = new SettingsStore(dir); var original = store.Load().Settings; store.Save(original);
        double shown = 16; var session = new SettingsSession(original, a => shown = a.DetailsFontSize, false);
        session.Draft.Appearance.DetailsFontSize = 21; session.PreviewAppearance();
        var candidate = original.Clone(); candidate.Appearance.DetailsFontSize = 22;
        string external = File.ReadAllText(store.FilePath) + " "; File.WriteAllText(store.FilePath, external);
        string? error = session.ApplyAsync(candidate, value => { store.SaveImported(value); return Task.FromResult<string?>(null); }, true).GetAwaiter().GetResult();
        Check(error is not null && File.ReadAllText(store.FilePath) == external && session.Draft.Appearance.DetailsFontSize == 21 && shown == 21 && session.LastAppliedSnapshot.Appearance.DetailsFontSize == 16, "Failed import changed active state.");
        Check(Directory.GetFiles(dir, "settings.pre-import.*.json").Length == 0, "Stale import wrote a backup before fingerprint refusal.");
    }
    private static void Cancel(string root)
    {
        string dir = Dir(root, "cancel"), path = Package(dir); string[] before = Directory.GetFiles(dir);
        var window = new SettingsImportWindow(path, new AppSettings(), SettingsTransfer.Read(path));
        var panel = (DockPanel)window.Content;
        var content = (StackPanel)panel.Children.OfType<ScrollViewer>().Single().Content;
        foreach (var box in content.Children.OfType<CheckBox>()) box.IsChecked = false;
        bool refused = false; try { window.BuildSelection(); } catch (FormatException) { refused = true; } Check(refused, "No group selection allowed apply.");
        content.Children.OfType<CheckBox>().First().IsChecked = true;
        window.Content = null; var border = new Border { Child = panel, Background = (Brush)Application.Current.FindResource("WindowBrush") };
        TextBlock.SetForeground(border, (Brush)Application.Current.FindResource("TextBrush"));
        border.Measure(new Size(644, 541)); border.Arrange(new Rect(0, 0, 644, 541)); border.UpdateLayout();
        var bitmap = new RenderTargetBitmap(644, 541, 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(root, "import-preview.png"))) encoder.Save(file);
        window.Close();
        Check(window.SelectedSettings is null && Directory.GetFiles(dir).OrderBy(value => value).SequenceEqual(before.OrderBy(value => value)), "Cancel wrote config or backup files.");
    }
    private static void UnknownGesture()
    {
        var row = new ShortcutRow(new("future.action", "FUTURE(GESTURE)"));
        Check(!row.IsKnown && row.Gesture == "FUTURE(GESTURE)" && row.ToBinding().Gesture == "FUTURE(GESTURE)", "Unknown gesture crashed or changed.");
    }
    private static void Editor(string root)
    {
        string dir = Dir(root, "editor"); var store = new SettingsStore(dir); var initial = store.Load().Settings; store.Save(initial);
        double shown = 16;
        var window = new SettingsWindow(initial, _ => Task.FromResult<string?>(null), null, false,
            appearancePreview: a => shown = a.DetailsFontSize,
            applyImported: candidate => { store.SaveImported(candidate); return Task.FromResult<string?>(null); });
        var imported = initial.Clone(); imported.Appearance.DetailsFontSize = 22; imported.ZoomStep = 1.7;
        Check(window.CommitImportedAsync(imported).GetAwaiter().GetResult() is null, "Editor import commit failed.");
        var page = (AppearancePage)window.FindName("AppearanceEditor"); var font = (Slider)page.FindName("FontSlider");
        Check(font.Value == 22 && ((TextBox)window.FindName("ZoomStepBox")).Text == "1.7" && shown == 22 && !window.IsSaving, "Successful import did not reload UI.");
        font.Value = 23;
        string exported = Path.Combine(dir, "applied-only.json"); SettingsTransfer.Export(exported, window.AppliedSnapshot, false, store.FilePath);
        Check(SettingsTransfer.Read(exported).Settings.Appearance.DetailsFontSize == 22, "Export included unsaved appearance draft.");
        string external = File.ReadAllText(store.FilePath) + " "; File.WriteAllText(store.FilePath, external);
        imported.Appearance.DetailsFontSize = 24;
        Check(window.CommitImportedAsync(imported).GetAwaiter().GetResult() is not null && font.Value == 23 && shown == 23 && window.AppliedSnapshot.Appearance.DetailsFontSize == 22 && File.ReadAllText(store.FilePath) == external, "Failed import changed draft, preview, baseline or disk.");
        window.Close(); Check(shown == 22, "Close after failed import lost last successful appearance.");
    }
}

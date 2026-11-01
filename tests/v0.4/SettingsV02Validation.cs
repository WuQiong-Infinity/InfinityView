using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Persistence;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class SettingsV02Validation
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var cases = new (string Name, Action Test)[]
        {
            ("schema2 migration preserves bindings presets unknown fields and byte-exact backup", () => Migration(root)),
            ("legacy reserved-field collision is backed up and stays read only", () => Collision(root)),
            ("new schema and malformed values never overwrite the original", () => Invalid(root)),
            ("cancel restores last successful appearance after apply and re-edit", Cancel),
            ("failed save keeps draft and restores last success on close", () => Failure(root)),
            ("saving freezes its snapshot and refuses close or duplicate apply", Saving),
            ("reset appearance preserves other groups and unknown appearance fields", Reset),
            ("checkerboard stays below image and never changes source pixels", Checker)
        }.Concat(SettingsTests.Cases(root)).Concat(PresetTests.Cases(root)).Concat(DetailsNavigationTests.Cases(root)).ToArray();
        int failures = 0;
        foreach (var item in cases)
        {
            try { item.Test(); Console.WriteLine("PASS " + item.Name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        }
        Console.WriteLine($"RESULT {cases.Length - failures}/{cases.Length} v0.2 settings checks passed");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    private static string DirectoryFor(string root, string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static void Migration(string root)
    {
        string dir = DirectoryFor(root, "schema2"), path = Path.Combine(dir, "settings.json");
        string target = Path.Combine(dir, "not-created-photo-target");
        var old = new AppSettings { ShowColorInfo = true, Bindings = [new(CommandCatalog.Open, "K"), new(CommandCatalog.Details, ""), new(CommandCatalog.ActualPixelsAtPointer, "I")],
            Presets = [new FolderPreset { Id = "stable-target", Name = "保留的目标", Path = target, Enabled = false, CopyAdvance = true }],
            UnknownFields = new() { ["futureData"] = JsonDocument.Parse("{\"enabled\":true}").RootElement.Clone() } };
        byte[] original = [239, 187, 191, .. Encoding.UTF8.GetBytes(SettingsTests.LegacyJson(old))]; File.WriteAllBytes(path, original);
        var store = new SettingsStore(dir); var result = store.Load();
        Check(!result.IsReadOnly && result.Settings.SchemaVersion == 3 && File.ReadAllBytes(path).SequenceEqual(original), "Load mutated legacy config.");
        Check(result.Settings.ShowColorInfo && result.Settings.Bindings.SequenceEqual(old.Bindings) && result.Settings.Presets[0].Id == "stable-target" && !result.Settings.Presets[0].Enabled, "Migration changed user choices.");
        store.Save(result.Settings);
        string backup = Directory.GetFiles(dir, "settings.pre-v0.2.*.json").Single();
        Check(File.ReadAllBytes(backup).SequenceEqual(original) && File.ReadAllBytes(path + ".bak").SequenceEqual(original), "Original bytes not recoverable.");
        var loaded = new SettingsStore(dir).Load().Settings;
        Check(loaded.UnknownFields!["futureData"].GetProperty("enabled").GetBoolean() && loaded.Appearance.DetailsFontSize == 16 && !Directory.Exists(target), "Unknown field lost or preset path executed.");
        string oldDefaultDir = DirectoryFor(root, "schema2-default"); File.WriteAllText(Path.Combine(oldDefaultDir, "settings.json"), "{\"schemaVersion\":2}");
        Check(new SettingsStore(oldDefaultDir).Load().Settings.ShowColorInfo && !new AppSettings().ShowColorInfo, "Legacy/new diagnostic defaults not separated.");
    }
    private static void Collision(string root)
    {
        string dir = DirectoryFor(root, "collision"), path = Path.Combine(dir, "settings.json");
        byte[] original = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"appearance\":{\"oldPlugin\":42},\"showColorInfo\":false}"); File.WriteAllBytes(path, original);
        var store = new SettingsStore(dir); var loaded = store.Load(); bool refused = false;
        try { store.Save(loaded.Settings); } catch (InvalidOperationException) { refused = true; }
        Check(loaded.IsReadOnly && refused && !loaded.Settings.ShowColorInfo && File.ReadAllBytes(path).SequenceEqual(original), "Legacy collision overwrote source.");
        Check(Directory.GetFiles(dir, "settings.pre-v0.2.*.json").Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "Conflict lost complete original backup.");
    }
    private static void Invalid(string root)
    {
        string dir = DirectoryFor(root, "future"), path = Path.Combine(dir, "settings.json");
        string original = "{\"schemaVersion\":99,\"appearance\":\"future-layout\"}"; File.WriteAllText(path, original);
        var store = new SettingsStore(dir); Check(store.Load().IsReadOnly, "Future layout treated as corrupt writable data.");
        bool refused = false; try { store.Save(new()); } catch (InvalidOperationException) { refused = true; }
        Check(refused && File.ReadAllText(path) == original, "Future config overwritten.");
        var settings = new AppSettings();
        foreach (double size in new[] { 11, 25, double.NaN, double.PositiveInfinity }) { settings.Appearance.DetailsFontSize = size; Check(SettingsValidation.Error(settings) is not null, "Invalid font accepted."); }
        settings.Appearance.DetailsFontSize = 16; settings.Appearance.DetailsOutlineThickness = 3.1;
        Check(SettingsValidation.Error(settings) is not null, "Invalid outline accepted.");
    }
    private static void Cancel()
    {
        double shown = 16;
        var session = new SettingsSession(new(), a => shown = a.DetailsFontSize, false);
        session.Draft.Appearance.DetailsFontSize = 20; session.PreviewAppearance(); Check(shown == 20, "No preview.");
        Check(session.TryClose() && shown == 16, "Cancel did not restore baseline.");
        session.Draft.Appearance.DetailsFontSize = 18;
        Check(session.ApplyAsync(session.Draft, _ => Task.FromResult<string?>(null)).GetAwaiter().GetResult() is null, "Apply failed.");
        session.Draft.Appearance.DetailsFontSize = 24; session.PreviewAppearance();
        Check(session.TryClose() && shown == 18, "Re-edit cancel restored original open value instead of last success.");
    }
    private static void Failure(string root)
    {
        string dir = DirectoryFor(root, "external-failure"); var store = new SettingsStore(dir); var settings = store.Load().Settings; store.Save(settings);
        double shown = 16; var session = new SettingsSession(settings, a => shown = a.DetailsFontSize, false);
        session.Draft.Appearance.DetailsFontSize = 21; session.PreviewAppearance();
        string external = File.ReadAllText(store.FilePath) + " "; File.WriteAllText(store.FilePath, external);
        string? error = session.ApplyAsync(session.Draft, value => { store.Save(value); return Task.FromResult<string?>(null); }).GetAwaiter().GetResult();
        Check(error is not null && session.Draft.Appearance.DetailsFontSize == 21 && File.ReadAllText(store.FilePath) == external, "Failed save changed disk/draft or reported success.");
        Check(session.TryClose() && shown == 16, "Failed save changed last-applied baseline.");
        var readOnly = new SettingsSession(settings, a => shown = a.DetailsFontSize, true);
        readOnly.Draft.Appearance.DetailsFontSize = 24; readOnly.PreviewAppearance();
        Check(readOnly.ApplyAsync(readOnly.Draft, _ => throw new Exception("Must not write")).GetAwaiter().GetResult() is not null && readOnly.TryClose() && shown == 16, "Read-only preview/save semantics wrong.");
    }
    private static void Saving()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SettingsSession(new(), _ => { }, false); var candidate = session.Draft.Clone(); candidate.Appearance.DetailsFontSize = 19;
        double savedSize = 0;
        var pending = session.ApplyAsync(candidate, async snapshot => { await gate.Task; savedSize = snapshot.Appearance.DetailsFontSize; return null; });
        candidate.Appearance.DetailsFontSize = 24;
        Check(session.IsApplying && !session.TryClose() && session.ApplyAsync(candidate, _ => Task.FromResult<string?>(null)).GetAwaiter().GetResult() is not null, "Save allowed close or duplicate.");
        gate.SetResult(); Check(pending.GetAwaiter().GetResult() is null && savedSize == 19 && session.Draft.Appearance.DetailsFontSize == 19, "In-flight snapshot was mutated.");
    }
    private static void Reset()
    {
        var settings = new AppSettings { ZoomStep = 1.8 }; settings.Presets[0].Path = "C:\\never-create-this-target";
        settings.Appearance.UnknownFields = new() { ["futureAppearance"] = JsonDocument.Parse("7").RootElement.Clone() };
        var session = new SettingsSession(settings, _ => { }, false); session.Draft.Appearance.DetailsFontSize = 23; session.ResetAppearance();
        Check(session.Draft.Appearance.DetailsFontSize == 16 && session.Draft.ZoomStep == 1.8 && session.Draft.Presets[0].Path == settings.Presets[0].Path &&
            session.Draft.Bindings.SequenceEqual(settings.Bindings) && session.Draft.Appearance.UnknownFields!["futureAppearance"].GetInt32() == 7, "Reset touched another group or removed unknown data.");
    }
    private static void Checker()
    {
        byte[] sourcePixels = new byte[32 * 32 * 4];
        var source = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, sourcePixels, 128); source.Freeze();
        var viewport = new ImageViewport { Source = source, Background = Brushes.Red, TransparencyCheckerboard = true };
        viewport.Measure(new Size(128, 96)); viewport.Arrange(new Rect(0, 0, 128, 96)); viewport.UpdateLayout();
        var rendered = new RenderTargetBitmap(128, 96, 96, 96, PixelFormats.Pbgra32); rendered.Render(viewport);
        byte[] pixels = new byte[128 * 96 * 4]; rendered.CopyPixels(pixels, 512, 0);
        Check(pixels[0] == 0 && pixels[1] == 0 && pixels[2] == 255, "Checker painted outside the image.");
        int center = (48 * 128 + 64) * 4; Check(pixels[center] == pixels[center + 1] && pixels[center + 1] == pixels[center + 2], "Checker missing under transparent image.");
        byte[] after = new byte[sourcePixels.Length]; source.CopyPixels(after, 128, 0);
        Check(ReferenceEquals(viewport.Source, source) && after.SequenceEqual(sourcePixels), "Appearance changed image source pixels.");
    }
}

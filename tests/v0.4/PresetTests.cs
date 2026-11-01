using System.IO;
using LightView.App.Persistence;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class PresetTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("two defaults have four independent commands and exact E Q actions", Defaults),
        ("preset actions cannot auto-repeat", RepeatGuard),
        ("rename and reorder never retarget existing bindings", StableIdentity),
        ("third preset adds copy and move without hardcoded slots", ThirdPreset),
        ("deleted preset bindings cannot target another folder", RemovedPreset),
        ("invalid or duplicate preset identities are rejected", InvalidIdentity),
        ("saving preset paths does not create target directories", () => SaveOnlyConfiguration(root)),
        ("schema1 migrates without assigning over custom E binding", () => SchemaMigration(root))
    ];
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Defaults()
    {
        var settings = new AppSettings(); var map = new ShortcutMap(settings.Bindings, settings.Presets);
        var actions = CommandCatalog.Definitions(settings.Presets).Where(item => CommandCatalog.TryPresetAction(item.Id, out _, out _)).ToArray();
        Check(actions.Length == 4 && actions.Select(item => item.Id).Distinct().Count() == 4, "not four independent actions");
        Check(map.Resolve("E")?.Id == CommandCatalog.CopyTo("favorites"), "E does not copy to favorites");
        Check(map.Resolve("Q")?.Id == CommandCatalog.MoveTo("rejects"), "Q does not move to rejects");
        Check(settings.Presets.All(preset => preset.Path.Length == 0), "default path was guessed");
    }
    private static void RepeatGuard()
    {
        var settings = new AppSettings(); var map = new ShortcutMap(settings.Bindings, settings.Presets);
        Check(map.Resolve("E", true) is null && map.Resolve("Q", true) is null, "held keys repeat file actions");
    }
    private static void StableIdentity()
    {
        var settings = new AppSettings(); settings.Presets.Reverse();
        settings.Presets.Single(item => item.Id == "favorites").Name = "精选";
        var map = new ShortcutMap(settings.Bindings, settings.Presets);
        Check(map.Resolve("E")?.Id == CommandCatalog.CopyTo("favorites"), "reorder changed destination identity");
        Check(map.Resolve("E")?.Name.Contains("精选") == true, "renamed label is stale");
    }
    private static void ThirdPreset()
    {
        var settings = new AppSettings(); var extra = new FolderPreset { Name = "交付" };
        settings.Presets.Add(extra);
        settings.Bindings.Add(new(CommandCatalog.CopyTo(extra.Id), "Z"));
        settings.Bindings.Add(new(CommandCatalog.MoveTo(extra.Id), "C"));
        var map = new ShortcutMap(settings.Bindings, settings.Presets);
        Check(SettingsValidation.Error(settings) is null && map.Resolve("Z")?.Id == CommandCatalog.CopyTo(extra.Id)
            && map.Resolve("C")?.Id == CommandCatalog.MoveTo(extra.Id), "extra preset is not dynamic");
    }
    private static void RemovedPreset()
    {
        var settings = new AppSettings(); settings.Presets.RemoveAll(item => item.Id == "favorites");
        var map = new ShortcutMap(settings.Bindings, settings.Presets);
        Check(map.Resolve("E") is null && map.Resolve("Q")?.Id == CommandCatalog.MoveTo("rejects"), "removed action retargeted");
    }
    private static void InvalidIdentity()
    {
        var settings = new AppSettings(); settings.Presets[1].Id = settings.Presets[0].Id;
        Check(SettingsValidation.Error(settings) is not null, "duplicate IDs accepted");
        settings = new AppSettings(); settings.Presets[0].Path = "relative/folder";
        Check(SettingsValidation.Error(settings) is not null, "relative path accepted");
    }
    private static void SaveOnlyConfiguration(string root)
    {
        string directory = Path.Combine(root, "preset-config"), target = Path.Combine(root, "not-created-target");
        var store = new SettingsStore(directory); var settings = store.Load().Settings;
        settings.Presets[0].Path = target; settings.Presets[0].Name = "精选";
        settings.Presets[0].CopyAdvance = true; settings.Presets.Reverse(); store.Save(settings);
        var loaded = new SettingsStore(directory).Load().Settings;
        Check(!Directory.Exists(target), "saving config created a photo directory");
        Check(loaded.Presets[1].Id == "favorites" && loaded.Presets[1].Name == "精选" && loaded.Presets[1].CopyAdvance, "preset values or order lost");
    }
    private static void SchemaMigration(string root)
    {
        string directory = Path.Combine(root, "schema1-migration"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"bindings\":[{\"commandId\":\"image.open\",\"gesture\":\"E\",\"enabled\":true}]}");
        var store = new SettingsStore(directory); var settings = store.Load().Settings;
        var map = new ShortcutMap(settings.Bindings, settings.Presets);
        Check(settings.SchemaVersion == 3 && settings.Presets.Count == 2 && map.Resolve("E")?.Id == CommandCatalog.Open, "migration overwrote user shortcut");
        Check(File.ReadAllText(path).Contains("\"schemaVersion\":1"), "loading silently rewrote config");
    }
}

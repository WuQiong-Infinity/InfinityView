using System.IO;
using LightView.App.Persistence;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class DistributionValidation
{
    public static int Paths(string expected)
    {
        string actual = AppPaths.UserData;
        if (!actual.Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected data directory: {actual}");
        // A regular file blocks creating UserData. Failure must never fall back
        // to the daily installation's settings directory.
        var store = new SettingsStore(actual);
        store.Load();
        bool blocked = File.Exists(actual);
        try
        {
            store.Save(new AppSettings());
            if (blocked) throw new InvalidOperationException("Blocked portable data path accepted");
            if (!File.Exists(Path.Combine(actual, "settings.json"))) throw new InvalidOperationException("Settings missing");
        }
        catch (IOException) when (blocked)
        { Console.WriteLine("PASS blocked portable storage fails explicitly without fallback"); return 0; }
        Console.WriteLine("PASS portable data path and settings persistence: " + actual);
        return 0;
    }
}

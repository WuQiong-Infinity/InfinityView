using System.IO;

namespace LightView.App.Persistence;

internal static class AppPaths
{
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "InfinityView");
    public static string UserData
    {
        get
        {
            string? testOverride = Environment.GetEnvironmentVariable("INFINITYVIEW_DATA_DIR");
            if (FrameworkApi.IsNullOrWhiteSpace(testOverride)) testOverride = Environment.GetEnvironmentVariable("LIGHTVIEW_DATA_DIR");
            if (!FrameworkApi.IsNullOrWhiteSpace(testOverride)) return Path.GetFullPath(testOverride);
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable")))
                return Path.Combine(AppContext.BaseDirectory, "UserData");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfinityView");
        }
    }
}

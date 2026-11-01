using System.IO;
using Microsoft.Win32;

namespace LightView.App.Platform;

// The installer's own per-user uninstall record is the single installation
// location source. A copied marker alone never grants installation status.
internal static class InstallationLocator
{
    internal const string Identity = "LightView.Desktop.Install";
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LightView.Desktop.Install_is1";

    public static string? FindExecutable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return Validate(key?.GetValue("InstallLocation") as string,
                key?.GetValue("DisplayName") as string, key?.GetValue("UninstallString") as string);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    internal static string? Validate(string? directory, string? displayName, string? uninstallCommand)
    {
        try
        {
            if (FrameworkApi.IsNullOrWhiteSpace(directory) || !FrameworkApi.IsPathFullyQualified(directory) ||
                displayName is not ("InfinityView" or "LightView" or "LightView version 0.2.0")) return null;
            directory = FrameworkApi.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string executable = Path.Combine(directory, displayName == "InfinityView" ? "InfinityView.exe" : "LightView.exe");
            string uninstaller = Path.Combine(directory, "unins000.exe");
            if (uninstallCommand?.Trim().Trim('"').Equals(uninstaller, StringComparison.OrdinalIgnoreCase) != true ||
                !File.Exists(executable) || !File.Exists(uninstaller) || File.Exists(Path.Combine(directory, "portable"))) return null;
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            string marker = Path.Combine(directory, ".lightview-install");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker).Trim() != Identity) return null;
            return executable;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }
}

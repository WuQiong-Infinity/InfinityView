using System.IO;
using System.Runtime.InteropServices;
using LightView.Core.Viewing;

namespace LightView.App.Platform;

public sealed class ImageFolder
{
    private readonly string[] files;
    public string DirectoryPath { get; }
    public int Count => files.Length;
    public IReadOnlyList<string> Files => Array.AsReadOnly(files);
    private ImageFolder(string directory, string[] files) { DirectoryPath = directory; this.files = files; }

    public static ImageFolder Read(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string[] files = Directory.EnumerateFiles(fullPath).Where(ImageFormats.IsSupported).ToArray();
        Array.Sort(files, Comparer<string>.Create((a, b) =>
        {
            int result = StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b));
            return result != 0 ? result : StringComparer.Ordinal.Compare(a, b);
        }));
        return new(fullPath, files);
    }

    public int IndexOf(string path) => Array.FindIndex(files,
        candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate, path));

    public string? Neighbor(string current, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        int index = IndexOf(current);
        if (index < 0) return null;
        for (int next = index + direction; next >= 0 && next < files.Length; next += direction)
            if (File.Exists(files[next])) return files[next];
        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int StrCmpLogicalW(string first, string second);
}

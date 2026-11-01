using System.IO;
using System.Security.Cryptography;
using LightView.App.Platform;

namespace LightView.App.Organizing;

public sealed record FileSnapshot(string Path, string Identity, long Length, long LastWrite, string Sha256)
{
    internal static FileSnapshot Capture(string path, FileStream stream)
    {
        var before = WindowsFileHandle.Facts(stream.SafeFileHandle);
        long position = stream.Position;
        stream.Position = 0;
        string hash;
        try { hash = FrameworkApi.Hex(FrameworkApi.Sha256(stream)); }
        finally { stream.Position = position; }
        var after = WindowsFileHandle.Facts(stream.SafeFileHandle);
        if (before != after) throw new IOException("图片在读取期间发生变化，请重新打开。");
        return new(System.IO.Path.GetFullPath(path), after.Identity, after.Length, after.LastWrite, hash);
    }

    internal bool SameVersion(FileSnapshot other) => Identity == other.Identity && Length == other.Length &&
        LastWrite == other.LastWrite && Sha256 == other.Sha256;
}

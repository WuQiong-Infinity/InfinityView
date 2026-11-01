using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightView.App.Platform;

internal static class WindowsFileHandle
{
    internal static SafeFileHandle Open(string path, uint access = 0x80000000, uint creation = 3, bool directory = false, uint? sharing = null)
    {
        var handle = CreateFileW(path, access, sharing ?? (directory ? 3u : 1u), IntPtr.Zero, creation,
            0x00200000u | (directory ? 0x02000000u : 0u), IntPtr.Zero);
        if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw Error(code); }
        return handle;
    }

    internal static IOException Error(int code) => new(new Win32Exception(code).Message, unchecked((int)(0x80070000u | (uint)code)));

    internal static FileFacts Facts(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Error(Marshal.GetLastWin32Error());
        byte[] id = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, id, 24)) throw Error(Marshal.GetLastWin32Error());
        return new(FrameworkApi.Hex(id), ((long)info.SizeHigh << 32) | info.SizeLow,
            ((long)info.WriteHigh << 32) | info.WriteLow, (FileAttributes)info.Attributes);
    }

    internal static void EnsureOrdinary(SafeFileHandle handle)
    {
        var facts = Facts(handle);
        const FileAttributes unsupported = FileAttributes.ReparsePoint | FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.Directory;
        if ((facts.Attributes & unsupported) != 0) throw new NotSupportedException("整理暂不支持链接、加密、离线或特殊文件。");
        // FILE_STREAM_INFO includes the default stream and any alternate streams.
        byte[] streams = new byte[65536];
        if (!GetFileInformationByHandleEx(handle, 7, streams, (uint)streams.Length)) throw Error(Marshal.GetLastWin32Error());
        int offset = 0;
        do
        {
            int next = BitConverter.ToInt32(streams, offset), length = BitConverter.ToInt32(streams, offset + 4);
            if (length < 0 || offset + 24L + length > streams.Length) throw new IOException("文件流信息无效。");
            if (!Encoding.Unicode.GetString(streams, offset + 24, length).Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("整理暂不支持含备用数据流的文件。");
            if (next == 0) break;
            if (next < 24 || offset + (long)next > streams.Length - 24) throw new IOException("文件流信息无效。");
            offset += next;
        } while (true);
    }

    internal static void Rename(SafeFileHandle handle, string destination)
    {
        byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        int nameOffset = IntPtr.Size == 8 ? 20 : 12, rootOffset = IntPtr.Size == 8 ? 8 : 4;
        byte[] info = new byte[nameOffset + name.Length + 2];
        // ReplaceIfExists and RootDirectory remain zero: never overwrite.
        BitConverter.GetBytes(name.Length).CopyTo(info, rootOffset + IntPtr.Size);
        name.CopyTo(info, nameOffset);
        if (!SetFileInformationByHandle(handle, 3, info, (uint)info.Length)) throw Error(Marshal.GetLastWin32Error());
    }

    internal static void MarkForDeletion(SafeFileHandle handle)
    {
        if (!SetFileInformationByHandle(handle, 4, [1], 1)) throw Error(Marshal.GetLastWin32Error());
    }

    internal static void PreserveWriteTime(SafeFileHandle handle, long value)
    {
        if (!SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref value)) throw Error(Marshal.GetLastWin32Error());
    }

    internal static IDisposable PinDirectory(string path)
    {
        path = Path.GetFullPath(path);
        string root = Path.GetPathRoot(path)!;
        if (root.StartsWith(@"\\") || new DriveInfo(root).DriveType != DriveType.Fixed || path.IndexOf(':', 2) >= 0)
            throw new NotSupportedException("整理目标须为本地磁盘的普通目录。");
        var handles = new List<SafeFileHandle>();
        try
        {
            string current = root;
            foreach (string segment in new[] { "" }.Concat(path.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)))
            {
                if (segment.Length > 0) current = Path.Combine(current, segment);
                var handle = Open(current, 0x80, directory: true);
                handles.Add(handle);
                var attributes = Facts(handle).Attributes;
                if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new NotSupportedException("整理目标路径不能经过链接或重解析目录。");
            }
            return new DirectoryLease(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    private sealed class DirectoryLease(List<SafeFileHandle> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }
    internal sealed record FileFacts(string Identity, long Length, long LastWrite, FileAttributes Attributes);
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, [Out] byte[] info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, IntPtr creation, IntPtr access, ref long write);
}

using System.IO;
using System.Runtime.InteropServices;
using LightView.App.Platform;

namespace LightView.App.Organizing;

internal static class RecycleService
{
    internal static Task<string> RecycleAsync(FileSnapshot expected, IntPtr owner)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { done.SetResult(Recycle(expected, owner)); } catch (Exception e) { done.SetException(e); } });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static string Recycle(FileSnapshot expected, IntPtr owner)
    {
        using var directories = WindowsFileHandle.PinDirectory(Path.GetDirectoryName(expected.Path)!);
        // Deny content writes while Shell performs the recycle; allow its rename.
        using var source = new FileStream(WindowsFileHandle.Open(expected.Path, sharing: 5), FileAccess.Read);
        WindowsFileHandle.EnsureOrdinary(source.SafeFileHandle);
        if (!expected.SameVersion(FileSnapshot.Capture(expected.Path, source))) throw new IOException("图片文件已变化，请重新打开后再删除。");
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!)!;
        IShellItem? item = null;
        try
        {
            Guid id = typeof(IShellItem).GUID; SHCreateItemFromParsingName(expected.Path, IntPtr.Zero, ref id, out item);
            var sink = new RecycleSink(expected);
            // Recycle only, no silent permanent-delete fallback; no connected files.
            operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x2000 | 0x4000 | 0x0400 | 0x0010 | 0x0004);
            operation.SetOwnerWindow(owner); operation.DeleteItem(item, sink); operation.PerformOperations(); operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted || sink.Error < 0 || sink.RecycledPath is null)
                throw new IOException("无法移入回收站，未执行永久删除。" + (sink.Error < 0 ? $"（0x{sink.Error:X8}）" : ""));
            return sink.RecycledPath;
        }
        finally { if (item is not null) Marshal.FinalReleaseComObject(item); Marshal.FinalReleaseComObject(operation); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(FileSnapshot expected) : IProgressSink
    {
        internal string? RecycledPath; internal int Error;
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                if ((flags & 0x80) == 0) return unchecked((int)0x80004004);
                item.GetDisplayName(0x80058000, out string path);
                if (!Path.GetFullPath(path).Equals(expected.Path, StringComparison.OrdinalIgnoreCase)) return unchecked((int)0x80004004);
                using var check = new FileStream(WindowsFileHandle.Open(path, sharing: 5), FileAccess.Read);
                return expected.SameVersion(FileSnapshot.Capture(path, check)) ? 0 : unchecked((int)0x80004004);
            }
            catch (Exception e) { return Marshal.GetHRForException(e); }
        }
        public int PostDeleteItem(uint f, IShellItem item, int result, IShellItem? created)
        { Error = result; if (result >= 0 && created is not null) try { created.GetDisplayName(0x80058000, out var path); RecycledPath = path; } catch (COMException e) { Error = e.HResult; } return 0; }
        public int StartOperations() => 0; public int FinishOperations(int result) { if (result < 0) Error = result; return 0; }
        public int PreRenameItem(uint f, IShellItem i, string n) => 0; public int PostRenameItem(uint f, IShellItem i, string n, int r, IShellItem? c) => 0;
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) => 0; public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? c) => 0;
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => 0; public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? c) => 0;
        public int PreNewItem(uint f, IShellItem d, string n) => 0; public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int r, IShellItem? c) => 0;
        public int UpdateProgress(uint total, uint completed) => 0; public int ResetTimer() => 0; public int PauseTimer() => 0; public int ResumeTimer() => 0;
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr p, ref Guid b, ref Guid r, out IntPtr v); void GetParent(out IShellItem p);
        void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name); void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem other, uint hint, out int order);
    }
    [ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProgressSink
    {
        [PreserveSig] int StartOperations(); [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint f, IShellItem i, [MarshalAs(UnmanagedType.LPWStr)] string n); [PreserveSig] int PostRenameItem(uint f, IShellItem i, [MarshalAs(UnmanagedType.LPWStr)] string n, int r, IShellItem? c);
        [PreserveSig] int PreMoveItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n); [PreserveSig] int PostMoveItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n, int r, IShellItem? c);
        [PreserveSig] int PreCopyItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n); [PreserveSig] int PostCopyItem(uint f, IShellItem i, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n, int r, IShellItem? c);
        [PreserveSig] int PreDeleteItem(uint f, IShellItem i); [PreserveSig] int PostDeleteItem(uint f, IShellItem i, int r, IShellItem? c);
        [PreserveSig] int PreNewItem(uint f, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n); [PreserveSig] int PostNewItem(uint f, IShellItem d, [MarshalAs(UnmanagedType.LPWStr)] string n, [MarshalAs(UnmanagedType.LPWStr)] string t, uint a, int r, IShellItem? c);
        [PreserveSig] int UpdateProgress(uint total, uint completed); [PreserveSig] int ResetTimer(); [PreserveSig] int PauseTimer(); [PreserveSig] int ResumeTimer();
    }
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IProgressSink sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags); void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems(IntPtr items); void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink sink); void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink sink); void MoveItems(IntPtr items, IShellItem destination); void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink sink); void CopyItems(IntPtr items, IShellItem destination);
        void DeleteItem(IShellItem item, IProgressSink sink); void DeleteItems(IntPtr items); void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IProgressSink sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] private static extern void SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid id, out IShellItem item);
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LightView.App.Platform;

// Original ABI adapter for Windows' existing common folder picker (IFileDialog).
// API contract: learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifiledialog
internal sealed class FolderDialog
{
    public string Title { get; set; } = "选择文件夹";
    public string? InitialDirectory { get; set; }
    public string FolderName { get; private set; } = "";
    public bool? ShowDialog(Window owner)
    {
        var dialog = (IFileDialog)new FileOpenDialogCom();
        IShellItem? initial = null, result = null;
        try
        {
            dialog.GetOptions(out uint options);
            dialog.SetOptions((options | 0x20u | 0x40u | 0x8u) & ~0x200u);
            dialog.SetTitle(Title);
            if (!FrameworkApi.IsNullOrWhiteSpace(InitialDirectory))
            {
                var iid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(InitialDirectory, IntPtr.Zero, ref iid, out initial));
                dialog.SetFolder(initial);
            }
            int code = dialog.Show(new WindowInteropHelper(owner).EnsureHandle());
            if (code == unchecked((int)0x800704C7)) return false;
            Marshal.ThrowExceptionForHR(code);
            dialog.GetResult(out result);
            result.GetDisplayName(0x80058000, out var path); // SIGDN_FILESYSPATH
            try { FolderName = Marshal.PtrToStringUni(path)!; }
            finally { Marshal.FreeCoTaskMem(path); }
            return true;
        }
        finally
        {
            if (result is not null) Marshal.FinalReleaseComObject(result);
            if (initial is not null) Marshal.FinalReleaseComObject(initial);
            Marshal.FinalReleaseComObject(dialog);
        }
    }
    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")] private class FileOpenDialogCom { }
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint location);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}

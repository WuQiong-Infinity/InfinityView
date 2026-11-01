using System.Runtime.InteropServices;

namespace LightView.App.Platform;

internal static class DefaultAssociationQuery
{
    // QueryCurrentDefault is the read-only method supported on Windows 8+.
    // AL_EFFECTIVE (1) asks for the current user's effective Shell association.
    // No setters, raw UserChoice access, or hash manipulation are used here.
    public static string? Read(string extension)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("591209c7-767b-42b2-9fba-44ee4615f2c7"))!);
            int result = ((IRegistration)instance!).QueryCurrentDefault(extension, 0, 1, out string? progId);
            return result >= 0 && !FrameworkApi.IsNullOrWhiteSpace(progId) ? progId : null;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or UnauthorizedAccessException)
        { return null; }
        finally { if (instance is not null) Marshal.ReleaseComObject(instance); }
    }
    [ComImport, Guid("4e530b0a-e611-4c77-a3ac-9031d022281b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRegistration
    {
        [PreserveSig]
        int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string query, int associationType, int associationLevel,
            [MarshalAs(UnmanagedType.LPWStr)] out string? association);
    }
}

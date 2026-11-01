using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using LightView.App.Imaging;

namespace LightView.App.Platform;

internal sealed record DisplayColorState(string DeviceName, string? IccPath,
    bool? AdvancedColorEnabled, int? BitsPerColor, string? Error);

internal static class DisplayColorProbe
{
    public static DisplayColorTarget TargetForWindow(IntPtr window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        IntPtr monitor = MonitorFromWindow(window, 2);
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info))
            return DisplayColorTarget.Standard with { Description = "sRGB（显示状态未确认）", Key = "unknown" };
        var state = ReadActiveDisplays().FirstOrDefault(item => item.DeviceName == info.Device);
        if (state?.AdvancedColorEnabled == true)
            return DisplayColorTarget.Standard with { Description = "sRGB（由系统管理显示颜色）", Key = "auto:" + info.Device };
        if (state?.AdvancedColorEnabled != false)
            return DisplayColorTarget.Standard with { Description = "sRGB（显示状态未确认）", Key = "unknown:" + info.Device };
        try
        {
            if (state.IccPath is null || !File.Exists(state.IccPath))
                return DisplayColorTarget.Standard with { Description = "sRGB（显示器配置未提供）", Key = "missing:" + info.Device };
            if (new FileInfo(state.IccPath).Length > 16 * 1024 * 1024) throw new NotSupportedException();
            var target = DisplayColorTarget.FromProfile(File.ReadAllBytes(state.IccPath),
                "显示器 ICC：" + Path.GetFileName(state.IccPath));
            return target with { Key = "legacy:" + info.Device + ":" + target.Key };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return DisplayColorTarget.Standard with { Description = "sRGB（显示器配置不可读）", Key = "unreadable:" + info.Device };
        }
    }

    public static IReadOnlyList<DisplayColorState> ReadActiveDisplays()
    {
        int error = GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount);
        if (error != 0) return [new("Unknown", null, null, null, new Win32Exception(error).Message)];
        // DISPLAYCONFIG_PATH_INFO is 72 bytes, DISPLAYCONFIG_MODE_INFO is 64.
        IntPtr paths = Marshal.AllocHGlobal(checked((int)pathCount * 72));
        IntPtr modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
        try
        {
            error = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error != 0) return [new("Unknown", null, null, null, new Win32Exception(error).Message)];
            var result = new List<DisplayColorState>();
            for (int i = 0; i < pathCount; i++)
            {
                IntPtr path = paths + i * 72;
                byte[] sourcePacket = new byte[84];
                WriteHeader(sourcePacket, 1, path);
                int sourceError = DisplayConfigGetDeviceInfo(sourcePacket);
                string name = sourceError == 0
                    ? Encoding.Unicode.GetString(sourcePacket, 20, 64).TrimEnd('\0') : "Unknown";
                byte[] colorPacket = new byte[32];
                WriteHeader(colorPacket, 9, path + 20);
                int colorError = DisplayConfigGetDeviceInfo(colorPacket);
                bool? advanced = colorError == 0 ? (BitConverter.ToUInt32(colorPacket, 20) & 2) != 0 : null;
                int? bits = colorError == 0 ? (int)BitConverter.ToUInt32(colorPacket, 28) : null;
                string? profile = ReadProfile(name);
                string? detail = sourceError != 0 ? new Win32Exception(sourceError).Message
                    : colorError != 0 ? new Win32Exception(colorError).Message : null;
                result.Add(new(name, profile, advanced, bits, detail));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(modes); Marshal.FreeHGlobal(paths); }
    }

    private static void WriteHeader(byte[] packet, uint type, IntPtr pathSection)
    {
        BitConverter.GetBytes(type).CopyTo(packet, 0);
        BitConverter.GetBytes(packet.Length).CopyTo(packet, 4);
        Marshal.Copy(pathSection, packet, 8, 12);
    }

    private static string? ReadProfile(string deviceName)
    {
        if (deviceName == "Unknown") return null;
        IntPtr dc = CreateDCW("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            uint count = 32768;
            var path = new StringBuilder((int)count);
            return GetICMProfileW(dc, ref count, path) ? path.ToString() : null;
        }
        finally { DeleteDC(dc); }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, IntPtr paths,
        ref uint modeCount, IntPtr modes, IntPtr topology);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] packet);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateDCW(string driver, string device, string? output, IntPtr data);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetICMProfileW(IntPtr dc, ref uint length, StringBuilder profile);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}

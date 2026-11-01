using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightView.Tests;

// Foreground, synthetic-only launch oracle. This supplies a real owner HWND and
// WPF/COM message pump, unlike a background console invoking the Shell broker.
internal static class ShellLaunchValidation
{
    public static int Run(string fixture, string root)
    {
        fixture = Path.GetFullPath(fixture); root = Path.GetFullPath(root);
        string allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work")) + Path.DirectorySeparatorChar;
        if (!fixture.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Synthetic fixtures and output must remain under work");
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window { Title = "LightView 打开链路验收", Width = 560, Height = 180,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)), Foreground = Brushes.White,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var button = new Button { Content = "通过当前默认应用打开人工图片", Margin = new Thickness(24), FontSize = 16 };
        window.Content = button; var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        timer.Tick += (_, _) => window.Close();
        window.Closed += (_, _) => { timer.Stop(); frame.Continue = false; };
        button.Click += (_, _) =>
        {
            string? oldData = Environment.GetEnvironmentVariable("LIGHTVIEW_DATA_DIR"), oldOutput = Environment.GetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_OUTPUT"), oldExit = Environment.GetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_EXIT");
            Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", root);
            Environment.SetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_OUTPUT", Path.Combine(root, "first.json"));
            Environment.SetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_EXIT", "1");
            int init = CoInitializeEx(IntPtr.Zero, 6);
            try
            {
                var info = new Info { size = (uint)Marshal.SizeOf<Info>(), mask = 0x40 | 0x100,
                    hwnd = new WindowInteropHelper(window).Handle, verb = "open", file = fixture, show = 1 };
                bool success = ShellExecuteExW(ref info); int error = success ? 0 : Marshal.GetLastWin32Error();
                uint id = info.process == IntPtr.Zero ? 0 : GetProcessId(info.process);
                if (info.process != IntPtr.Zero) CloseHandle(info.process);
                File.WriteAllText(Path.Combine(root, "launch.json"), JsonSerializer.Serialize(new { success, error, processId = id,
                    method = "Foreground button ShellExecuteEx with owner HWND, no explicit class or executable", fixture }));
                button.Content = success ? "系统已接受打开请求" : "系统返回错误 " + error;
            }
            finally
            {
                if (init >= 0) CoUninitialize();
                Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", oldData);
                Environment.SetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_OUTPUT", oldOutput);
                Environment.SetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_EXIT", oldExit);
            }
        };
        window.Show(); timer.Start(); Dispatcher.PushFrame(frame); return 0;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Info
    {
        public uint size, mask; public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? file;
        [MarshalAs(UnmanagedType.LPWStr)] public string? parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? directory;
        public int show; public IntPtr instance, idList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? className;
        public IntPtr classKey; public uint hotKey; public IntPtr icon, process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ShellExecuteExW(ref Info info);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

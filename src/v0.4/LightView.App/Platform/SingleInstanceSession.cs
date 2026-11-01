using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LightView.App.Platform;

internal sealed class SingleInstanceSession : IDisposable
{
    private readonly string channel;
    private readonly Func<uint,bool>? foregroundGrant;
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private Task? server;
    public bool IsPrimary { get; }
    public string? LastForwardFailure { get; private set; }
    public SingleInstanceSession(string userData, Func<uint,bool>? foregroundGrant = null)
    {
        this.foregroundGrant = foregroundGrant;
        channel = ChannelName(userData);
        mutex = new(false, @"Local\" + channel);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }
    internal static string ChannelName(string userData) => "LightView-" + FrameworkApi.Hex(FrameworkApi.Sha256(Encoding.UTF8.GetBytes(Path.GetFullPath(userData).ToUpperInvariant()))).Substring(0, 24);
    public static bool IsRunning(string userData)
    {
        try
        {
            using var probe = Mutex.OpenExisting(@"Local\" + ChannelName(userData));
            bool acquired;
            try { acquired = probe.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return true;
            probe.ReleaseMutex(); return false;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    public void Listen(Func<string?, Task> receive)
    {
        if (!IsPrimary || server is not null) throw new InvalidOperationException("Only one primary listener is allowed.");
        server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(channel, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, OwnerSecurity());
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    byte[] size = new byte[4]; await pipe.ReadExactlyAsync(size, timeout.Token);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(size);
                    if (length is <= 0 or > 32768) continue;
                    byte[] data = new byte[length]; await pipe.ReadExactlyAsync(data, timeout.Token);
                    var request = JsonSerializer.Deserialize<OpenRequest>(data);
                    if (request is null) continue;
                    await receive(request.Path).WaitAsync(timeout.Token);
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token); await pipe.FlushAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or JsonException or OperationCanceledException) { }
            }
        });
    }
    public async Task<bool> ForwardAsync(string? path, string? expectedExecutable = null)
    {
        LastForwardFailure = null;
        if (IsPrimary) throw new InvalidOperationException("Primary cannot forward to itself.");
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new OpenRequest(path));
        if (data.Length > 32768) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var pipe = new NamedPipeClientStream(".", channel, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            if (!HasCurrentOwner(pipe.GetAccessControl())) return false;
            int? verifiedPid = null;
            if (expectedExecutable is not null)
            {
                verifiedPid = AcceptPeer(pipe, expectedExecutable);
                if (verifiedPid is null) return false;
            }
            // Grant only after owner and executable validation, before sending the request.
            ForegroundActivation.Grant(true, expectedExecutable, verifiedPid, foregroundGrant);
            byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
            await pipe.WriteAsync(size, timeout.Token); await pipe.WriteAsync(data, timeout.Token); await pipe.FlushAsync(timeout.Token);
            byte[] acknowledgment = new byte[1]; await pipe.ReadExactlyAsync(acknowledgment, timeout.Token);
            return acknowledgment[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    // Equivalent Owner contract to dotnet/runtime v10.0.13 CurrentUserOnly.
    // Pinned source and MIT attribution: Compatibility/pipe-source.json.
    internal static PipeSecurity OwnerSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.Owner ?? throw new UnauthorizedAccessException("No current token owner.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(owner);
        return security;
    }
    internal static bool HasCurrentOwner(PipeSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Owner is { } owner && owner.Equals(security.GetOwner(typeof(SecurityIdentifier)));
    }
    private int? AcceptPeer(NamedPipeClientStream pipe, string expectedExecutable)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using var process = Process.GetProcessById(checked((int)id));
            string actual = process.MainModule?.FileName ?? throw new IOException("现有实例位置无法读取。");
            if (Path.GetFullPath(actual).Equals(Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase)) return checked((int)id);
            string version = "版本无法读取";
            try { version = FileVersionInfo.GetVersionInfo(actual).ProductVersion ?? version; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            LastForwardFailure = "其他位置的 InfinityView 正在运行：" + Environment.NewLine + actual + Environment.NewLine + version +
                Environment.NewLine + Environment.NewLine + "请先正常退出该窗口，再重新打开当前版本：" + Environment.NewLine + expectedExecutable +
                Environment.NewLine + Environment.NewLine + "本次图片未交给旧副本，正在进行的整理不会被中断。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            LastForwardFailure = "无法确认现有 InfinityView 实例的运行位置。请正常退出现有窗口后，再重新打开当前版本。";
        }
        return null;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    public void Dispose()
    {
        stop.Cancel();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
        // Keep the token source alive until the background listener observes cancellation.
        if (server is { } running) _ = running.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        else stop.Dispose();
    }
    private sealed record OpenRequest(string? Path);
}

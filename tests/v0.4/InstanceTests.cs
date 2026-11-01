using System.Diagnostics;
using System.IO;
using LightView.App.Platform;

namespace LightView.Tests;

internal static class InstanceTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("second process forwards literal Unicode paths and receives acknowledgment", () => Forward(root)),
        ("different data roots have independent instances and released owner can be replaced", () => Isolation(root)),
        ("native pipe peer from another executable is rejected before any image request", () => Peer(root))
    ];
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static int Child(string directory, string path, string? expectedExecutable = null)
    {
        var start = new ProcessStartInfo(FrameworkApi.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (string.Equals(Path.GetFileNameWithoutExtension(FrameworkApi.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            FrameworkApi.AddArgument(start, typeof(InstanceTests).Assembly.Location);
        foreach (string arg in new[] { "--instance-client", directory, path }) FrameworkApi.AddArgument(start, arg);
        if (expectedExecutable is not null) FrameworkApi.AddArgument(start, expectedExecutable);
        using var process = Process.Start(start)!;
        Check(process.WaitForExit(15000), "Second process did not return"); return process.ExitCode;
    }
    internal static int RunClient(string directory, string path, string? expectedExecutable = null)
    {
        using var session = new SingleInstanceSession(directory);
        return session.IsPrimary ? 4 : session.ForwardAsync(path, expectedExecutable).GetAwaiter().GetResult() ? 0 : session.LastForwardFailure is not null ? 5 : 2;
    }
    private static void Forward(string root)
    {
        string directory = Path.Combine(root, "instance-data"), expected = Path.Combine(root, "中文 $() & name.png");
        using var session = new SingleInstanceSession(directory); Check(session.IsPrimary, "Primary ownership absent");
        string? received = null;
        session.Listen(path => { received = path; return Task.CompletedTask; });
        Check(Child(directory, expected) == 0 && received == expected, "Path was modified, executed, or not acknowledged");
        Check(Child(directory, expected) == 0, "Listener failed to accept another request");
    }
    private static void Isolation(string root)
    {
        string directory = Path.Combine(root, "instance-isolation");
        using (var session = new SingleInstanceSession(directory))
        {
            Check(session.IsPrimary, "First owner absent");
            Check(Child(directory + "-other", "sample.png") == 4, "Different data scope collided");
        }
        Check(Child(directory, "sample.png") == 4, "Closed owner could not be replaced");
    }
    private static void Peer(string root)
    {
        string directory = Path.Combine(root, "peer-identity"), path = Path.Combine(root, "new-request.jpg");
        using var session = new SingleInstanceSession(directory); string? received = null;
        session.Listen(value => { received = value; return Task.CompletedTask; });
        Check(Child(directory, path, Path.Combine(root, "other-copy", "LightView.exe")) == 5 && received is null, "Wrong executable received a photo before identity validation");
        Check(Child(directory, path, FrameworkApi.ProcessPath) == 0 && received == path, "Rejected connection broke the old protocol or same-copy forwarding");
    }
}

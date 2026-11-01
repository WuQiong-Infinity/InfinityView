using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LightView.App.Platform;

namespace LightView.Tests;

internal static class CompatibilityValidation
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var cases = new (string Name, Action Test)[]
        {
            ("owner ACL grants only token Owner and validates actual local pipe", () =>
            {
                var security = SingleInstanceSession.OwnerSecurity();
                using var identity = WindowsIdentity.GetCurrent();
                Check(identity.Owner!.Equals(security.GetOwner(typeof(SecurityIdentifier))), "Owner changed");
                var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
                Check(rules.Count == 1 && ((PipeAccessRule)rules[0]).IdentityReference.Equals(identity.Owner) &&
                    ((PipeAccessRule)rules[0]).AccessControlType == AccessControlType.Allow &&
                    ((PipeAccessRule)rules[0]).PipeAccessRights == PipeAccessRights.FullControl, "ACL widened");
                string name = "LightView-v04-ACL-" + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                var connected = server.WaitForConnectionAsync();
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(2000); connected.GetAwaiter().GetResult();
                Check(SingleInstanceSession.HasCurrentOwner(client.GetAccessControl()), "actual Owner mismatch");
            }),
            ("foreign Owner descriptor is rejected", () =>
            {
                var security = new PipeSecurity(); security.SetOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
                Check(!SingleInstanceSession.HasCurrentOwner(security), "foreign Owner accepted");
            }),
            ("SHA256 and uppercase channel bytes retain fixed values", () =>
                Check(FrameworkApi.Hex(FrameworkApi.Sha256(System.Text.Encoding.ASCII.GetBytes("abc"))) ==
                    "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "digest drift")),
            ("absolute path and finite value guards retain rejection", () =>
            {
                Check(!FrameworkApi.IsPathFullyQualified("C:relative") && !FrameworkApi.IsPathFullyQualified(@"\relative") &&
                    FrameworkApi.IsPathFullyQualified(@"C:\absolute") && FrameworkApi.IsPathFullyQualified(@"\\server\share\a"), "path guard drift");
                Check(!FrameworkApi.IsFinite(double.NaN) && !FrameworkApi.IsFinite(double.PositiveInfinity) && double.IsNaN(FrameworkApi.Clamp(double.NaN, 0, 1)), "finite guard drift");
            }),
            ("short reads assemble exactly and premature EOF rejects", () =>
            {
                using var shortRead = new OneByteStream(new byte[] { 1, 2, 3 }); var result = new byte[3]; shortRead.ReadExactly(result);
                Check(result.SequenceEqual(new byte[] { 1, 2, 3 }), "short read lost bytes");
                bool rejected = false; try { shortRead.ReadExactly(new byte[1]); } catch (EndOfStreamException) { rejected = true; }
                Check(rejected, "EOF accepted");
            }),
            ("async short read and cancellation retain boundaries", () =>
            {
                using var shortRead = new OneByteStream(new byte[] { 4, 5, 6 }); var result = new byte[3]; shortRead.ReadExactlyAsync(result, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.SequenceEqual(new byte[] { 4, 5, 6 }), "async short read lost bytes");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool rejected = false;
                try { shortRead.ReadExactlyAsync(new byte[1], cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "cancelled read continued");
            }),
            ("cancelled wait preserves underlying task and reports cancellation", () =>
            {
                var task = new TaskCompletionSource<bool>(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                bool rejected = false; try { task.Task.WaitAsync(cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected && !task.Task.IsCompleted, "wait cancelled underlying work"); task.SetResult(true);
            }),
            ("wait timeout is distinct from operation cancellation", () =>
            {
                var task = new TaskCompletionSource<bool>(); bool rejected = false;
                try { task.Task.WaitAsync(TimeSpan.FromMilliseconds(20)).GetAwaiter().GetResult(); } catch (TimeoutException) { rejected = true; }
                Check(rejected && !task.Task.IsCompleted, "timeout contract changed"); task.SetResult(true);
            }),
            ("Windows command-line arguments survive real child process", () =>
            {
                string[] expected = { "", "中文 空格", "quote\"inside", @"C:\ends with slash\", @"C:\two\\\" + "\"end", "literal $() & # [x]" };
                var start = new ProcessStartInfo(FrameworkApi.ProcessPath) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                FrameworkApi.AddArgument(start, "--echo-args"); foreach (string argument in expected) FrameworkApi.AddArgument(start, argument);
                using var child = Process.Start(start)!; string text = child.StandardOutput.ReadToEnd();
                Check(child.WaitForExit(5000) && child.ExitCode == 0 && JsonSerializer.Deserialize<string[]>(text)!.SequenceEqual(expected), "child arguments changed");
            })
        };
        int failures = 0;
        foreach (var item in cases) try { item.Test(); Console.WriteLine("PASS " + item.Name); } catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        Console.WriteLine($"RESULT {cases.Length - failures}/{cases.Length} framework compatibility checks");
        return failures == 0 ? 0 : 1;
    }
    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation) => base.ReadAsync(buffer, offset, Math.Min(1, count), cancellation);
    }
}

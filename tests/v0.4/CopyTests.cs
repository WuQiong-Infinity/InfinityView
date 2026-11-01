using System.IO;
using System.Runtime.InteropServices;
using LightView.App.Organizing;
using LightView.App.Platform;
using LightView.App.Imaging;
using LightView.App.ViewModels;

namespace LightView.Tests;

internal static class CopyTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("copy preserves bytes timestamp and durable completed journal", () => Basic(root)),
        ("copy keeps conflicts and handles a competing commit", () => Conflict(root)),
        ("same source location and hard link are no op", () => Same(root)),
        ("repeated copy reuses only an unchanged session destination", () => Duplicate(root)),
        ("same timestamp content edit and path replacement reject stale display", () => Changed(root)),
        ("source cannot be written or deleted during transfer", () => Locked(root)),
        ("cancel before commit removes only own temporary", () => Cancel(root)),
        ("cancel after commit keeps completed copy", () => LateCancel(root)),
        ("failures before commit retain source and remove own temporary", () => EarlyFailures(root)),
        ("journal failure after commit preserves copy and reports recovery", () => AfterCommit(root)),
        ("single executor ignores concurrent commands without queueing", () => Busy(root)),
        ("alternate streams reject without silently losing data", () => Special(root)),
        ("loading another picture disables organizing the previous display", () => Loading(root))
    ];

    private static (string Source, string Target, string Logs) Setup(string root, string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source.png"), target = Path.Combine(folder, "target"), logs = Path.Combine(folder, "journal");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(source, Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i * 13)).ToArray());
        return (source, target, logs);
    }
    private static FileSnapshot Snapshot(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return FileSnapshot.Capture(path, file); }
    private static CopyResult Copy(SafeCopyService service, FileSnapshot file, string target, CancellationToken token = default)
        => service.CopyAsync(new(file, target, "测试"), token).GetAwaiter().GetResult();
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Intact(string source, string target, int files)
    {
        Check(File.Exists(source), "Source lost");
        Check(Directory.GetFiles(target).Length == files, "Unexpected target/temporary files");
    }
    private static void Basic(string root)
    {
        var t = Setup(root, "copy-basic"); var source = Snapshot(t.Source);
        using var lease = OperationJournal.AcquireSession(t.Logs);
        var result = Copy(new(new OperationJournal(t.Logs)), source, t.Target);
        Check(result.State == "Completed", result.Message);
        Check(File.ReadAllBytes(t.Source).SequenceEqual(File.ReadAllBytes(result.Destination!)), "Bytes changed");
        Check(File.GetLastWriteTimeUtc(t.Source) == File.GetLastWriteTimeUtc(result.Destination!), "Timestamp changed");
        var lines = File.ReadAllLines(Directory.GetFiles(t.Logs, "*.jsonl").Single());
        Check(lines.Length >= 6 && lines[0].Contains("Prepared") && lines[lines.Length - 1].Contains("Completed"), "Journal incomplete");
        bool denied = false; try { using var other = OperationJournal.AcquireSession(t.Logs); } catch (IOException) { denied = true; }
        Check(denied, "Two sessions acquired write lease");
    }
    private static void Conflict(string root)
    {
        var t = Setup(root, "copy-conflict"); File.WriteAllText(Path.Combine(t.Target, "source.png"), "original");
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, r) =>
        {
            if (stage == "CommitPlanned" && r.Destination == Path.Combine(t.Target, "source (1).png"))
                File.WriteAllText(r.Destination, "competitor");
        });
        var result = Copy(service, Snapshot(t.Source), t.Target);
        Check(result.State == "Completed" && Path.GetFileName(result.Destination) == "source (2).png", result.Message);
        Check(File.ReadAllText(Path.Combine(t.Target, "source.png")) == "original" &&
              File.ReadAllText(Path.Combine(t.Target, "source (1).png")) == "competitor", "Existing file overwritten");
    }
    private static void Same(string root)
    {
        var t = Setup(root, "copy-same"); var snap = Snapshot(t.Source); var service = new SafeCopyService(new OperationJournal(t.Logs));
        Check(Copy(service, snap, Path.GetDirectoryName(t.Source)!).State == "NoOp", "Same path copied");
        string link = Path.Combine(t.Target, "source.png");
        Check(CreateHardLinkW(link, t.Source, IntPtr.Zero), "Cannot create isolated hard link");
        Check(Copy(service, snap, t.Target).State == "NoOp", "Hard link copied");
        Check(!Directory.Exists(t.Logs), "No op wrote journal");
    }
    private static void Duplicate(string root)
    {
        var t = Setup(root, "copy-repeat"); var service = new SafeCopyService(new OperationJournal(t.Logs)); var snap = Snapshot(t.Source);
        var first = Copy(service, snap, t.Target); Check(first.State == "Completed", first.Message);
        Check(Copy(service, snap, t.Target).State == "NoOp", "Duplicate created");
        File.WriteAllText(first.Destination!, "changed externally");
        Check(Copy(service, snap, t.Target).State == "Completed", "Changed destination incorrectly reused"); Intact(t.Source, t.Target, 2);
    }
    private static void Changed(string root)
    {
        var t = Setup(root, "copy-changed"); var snap = Snapshot(t.Source); DateTime time = File.GetLastWriteTimeUtc(t.Source);
        byte[] data = File.ReadAllBytes(t.Source); data[0] ^= 0xff; File.WriteAllBytes(t.Source, data); File.SetLastWriteTimeUtc(t.Source, time);
        var service = new SafeCopyService(new OperationJournal(t.Logs));
        Check(Copy(service, snap, t.Target).State == "Failed", "Same timestamp edit copied stale content");
        snap = Snapshot(t.Source); File.Move(t.Source, t.Source + ".old"); File.WriteAllBytes(t.Source, data); File.SetLastWriteTimeUtc(t.Source, time);
        Check(Copy(service, snap, t.Target).State == "Failed", "Replacement file accepted"); Intact(t.Source, t.Target, 0);
    }
    private static void Locked(string root)
    {
        var t = Setup(root, "copy-lock"); bool writesDenied = false, deletesDenied = false;
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, _) =>
        {
            if (stage != "Transferring") return;
            try { using var write = File.OpenWrite(t.Source); } catch (IOException) { writesDenied = true; }
            try { File.Delete(t.Source); } catch (IOException) { deletesDenied = true; }
        });
        Check(Copy(service, Snapshot(t.Source), t.Target).State == "Completed" && writesDenied && deletesDenied, "Source not locked");
    }
    private static void Cancel(string root)
    {
        var t = Setup(root, "copy-cancel"); using var cts = new CancellationTokenSource();
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, _) => { if (stage == "CommitPlanned") cts.Cancel(); });
        Check(Copy(service, Snapshot(t.Source), t.Target, cts.Token).State == "Cancelled", "Cancel not respected"); Intact(t.Source, t.Target, 0);
    }
    private static void LateCancel(string root)
    {
        var t = Setup(root, "copy-late-cancel"); using var cts = new CancellationTokenSource();
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, _) => { if (stage == "DestinationCommitted") cts.Cancel(); });
        Check(Copy(service, Snapshot(t.Source), t.Target, cts.Token).State == "Completed", "Committed copy claimed canceled"); Intact(t.Source, t.Target, 1);
    }
    private static void EarlyFailures(string root)
    {
        foreach (string stage in new[] { "Prepared", "Transferring", "Verifying", "CommitPlanned" })
        {
            var t = Setup(root, "copy-failure-" + stage);
            var service = new SafeCopyService(new OperationJournal(t.Logs), (s, _) => { if (s == stage) throw new IOException("Injected " + stage); });
            Check(Copy(service, Snapshot(t.Source), t.Target).State == "Failed", stage + " incorrectly successful"); Intact(t.Source, t.Target, 0);
        }
        var j = Setup(root, "copy-prepared-journal-fail");
        Check(Copy(new(new FailingJournal(j.Logs, "Prepared")), Snapshot(j.Source), j.Target).State == "Failed", "Journal failed open"); Intact(j.Source, j.Target, 0);
    }
    private static void AfterCommit(string root)
    {
        var t = Setup(root, "copy-committed-failure");
        var result = Copy(new(new FailingJournal(t.Logs, "DestinationCommitted")), Snapshot(t.Source), t.Target);
        Check(result.State == "NeedsRecovery", result.Message); Intact(t.Source, t.Target, 1);
        Check(File.ReadAllBytes(t.Source).SequenceEqual(File.ReadAllBytes(result.Destination!)), "Committed destination lost");
    }
    private static void Busy(string root)
    {
        var t = Setup(root, "copy-busy"); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, _) =>
        { if (stage == "Transferring") { entered.Set(); if (!release.Wait(5000)) throw new IOException("Test timeout"); } });
        var request = new CopyRequest(Snapshot(t.Source), t.Target, "测试"); var first = service.CopyAsync(request);
        Check(entered.Wait(5000), "Transfer did not enter");
        try { Check(service.CopyAsync(request).GetAwaiter().GetResult().State == "Skipped", "Concurrent operation queued"); }
        finally { release.Set(); }
        Check(first.GetAwaiter().GetResult().State == "Completed", "First copy failed"); Intact(t.Source, t.Target, 1);
    }
    private static void Special(string root)
    {
        var t = Setup(root, "copy-special");
        // Framework's managed File API rejects ADS before the fixture exists.
        // Create the same NTFS test stream through Win32, then retain the original assertions.
        using (var alternate = new FileStream(LightView.App.Platform.WindowsFileHandle.Open(t.Source + ":extra", 0x40000000, 2), FileAccess.Write))
            alternate.Write(System.Text.Encoding.UTF8.GetBytes("alternate data"));
        Check(Copy(new(new OperationJournal(t.Logs)), Snapshot(t.Source), t.Target).State == "Failed", "Alternate stream silently discarded"); Intact(t.Source, t.Target, 0);
    }
    private static void Loading(string root)
    {
        var loader = new BlockingLoader();
        var viewer = new ViewerViewModel(loader);
        viewer.OpenAsync(Path.Combine(root, "sample.png")).GetAwaiter().GetResult();
        Check(viewer.OrganizingImage?.FileVersion is not null, "Displayed version absent");
        var displayed = viewer.OrganizingImage;
        loader.Block = true;
        var pending = viewer.OpenAsync(Path.Combine(root, "sample.png"));
        Check(loader.Entered.Wait(5000), "Second load not in flight");
        try { Check(viewer.OrganizingImage is null && ReferenceEquals(viewer.DisplayedImage, displayed), "Loading allowed old display organizing"); }
        finally { loader.Release.Set(); }
        pending.GetAwaiter().GetResult();
    }
    private sealed class BlockingLoader : IImageLoader
    {
        public bool Block;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public LoadedImage Load(string path, CancellationToken cancellation)
        {
            if (Block) { Entered.Set(); if (!Release.Wait(5000)) throw new IOException("Test timeout"); }
            return new WicImageLoader().Load(path, cancellation);
        }
    }
    private sealed class FailingJournal(string directory, string stage) : IOperationJournal
    {
        private readonly OperationJournal journal = new(directory);
        public void Append(CopyRecord record) { if (record.State == stage) throw new IOException("Injected journal failure"); journal.Append(record); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
}

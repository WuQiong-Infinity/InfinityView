using System.Diagnostics;
using System.IO;
using LightView.App.Organizing;
using LightView.Core.Commands;

namespace LightView.Tests;

internal static class UndoRecoveryTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("undo copy moves only its generated file to retained recovery area", () => UndoCopy(root)),
        ("undo move restores original name and uses last in first out", () => UndoMove(root)),
        ("undo occupied original refuses then explicitly renames without overwrite", () => Conflict(root)),
        ("modified replaced and missing destinations refuse undo without dropping history", () => Stale(root)),
        ("unwritable recovery area keeps destination and undo record", () => RecoveryUnavailable(root)),
        ("undo journal failure after actual mutation retains trustworthy evidence", () => UndoLogFailure(root)),
        ("session undo retains at most 100 successful operations", () => HistoryLimit(root)),
        ("undo shortcut is repeat suppressed and preserves text undo", Shortcut),
        ("startup recovery preserves all files across real process exits", () => ProcessExits(root)),
        ("truncated recovery journal stays visible and cannot be acknowledged blindly", () => Truncated(root)),
        ("acknowledge recovery records keep-all without moving or deleting data", () => Acknowledge(root)),
        ("cross volume undo move restores raw bytes safely", () => CrossUndo(root)),
        ("cross volume move process exits preserve accurate source removal state", () => MoveProcessExits(root))
    ];
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static (string Source, string Target, string Logs) Setup(string root, string name)
    {
        string directory = Path.Combine(root, name), target = Path.Combine(directory, "target"); Directory.CreateDirectory(target);
        string source = Path.Combine(directory, "source.png"); File.Copy(Path.Combine(root, "sample.png"), source);
        return (source, target, Path.Combine(directory, "journal"));
    }
    private static FileSnapshot Snap(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return FileSnapshot.Capture(path, stream); }
    private static CopyResult Copy(SafeCopyService service, string source, string target, bool move = false)
        => (move ? service.MoveAsync(new(Snap(source), target, "测试")) : service.CopyAsync(new(Snap(source), target, "测试"))).GetAwaiter().GetResult();
    private static void UndoCopy(string root)
    {
        var t = Setup(root, "undo-copy"); var service = new SafeCopyService(new OperationJournal(t.Logs)); var made = Copy(service, t.Source, t.Target);
        var result = service.UndoAsync().GetAwaiter().GetResult();
        Check(result.State == "Completed" && File.Exists(t.Source) && !File.Exists(made.Destination), result.Message);
        Check(result.Destination!.Contains(".lightview-undo") && Snap(result.Destination).Sha256 == Snap(t.Source).Sha256 && service.UndoCount == 0, "Recovery copy lost or history incorrect");
        Check(JournalRecovery.Read(t.Logs).Items.Count == 0, "Successful undo reported unresolved");
        Check(JournalRecovery.Read(t.Logs, true).Items.Single().IsRetainedCopy, "Manual recovery view cannot locate retained copy");
        Check(Copy(service, t.Source, t.Target).State == "Completed", "Undone duplicate prevented new copy");
    }
    private static void UndoMove(string root)
    {
        var t = Setup(root, "undo-move"); var service = new SafeCopyService(new OperationJournal(t.Logs)); var snap = Snap(t.Source);
        File.WriteAllText(Path.Combine(t.Target, "source.png"), "existing"); var moved = Copy(service, t.Source, t.Target, true);
        string second = Path.Combine(Path.GetDirectoryName(t.Source)!, "second.png"); File.WriteAllText(second, "second source");
        Copy(service, second, t.Target, true);
        var result = service.UndoAsync().GetAwaiter().GetResult(); Check(result.State == "Completed" && File.Exists(second) && !File.Exists(t.Source), "Not LIFO");
        result = service.UndoAsync().GetAwaiter().GetResult();
        Check(result.State == "Completed" && result.Destination == t.Source && Snap(t.Source).Sha256 == snap.Sha256 && !File.Exists(moved.Destination), "Original name/content not restored");
        Check(File.ReadAllText(Path.Combine(t.Target, "source.png")) == "existing", "Existing target overwritten on undo");
    }
    private static void Conflict(string root)
    {
        var t = Setup(root, "undo-conflict"); var service = new SafeCopyService(new OperationJournal(t.Logs)); Copy(service, t.Source, t.Target, true);
        File.WriteAllText(t.Source, "occupant"); var result = service.UndoAsync().GetAwaiter().GetResult();
        Check(result.State == "Conflict" && service.UndoCount == 1 && File.ReadAllText(t.Source) == "occupant", "Conflict did not preserve both");
        result = service.UndoAsync(true).GetAwaiter().GetResult();
        Check(result.State == "Completed" && Path.GetFileName(result.Destination) == "source (1).png" && File.ReadAllText(t.Source) == "occupant", "Explicit renamed restore failed");
    }
    private static void Stale(string root)
    {
        foreach (string change in new[] { "edit", "replace", "delete" })
        {
            var t = Setup(root, "undo-stale-" + change); var service = new SafeCopyService(new OperationJournal(t.Logs)); var made = Copy(service, t.Source, t.Target);
            var original = Snap(made.Destination!); byte[] bytes = File.ReadAllBytes(made.Destination!); var time = File.GetLastWriteTimeUtc(made.Destination!);
            if (change == "edit") { bytes[bytes.Length - 1] ^= 1; File.WriteAllBytes(made.Destination!, bytes); File.SetLastWriteTimeUtc(made.Destination!, time); }
            else if (change == "replace") { File.Move(made.Destination!, made.Destination + ".old"); File.WriteAllBytes(made.Destination!, bytes); File.SetLastWriteTimeUtc(made.Destination!, time); }
            else File.Delete(made.Destination!);
            var result = service.UndoAsync().GetAwaiter().GetResult();
            Check(result.State == "Failed" && service.UndoCount == 1 && File.Exists(t.Source), "Changed destination incorrectly undone");
            if (change != "delete") Check(Snap(made.Destination!).SameVersion(original) == false, "Expected changed fixture");
        }
    }
    private static void RecoveryUnavailable(string root)
    {
        var t = Setup(root, "undo-recovery-blocked"); var service = new SafeCopyService(new OperationJournal(t.Logs)); var made = Copy(service, t.Source, t.Target);
        File.WriteAllText(Path.Combine(t.Target, ".lightview-undo"), "blocking file");
        var result = service.UndoAsync().GetAwaiter().GetResult();
        Check(result.State == "Failed" && File.Exists(made.Destination) && service.UndoCount == 1, "Failed recovery removed data/history");
    }
    private static void UndoLogFailure(string root)
    {
        var t = Setup(root, "undo-log-failure"); var service = new SafeCopyService(new UndoneFailJournal(t.Logs)); var made = Copy(service, t.Source, t.Target);
        var result = service.UndoAsync().GetAwaiter().GetResult();
        Check(result.State == "NeedsRecovery" && File.Exists(result.Destination) && !File.Exists(made.Destination) && service.UndoCount == 1, result.Message);
        Check(JournalRecovery.Read(t.Logs).Items.Count == 1, "Actual undo/log failure is not visible at startup");
        Check(Directory.GetFiles(t.Logs, "*.jsonl").Any(path => File.ReadAllText(path).Contains("RelatedOperationId")), "Linked undo evidence absent");
    }
    private static void HistoryLimit(string root)
    {
        var t = Setup(root, "undo-limit"); var service = new SafeCopyService(new OperationJournal(t.Logs));
        for (int i = 0; i < 101; i++)
        {
            string file = Path.Combine(Path.GetDirectoryName(t.Source)!, i + ".png"); File.WriteAllBytes(file, [(byte)i]);
            Check(Copy(service, file, t.Target).State == "Completed", "History setup failed");
        }
        Check(service.UndoCount == 100, "Undo history not bounded");
    }
    private static void Shortcut()
    {
        var map = new ShortcutMap(CommandCatalog.DefaultBindings());
        Check(map.Resolve("CTRL+Z")?.Id == CommandCatalog.Undo && map.Resolve("CTRL+Z", isRepeat: true) is null &&
            map.Resolve("CTRL+Z", textEntry: true) is null, "Undo intercepted text or repeated");
    }
    private static void ProcessExits(string root)
    {
        foreach (string stage in new[] { "Prepared", "Transferring", "Verifying", "CommitPlanned", "DestinationCommitted" })
        {
            var t = Setup(root, "recovery-exit-" + stage);
            var start = new ProcessStartInfo(FrameworkApi.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(FrameworkApi.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) FrameworkApi.AddArgument(start, typeof(UndoRecoveryTests).Assembly.Location);
            foreach (string arg in new[] { "--crash-file-op", t.Source, t.Target, t.Logs, stage, "Copy" }) FrameworkApi.AddArgument(start, arg);
            using var process = Process.Start(start)!; Check(process.WaitForExit(10000) && process.ExitCode == 77, "Child did not stop at " + stage);
            var before = Directory.GetFiles(t.Target).ToDictionary(path => path, path => Snap(path).Sha256);
            var recovered = JournalRecovery.Read(t.Logs);
            Check(recovered.Items.Count == 1 && recovered.Items[0].Record?.State == stage && File.Exists(t.Source), "Interrupted stage not recognized");
            Check(before.All(pair => File.Exists(pair.Key) && Snap(pair.Key).Sha256 == pair.Value) && Directory.GetFiles(t.Target).Length == before.Count, "Recovery mutated data");
        }
    }
    public static int CrashChild(string source, string target, string logs, string stage, string kind)
    {
        using var lease = OperationJournal.AcquireSession(logs);
        var service = new SafeCopyService(new OperationJournal(logs), (s, _) => { if (s == stage) Environment.Exit(77); });
        return Copy(service, source, target, kind == "Move").State == "Completed" ? 0 : 1;
    }
    private static void MoveProcessExits(string root)
    {
        foreach (string stage in new[] { "SourceRemovalPlanned", "SourceRemoved" })
        {
            var t = Setup(root, "move-recovery-exit-" + stage);
            string target = Path.Combine(Path.GetTempPath(), "LightView-crash-isolated-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
            var original = Snap(t.Source);
            var start = new ProcessStartInfo(FrameworkApi.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(FrameworkApi.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) FrameworkApi.AddArgument(start, typeof(UndoRecoveryTests).Assembly.Location);
            foreach (string arg in new[] { "--crash-file-op", t.Source, target, t.Logs, stage, "Move" }) FrameworkApi.AddArgument(start, arg);
            using var process = Process.Start(start)!;
            Check(process.WaitForExit(10000) && process.ExitCode == 77, "Move child did not stop at " + stage);
            var scan = JournalRecovery.Read(t.Logs);
            Check(scan.Items.Count == 1 && scan.Items[0].Record?.State == stage, "Move recovery state incorrect");
            Check(File.Exists(t.Source) == (stage == "SourceRemovalPlanned") && Snap(Path.Combine(target, "source.png")).Sha256 == original.Sha256,
                "Recovery confused source removal or modified target");
        }
    }
    private static void Truncated(string root)
    {
        var t = Setup(root, "recovery-truncated"); var record = Record(t.Source, t.Target); var journal = new OperationJournal(t.Logs); journal.Append(record);
        string path = Path.Combine(t.Logs, record.OperationId + ".jsonl"); File.AppendAllText(path, "{\"operation\":");
        var scan = JournalRecovery.Read(t.Logs); Check(scan.Items.Count == 1 && scan.Items[0].Problem.Length > 0, "Truncated journal ignored");
        bool denied = false; try { JournalRecovery.KeepAll(t.Logs, scan.Items[0]); } catch (IOException) { denied = true; }
        Check(denied && File.Exists(t.Source), "Corrupt journal acknowledged blindly");
    }
    private static CopyRecord Record(string source, string target) => new(Guid.NewGuid().ToString("N"), "Prepared", Snap(source), target, Path.Combine(target, ".owned.tmp"));
    private static void Acknowledge(string root)
    {
        var t = Setup(root, "recovery-keep-all"); var record = Record(t.Source, t.Target); new OperationJournal(t.Logs).Append(record);
        File.WriteAllText(record.TemporaryPath, "partial"); var before = Snap(t.Source); var temp = Snap(record.TemporaryPath);
        JournalRecovery.KeepAll(t.Logs, JournalRecovery.Read(t.Logs).Items.Single());
        Check(JournalRecovery.Read(t.Logs).Items.Count == 0 && before.SameVersion(Snap(t.Source)) && temp.SameVersion(Snap(record.TemporaryPath)), "Keep-all changed data");
    }
    private static void CrossUndo(string root)
    {
        var t = Setup(root, "undo-cross"); string target = Path.Combine(Path.GetTempPath(), "LightView-undo-isolated-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
        var original = Snap(t.Source); var service = new SafeCopyService(new OperationJournal(t.Logs)); var moved = Copy(service, t.Source, target, true);
        Check(moved.State == "Completed" && !File.Exists(t.Source), moved.Message);
        var result = service.UndoAsync().GetAwaiter().GetResult();
        Check(result.State == "Completed" && Snap(t.Source).Sha256 == original.Sha256 && !File.Exists(moved.Destination), result.Message);
    }
    private sealed class UndoneFailJournal(string directory) : IOperationJournal
    {
        public void Append(CopyRecord record) { if (record.State == "Undone") throw new IOException("Injected undone log fail"); new OperationJournal(directory).Append(record); }
    }
}

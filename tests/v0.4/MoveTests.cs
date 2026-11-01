using System.IO;
using LightView.App.Organizing;
using LightView.App.Imaging;
using LightView.App.ViewModels;
using LightView.App.Views;
using System.Windows;

namespace LightView.Tests;

internal static class MoveTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("same volume move preserves identity and never overwrites", () => SameVolume(root)),
        ("same volume cancellation before commit preserves source", () => Cancel(root)),
        ("same volume log failure after rename reports moved recovery", () => RenameLogFailure(root)),
        ("real cross volume move verifies bytes then removes original handle", () => CrossVolume(root, false)),
        ("cross volume delete failure retains both and reports incomplete move", () => CrossVolume(root, true)),
        ("moving advances next then previous and clears an empty folder", () => Navigation(root)),
        ("move completion cannot pull back a newer viewed image", () => NewerView(root)),
        ("move without advance updates path and preserves viewport", () => NoAdvance(root)),
        ("failed new navigation still prevents an old move auto advance", () => FailedNavigation(root))
    ];
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static (string Source, string Target, string Logs) Setup(string root, string name)
    {
        string folder = Path.Combine(root, name), target = Path.Combine(folder, "target"); Directory.CreateDirectory(target);
        string source = Path.Combine(folder, "source.png"); File.Copy(Path.Combine(root, "sample.png"), source);
        return (source, target, Path.Combine(folder, "journal"));
    }
    private static FileSnapshot Snap(string file)
    { using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); return FileSnapshot.Capture(file, stream); }
    private static void SameVolume(string root)
    {
        var t = Setup(root, "move-same"); var snap = Snap(t.Source); File.WriteAllText(Path.Combine(t.Target, "source.png"), "occupied");
        var result = new SafeCopyService(new OperationJournal(t.Logs)).MoveAsync(new(snap, t.Target, "废片")).GetAwaiter().GetResult();
        Check(result.State == "Completed" && !File.Exists(t.Source), result.Message);
        Check(result.Record?.DestinationVersion?.Identity == snap.Identity && result.Record.Kind == "Move", "Atomic move lost identity");
        Check(Path.GetFileName(result.Destination) == "source (1).png" && Snap(result.Destination!).Sha256 == snap.Sha256, "Conflict or bytes incorrect");
        Check(!File.ReadAllText(Directory.GetFiles(t.Logs, "*.jsonl").Single()).Contains("Transferring"), "Same volume copied instead of renaming");
    }
    private static void Cancel(string root)
    {
        var t = Setup(root, "move-cancel"); using var cts = new CancellationTokenSource();
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, _) => { if (stage == "CommitPlanned") cts.Cancel(); });
        var result = service.MoveAsync(new(Snap(t.Source), t.Target, "废片"), cts.Token).GetAwaiter().GetResult();
        Check(result.State == "Cancelled" && File.Exists(t.Source) && !Directory.GetFiles(t.Target).Any(), result.Message);
    }
    private static void RenameLogFailure(string root)
    {
        var t = Setup(root, "move-rename-log-failure");
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, _) => { if (stage == "DestinationCommitted") throw new IOException("Injected log fail"); });
        var result = service.MoveAsync(new(Snap(t.Source), t.Target, "废片")).GetAwaiter().GetResult();
        Check(result.State == "NeedsRecovery" && !File.Exists(t.Source) && File.Exists(result.Destination), result.Message);
        Check(!result.Message.Contains("原文件仍保留"), "Moved source incorrectly claimed present");
    }
    private static void CrossVolume(string root, bool failDeletion)
    {
        var t = Setup(root, "move-cross-" + failDeletion); string target = Path.Combine(Path.GetTempPath(), "LightView-isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var snapshot = Snap(t.Source);
        using (var targetHandle = LightView.App.Platform.WindowsFileHandle.Open(target, 0x80, directory: true))
            Check(LightView.App.Platform.WindowsFileHandle.Facts(targetHandle).Identity.Substring(0, 16) != snapshot.Identity.Substring(0, 16), "Actual second volume required for this test");
        var service = new SafeCopyService(new OperationJournal(t.Logs), (stage, _) =>
        { if (failDeletion && stage == "SourceRemovalPlanned") throw new IOException("Injected source removal failure"); });
        var result = service.MoveAsync(new(snapshot, target, "跨卷废片")).GetAwaiter().GetResult();
        Check(result.State == (failDeletion ? "NeedsRecovery" : "Completed"), result.Message);
        Check(File.Exists(t.Source) == failDeletion && Snap(result.Destination!).Sha256 == snapshot.Sha256, "Cross volume move lost bytes or source state");
        string journal = File.ReadAllText(Directory.GetFiles(t.Logs, "*.jsonl").Single());
        Check(journal.Contains("Transferring") && journal.Contains("DestinationCommitted"), "Actual cross volume transfer was not journaled");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(t.Source)!, "cross-volume-target.txt"), target);
    }
    private static (ViewerViewModel Viewer, string Folder, string Target) Gallery(string root, string name)
    {
        string folder = Path.Combine(root, name), target = Path.Combine(folder, "target"); Directory.CreateDirectory(target);
        foreach (string namePart in new[] { "1.png", "2.png", "3.png" }) File.Copy(Path.Combine(root, "sample.png"), Path.Combine(folder, namePart));
        var viewer = new ViewerViewModel(new WicImageLoader()); viewer.OpenAsync(Path.Combine(folder, "1.png")).GetAwaiter().GetResult(); viewer.FolderReady.GetAwaiter().GetResult();
        return (viewer, folder, target);
    }
    private static FileSnapshot MoveDisplayed(ViewerViewModel viewer, string target)
    {
        var image = viewer.OrganizingImage!;
        var result = new SafeCopyService(new OperationJournal(Path.Combine(target, "journal"))).MoveAsync(new(image.FileVersion!, target, "测试")).GetAwaiter().GetResult();
        Check(result.State == "Completed", result.Message); return result.Record!.DestinationVersion!;
    }
    private static void Navigation(string root)
    {
        var t = Gallery(root, "move-navigation");
        foreach (string expected in new[] { "2.png", "3.png", "" })
        {
            var original = t.Viewer.OrganizingImage!; var destination = MoveDisplayed(t.Viewer, t.Target);
            t.Viewer.ApplyMoveAsync(original, destination, true).GetAwaiter().GetResult(); t.Viewer.FolderReady.GetAwaiter().GetResult();
            Check((t.Viewer.DisplayedImage is null ? "" : Path.GetFileName(t.Viewer.DisplayedImage.FilePath)) == expected, "Next/empty selection incorrect");
        }
        Check(t.Viewer.ActiveDirectory == t.Folder, "Empty viewer lost the related source directory for undo");
        var previous = Gallery(root, "move-previous"); previous.Viewer.OpenAsync(Path.Combine(previous.Folder, "3.png")).GetAwaiter().GetResult();
        var image = previous.Viewer.OrganizingImage!; var moved = MoveDisplayed(previous.Viewer, previous.Target);
        previous.Viewer.ApplyMoveAsync(image, moved, true).GetAwaiter().GetResult();
        Check(Path.GetFileName(previous.Viewer.DisplayedImage!.FilePath) == "2.png", "Last image did not choose previous");
    }
    private static void NewerView(string root)
    {
        var t = Gallery(root, "move-newer-view"); var original = t.Viewer.OrganizingImage!; var destination = MoveDisplayed(t.Viewer, t.Target);
        t.Viewer.OpenAsync(Path.Combine(t.Folder, "3.png")).GetAwaiter().GetResult();
        t.Viewer.ApplyMoveAsync(original, destination, true).GetAwaiter().GetResult();
        Check(Path.GetFileName(t.Viewer.DisplayedImage!.FilePath) == "3.png", "Old completion navigated newer image");
    }
    private static void NoAdvance(string root)
    {
        var t = Gallery(root, "move-no-advance"); var image = t.Viewer.OrganizingImage!;
        var viewport = new ImageViewport { Image = image, Width = 32, Height = 16 };
        viewport.Measure(new Size(32, 16)); viewport.Arrange(new Rect(0, 0, 32, 16)); viewport.ActualPixels();
        var moved = MoveDisplayed(t.Viewer, t.Target); t.Viewer.ApplyMoveAsync(image, moved, false).GetAwaiter().GetResult();
        viewport.Image = t.Viewer.DisplayedImage;
        Check(t.Viewer.OrganizingImage!.FilePath == moved.Path && !viewport.Transform.IsFit && viewport.Transform.Zoom == 1, "Move path/viewport wrong");
    }
    private static void FailedNavigation(string root)
    {
        var t = Gallery(root, "move-failed-navigation"); var original = t.Viewer.OrganizingImage!;
        int version = t.Viewer.NavigationVersion; var moved = MoveDisplayed(t.Viewer, t.Target);
        t.Viewer.OpenAsync(Path.Combine(t.Folder, "missing.png")).GetAwaiter().GetResult();
        t.Viewer.ApplyMoveAsync(original, moved, true, version).GetAwaiter().GetResult();
        Check(t.Viewer.DisplayedImage!.FilePath == moved.Path, "Failed new navigation allowed old auto advance");
    }
}

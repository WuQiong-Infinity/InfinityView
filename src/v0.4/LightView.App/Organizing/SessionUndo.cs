using System.IO;
using LightView.App.Platform;

namespace LightView.App.Organizing;

internal sealed partial class SafeCopyService
{
    private readonly List<CopyRecord> history = [];
    private readonly object historyLock = new();
    public int UndoCount { get { lock (historyLock) return history.Count; } }

    public async Task<CopyResult> UndoAsync(bool allowRename = false, CancellationToken cancellation = default)
    {
        if (!gate.Wait(0)) return new("Skipped", "文件操作正在进行，本次撤销未排队。");
        try
        {
            CopyRecord? original;
            lock (historyLock) original = history.LastOrDefault();
            if (original?.DestinationVersion is null) return new("Skipped", "本次会话没有可撤销操作。");
            var result = await Task.Run(() => Undo(original, allowRename, cancellation));
            if (result.State == "Completed") lock (historyLock) history.RemoveAt(history.Count - 1);
            return result;
        }
        finally { gate.Release(); }
    }

    private CopyResult Undo(CopyRecord original, bool allowRename, CancellationToken cancellation)
    {
        CopyResult? result = null;
        try
        {
            FileSnapshot generated = original.DestinationVersion!;
            string parent = Path.GetDirectoryName(generated.Path)!;
            using var parentLease = WindowsFileHandle.PinDirectory(parent);
            string target;
            if (original.Kind == "Copy")
            {
                string recoveryRoot = Path.Combine(parent, ".lightview-undo");
                Directory.CreateDirectory(recoveryRoot);
                using var recoveryLease = WindowsFileHandle.PinDirectory(recoveryRoot);
                target = Path.Combine(recoveryRoot, original.OperationId);
                Directory.CreateDirectory(target);
            }
            else target = Path.GetDirectoryName(original.Source.Path)!;
            result = Copy(new(generated, target, original.Kind == "Copy" ? "撤销恢复区" : "原位置", Path.GetFileName(original.Source.Path)),
                true, cancellation, original.Kind == "Copy" || allowRename, "Undo" + original.Kind, original.OperationId);
            if (result.State != "Completed") return result;
            journal.Append(original with { State = "Undone" });
            return result with { Message = original.Kind == "Copy"
                ? "已撤销复制；副本保留在 .lightview-undo 恢复区，不会自动清理。"
                : "已撤销移动 · " + Path.GetFileName(result.Destination) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (result?.State == "Completed" && result.Record is { } record)
            {
                try { journal.Append(record with { State = "NeedsRecovery", Error = error.Message }); }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            }
            return result?.State == "Completed"
                ? result with { State = "NeedsRecovery", Message = "撤销的文件操作已完成，但记录未完成；请检查恢复日志。" }
                : new("Failed", "未完成撤销：" + error.Message);
        }
    }
}

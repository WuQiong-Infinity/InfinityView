using System.IO;
using System.Security.Cryptography;
using LightView.App.Platform;
using LightView.Core.Settings;

namespace LightView.App.Organizing;

internal sealed record CopyRequest(FileSnapshot Source, string TargetDirectory, string PresetName, string? TargetName = null,
    FileConflictPolicy ConflictPolicy = FileConflictPolicy.Rename);
internal sealed record CopyResult(string State, string Message, string? Destination = null, CopyRecord? Record = null);

internal sealed partial class SafeCopyService(IOperationJournal journal, Action<string, CopyRecord>? fault = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, FileSnapshot> completed = new(StringComparer.OrdinalIgnoreCase);
    public Task<CopyResult> CopyAsync(CopyRequest request, CancellationToken cancellation = default) => RunAsync(request, false, cancellation);
    public Task<CopyResult> MoveAsync(CopyRequest request, CancellationToken cancellation = default) => RunAsync(request, true, cancellation);
    private async Task<CopyResult> RunAsync(CopyRequest request, bool move, CancellationToken cancellation)
    {
        if (!gate.Wait(0)) return new("Skipped", "文件操作正在进行，本次按键未排队。");
        try
        {
            var result = await Task.Run(() => Copy(request, move, cancellation));
            if (result.State == "Completed" && result.Record is { } record)
                lock (historyLock) { history.Add(record); if (history.Count > 100) history.RemoveAt(0); }
            return result;
        }
        finally { gate.Release(); }
    }

    private CopyResult Copy(CopyRequest request, bool move, CancellationToken cancellation,
        bool renameOnConflict = true, string? kind = null, string? relatedOperation = null)
    {
        CopyRecord? record = null;
        bool committed = false, sourceRemoved = false, cleaned = true;
        FileStream? temporary = null;
        void State(string state)
        {
            record = record! with { State = state };
            journal.Append(record);
            fault?.Invoke(state, record);
        }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            string sourcePath = Path.GetFullPath(request.Source.Path), target = FrameworkApi.TrimEndingDirectorySeparator(Path.GetFullPath(request.TargetDirectory));
            if (sourcePath.IndexOf(':', 2) >= 0 || !FrameworkApi.IsPathFullyQualified(request.TargetDirectory)) throw new NotSupportedException("整理路径无效。");
            using var sourceDirectories = WindowsFileHandle.PinDirectory(Path.GetDirectoryName(sourcePath)!);
            using var targetDirectories = WindowsFileHandle.PinDirectory(target);
            using var source = new FileStream(WindowsFileHandle.Open(sourcePath, move ? 0x80010000u : 0x80000000u), FileAccess.Read);
            WindowsFileHandle.EnsureOrdinary(source.SafeFileHandle);
            var version = FileSnapshot.Capture(sourcePath, source);
            if (!version.SameVersion(request.Source)) throw new IOException("图片文件已变化，请重新打开后再整理。");
            string targetName = request.TargetName ?? Path.GetFileName(sourcePath);
            if (FrameworkApi.IsNullOrWhiteSpace(targetName) || Path.GetFileName(targetName) != targetName || targetName.IndexOf(':') >= 0)
                throw new ArgumentException("目标文件名无效。");
            string destination = Path.Combine(target, targetName);
            if (!FrameworkApi.IsEnumDefined(request.ConflictPolicy)) throw new ArgumentException("同名冲突策略无效。");
            if (File.Exists(destination))
            {
                using var candidate = new FileStream(WindowsFileHandle.Open(destination), FileAccess.Read);
                if (WindowsFileHandle.Facts(candidate.SafeFileHandle).Identity == version.Identity)
                    return new("NoOp", "已在目标位置。", destination);
            }
            string key = version.Identity + ":" + version.Sha256 + ":" + target;
            if (!move && completed.TryGetValue(key, out var previous))
            {
                try
                {
                    using var prior = new FileStream(WindowsFileHandle.Open(previous.Path), FileAccess.Read);
                    if (previous.SameVersion(FileSnapshot.Capture(previous.Path, prior))) return new("NoOp", "本次会话已复制到此目标。", previous.Path);
                }
                catch (IOException) { }
                completed.Remove(key);
            }
            if (kind is null && request.ConflictPolicy != FileConflictPolicy.Rename && FrameworkApi.PathExists(destination))
                return ConflictResult(request.ConflictPolicy);
            using var targetHandle = WindowsFileHandle.Open(target, 0x80, directory: true);
            bool renameOnly = move && WindowsFileHandle.Facts(targetHandle).Identity.Substring(0, 16) == version.Identity.Substring(0, 16);
            if (!renameOnly && new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < version.Length)
                throw new IOException("目标磁盘可用空间不足。");
            string id = Guid.NewGuid().ToString("N"), tempPath = Path.Combine(target, ".lightview-" + id + ".tmp");
            record = new(id, "Prepared", version, target, renameOnly ? "" : tempPath, destination,
                Kind: kind ?? (move ? "Move" : "Copy"), RelatedOperationId: relatedOperation);
            State("Prepared");
            if (!renameOnly)
            {
            temporary = new FileStream(WindowsFileHandle.Open(tempPath, 0xC0010000, 1), FileAccess.ReadWrite);
            cleaned = false;
            State("Transferring");
            byte[] buffer = new byte[128 * 1024];
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                temporary.Write(buffer, 0, read);
            }
            temporary.Flush(true);
            State("Verifying");
            temporary.Position = 0;
            if (temporary.Length != version.Length || FrameworkApi.Hex(FrameworkApi.Sha256(temporary)) != version.Sha256)
                throw new IOException("副本内容校验失败，源文件已保留。");
            WindowsFileHandle.PreserveWriteTime(temporary.SafeFileHandle, version.LastWrite);
            }
            for (int number = 0; ; number++)
            {
                if (number > 10000) throw new IOException("同名文件过多，未提交副本。");
                if (!renameOnConflict && number > 0) throw new DestinationConflictException();
                destination = Path.Combine(target, number == 0 ? targetName
                    : $"{Path.GetFileNameWithoutExtension(targetName)} ({number}){Path.GetExtension(targetName)}");
                record = record with { Destination = destination };
                State("CommitPlanned");
                cancellation.ThrowIfCancellationRequested();
                try { WindowsFileHandle.Rename(renameOnly ? source.SafeFileHandle : temporary!.SafeFileHandle, destination); break; }
                catch (IOException error) when ((error.HResult & 0xffff) is 80 or 183)
                {
                    if (kind is null && request.ConflictPolicy != FileConflictPolicy.Rename)
                        throw new PolicyConflictException(request.ConflictPolicy);
                }
            }
            committed = true;
            sourceRemoved = renameOnly;
            var destinationVersion = FileSnapshot.Capture(destination, renameOnly ? source : temporary!);
            record = record with { DestinationVersion = destinationVersion };
            State("DestinationCommitted");
            if (move)
            {
                if (!sourceRemoved)
                {
                    State("SourceRemovalPlanned");
                    WindowsFileHandle.MarkForDeletion(source.SafeFileHandle);
                    source.Dispose();
                    sourceRemoved = true;
                }
                State("SourceRemoved");
            }
            State("Completed");
            if (!move) completed[key] = destinationVersion;
            return new("Completed", $"已{(move ? "移动" : "复制")}到 {request.PresetName} · {Path.GetFileName(destination)}", destination, record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or OperationCanceledException)
        {
            if (!committed && temporary is not null)
            {
                try { WindowsFileHandle.MarkForDeletion(temporary.SafeFileHandle); cleaned = true; }
                catch (IOException) { cleaned = false; }
            }
            string state = committed || !cleaned ? "NeedsRecovery" : error is PolicyConflictException policy ? ConflictResult(policy.Policy).State
                : error is DestinationConflictException ? "Conflict"
                : error is OperationCanceledException ? "Cancelled" : "Failed";
            if (record is not null)
            {
                record = record with { State = state, Error = error.Message };
                try { journal.Append(record); } catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            }
            if (!committed && cleaned && error is PolicyConflictException conflict)
                return ConflictResult(conflict.Policy) with { Record = record };
            return new(state, committed ? move
                    ? sourceRemoved ? "文件已移至目标，但操作记录未完成；请查看恢复日志。" : "副本已建立，原文件仍保留，移动未完成；请查看恢复日志。"
                    : "副本已建立，但操作记录未完成；已保留源与副本，请查看恢复日志。"
                : state == "Cancelled" ? "文件操作已取消，源文件已保留。" : "未完成文件操作：" + error.Message, committed ? record?.Destination : null, record);
        }
        finally { temporary?.Dispose(); }
    }
    private sealed class DestinationConflictException() : IOException("原位置已被占用；可选择改名恢复或取消。");
    private sealed class PolicyConflictException(FileConflictPolicy policy) : IOException("目标同名文件已存在，未覆盖。")
    { public FileConflictPolicy Policy { get; } = policy; }
    private static CopyResult ConflictResult(FileConflictPolicy policy) => policy == FileConflictPolicy.Skip
        ? new("Skipped", "已跳过：目标存在同名项目，源文件与原目标均已保留。")
        : new("Conflict", "目标存在同名项目，是否自动改名并保留双方？");
}

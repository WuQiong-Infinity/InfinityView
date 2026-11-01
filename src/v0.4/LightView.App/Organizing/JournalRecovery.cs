using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace LightView.App.Organizing;

internal sealed record RecoveryItem(string JournalPath, string Fingerprint, CopyRecord? Record, string Problem)
{
    public string OperationId => Path.GetFileNameWithoutExtension(JournalPath);
    public bool IsRetainedCopy => Record is { Kind: "UndoCopy", State: "Completed" };
    public string State => Problem.Length > 0 ? Problem : IsRetainedCopy ? "已撤销，副本保留在恢复区" : Record?.State switch
    {
        "Prepared" => "准备完成，尚未提交", "Transferring" => "传输中断", "Verifying" => "校验期间中断",
        "CommitPlanned" => "提交前后需核对", "DestinationCommitted" => "目标已提交，操作未结束",
        "SourceRemovalPlanned" => "目标已建立，需核对源", "SourceRemoved" => "源已移除，日志未结束",
        "NeedsRecovery" => "需要核对", _ => "状态需人工核对"
    };
    public string Source => Record?.Source?.Path ?? "未知";
    public string Destination => Record?.Destination ?? "未提交";
    public string Temporary => Record?.TemporaryPath ?? "";
    public string ExistingFiles => $"源：{(File.Exists(Source) ? "存在" : "不存在/未知")}；目标：{(File.Exists(Destination) ? "存在" : "不存在/未知")}；临时：{(File.Exists(Temporary) ? "存在" : "不存在/无")}（仅存在检查，未证明内容一致）\n记录的文件大小：{Record?.Source?.Length:N0} 字节；恢复区不会自动清理，实际磁盘占用请打开目标位置查看。";
}
internal sealed record RecoveryScan(IReadOnlyList<RecoveryItem> Items, string? Warning);

internal static class JournalRecovery
{
    public static RecoveryScan Read(string directory, bool includeRetained = false)
    {
        var results = new List<RecoveryItem>();
        if (!Directory.Exists(directory)) return new(results, null);
        try
        {
            string[] files = Directory.EnumerateFiles(directory, "*.jsonl").Take(5001).ToArray();
            foreach (string file in files.Take(5000))
            {
                CopyRecord? last = null; string problem = "", fingerprint = "";
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > 8 * 1024 * 1024) throw new IOException("日志超过读取上限");
                    fingerprint = FrameworkApi.Hex(FrameworkApi.Sha256(stream)); stream.Position = 0;
                    using var reader = new StreamReader(stream);
                    int lines = 0;
                    while (reader.ReadLine() is { } line)
                    {
                        if (++lines > 200) throw new IOException("日志记录超过读取上限");
                        using var json = JsonDocument.Parse(line);
                        var record = json.RootElement.GetProperty("operation").Deserialize<CopyRecord>();
                        if (record is null || !Guid.TryParseExact(record.OperationId, "N", out _) ||
                            record.OperationId != Path.GetFileNameWithoutExtension(file) || record.Source is null)
                            throw new IOException("日志身份无效");
                        last = record;
                    }
                    if (last is null) problem = "日志为空";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
                { problem = "日志需人工核对：" + error.Message; }
                if (problem.Length > 0 || last?.State is not ("Completed" or "Undone" or "Cancelled" or "Skipped" or "Conflict" or "Failed" or "AcknowledgedKeepAll") ||
                    includeRetained && last is { Kind: "UndoCopy", State: "Completed" })
                    results.Add(new(file, fingerprint, last, problem));
            }
            return new(results, files.Length > 5000 ? "日志数量超过5000，本次只读取前5000条；还有记录需要人工核对。" : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(results, "无法完成恢复检查：" + error.Message); }
    }

    public static void KeepAll(string directory, RecoveryItem item)
    {
        if (item.Record is null || item.Problem.Length > 0) throw new IOException("损坏日志请保留并人工核对，不能自动关闭记录。");
        string expected = Path.Combine(Path.GetFullPath(directory), item.OperationId + ".jsonl");
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected, Path.GetFullPath(item.JournalPath))) throw new IOException("日志路径不匹配。");
        using var file = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (FrameworkApi.Hex(FrameworkApi.Sha256(file)) != item.Fingerprint) throw new IOException("日志已变化，请重新检查。");
        file.Dispose();
        new OperationJournal(directory).Append(item.Record with { State = "AcknowledgedKeepAll" });
    }
}

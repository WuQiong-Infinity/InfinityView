using System.IO;
using System.Text.Json;
using LightView.App.Platform;

namespace LightView.App.Organizing;

internal sealed record ArchiveResult(int Archived, string? Warning);

internal static class JournalArchive
{
    // Invoke only before creating the session undo history, with the write lease held.
    // Unresolved, malformed and retained-copy records always stay in the recovery root.
    public static ArchiveResult Rotate(string directory, DateTimeOffset now, int keepRecent = 1000)
    {
        int archived = 0;
        try
        {
            var files = Directory.EnumerateFiles(directory, "*.jsonl").Take(20001)
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            foreach (var file in files.Take(20000).Skip(Math.Max(0, keepRecent)))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 8 * 1024 * 1024) continue;
                CopyRecord? last = null; int lines = 0;
                try
                {
                    using var stream = new FileStream(WindowsFileHandle.Open(file.FullName, 0x80010000), FileAccess.Read);
                    if ((WindowsFileHandle.Facts(stream.SafeFileHandle).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true))
                    {
                        while (reader.ReadLine() is { } line)
                        {
                            if (++lines > 200) { last = null; break; }
                            using var json = JsonDocument.Parse(line);
                            var record = json.RootElement.GetProperty("operation").Deserialize<CopyRecord>();
                            if (record?.Source is null || !Guid.TryParseExact(record.OperationId, "N", out _) ||
                                record.OperationId != Path.GetFileNameWithoutExtension(file.Name)) { last = null; break; }
                            last = record;
                        }
                    }
                    if (last is null || last.Kind == "UndoCopy" ||
                        last.State is not ("Completed" or "Undone" or "Cancelled" or "Skipped" or "Conflict" or "Failed" or "AcknowledgedKeepAll")) continue;
                    string target = Path.Combine(directory, "Archive", now.ToString("yyyy-MM"));
                    Directory.CreateDirectory(target);
                    // Rename the verified held object, keeping writes/replacement denied throughout.
                    WindowsFileHandle.Rename(stream.SafeFileHandle, Path.Combine(target, file.Name));
                    archived++;
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
                { /* A damaged log stays available to JournalRecovery. */ }
            }
            return new(archived, files.Length > 20000 ? "历史日志超过20000条，归档仅处理限定批次；其余记录仍保留。" : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(archived, "历史日志归档未完成，原记录保留：" + error.Message); }
    }
}

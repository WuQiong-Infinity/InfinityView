using System.IO;
using System.Text;
using System.Text.Json;

namespace LightView.App.Organizing;

internal interface IOperationJournal { void Append(CopyRecord record); }
internal sealed record CopyRecord(string OperationId, string State, FileSnapshot Source, string TargetDirectory,
    string TemporaryPath, string? Destination = null, FileSnapshot? DestinationVersion = null, string? Error = null,
    string Kind = "Copy", string? RelatedOperationId = null);

internal sealed class OperationJournal(string directory) : IOperationJournal
{
    public void Append(CopyRecord record)
    {
        Directory.CreateDirectory(directory);
        // A caller must hold the session lease; a failed flush prevents the next mutation.
        using var stream = new FileStream(Path.Combine(directory, record.OperationId + ".jsonl"),
            FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, operation = record }) + "\n");
        stream.Write(bytes);
        stream.Flush(true);
    }

    public static FileStream AcquireSession(string directory)
    {
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
}

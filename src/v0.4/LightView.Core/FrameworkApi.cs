using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LightView.Compatibility;

public sealed class TaskCompletionSource : TaskCompletionSource<bool>
{
    public TaskCompletionSource() { }
    public TaskCompletionSource(TaskCreationOptions options) : base(options) { }
    public bool TrySetResult() => base.TrySetResult(true);
    public void SetResult() => base.SetResult(true);
}

// Bounded adapters for the APIs used by LightView, not a replacement BCL.
public static class FrameworkApi
{
    public static bool IsNullOrEmpty([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] string? value) => string.IsNullOrEmpty(value);
    public static bool IsNullOrWhiteSpace([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] string? value) => string.IsNullOrWhiteSpace(value);
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
    public static bool IsAsciiLetterOrDigit(char value) => IsAsciiDigit(value) || value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    public static bool IsEnumDefined<T>(T value) where T : struct, Enum => Enum.IsDefined(typeof(T), value);
    public static double Clamp(double value, double minimum, double maximum)
    {
        if (minimum > maximum) throw new ArgumentException("minimum exceeds maximum");
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }
    public static int Clamp(int value, int minimum, int maximum)
    {
        if (minimum > maximum) throw new ArgumentException("minimum exceeds maximum");
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }
    public static byte[] Sha256(byte[] bytes) { using var sha = SHA256.Create(); return sha.ComputeHash(bytes); }
    public static byte[] Sha256(Stream stream) { using var sha = SHA256.Create(); return sha.ComputeHash(stream); }
    public static string Hex(byte[] bytes)
    {
        var output = new char[checked(bytes.Length * 2)]; const string digits = "0123456789ABCDEF";
        for (int i = 0; i < bytes.Length; i++) { output[i * 2] = digits[bytes[i] >> 4]; output[i * 2 + 1] = digits[bytes[i] & 15]; }
        return new string(output);
    }
    public static void ThrowIfNullOrWhiteSpace(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value cannot be empty or whitespace.", nameof(value));
    }
    private static bool Separator(char value) => value is '\\' or '/';
    public static bool IsPathFullyQualified(string path) => path is not null &&
        (path.Length >= 2 && Separator(path[0]) && Separator(path[1]) ||
         path.Length >= 3 && path[1] == ':' && Separator(path[2]) && (path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'));
    public static string TrimEndingDirectorySeparator(string path) => path.Length > (Path.GetPathRoot(path)?.Length ?? 0) && Separator(path[path.Length - 1])
        ? path.Substring(0, path.Length - 1) : path;
    public static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);
    public static string ProcessPath { get { using var process = Process.GetCurrentProcess(); return process.MainModule!.FileName; } }
    public static bool IsWindowsVersionAtLeast(int major, int minor, int build) => Environment.OSVersion.Version >= new Version(major, minor, build);
    // Windows CommandLineToArgvW/CRT argument quoting. No shell is involved.
    public static void AddArgument(ProcessStartInfo start, string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        start.Arguments += (start.Arguments.Length == 0 ? "" : " ") + result;
    }
}

public static class FrameworkExtensions
{
    public static V? GetValueOrDefault<K, V>(this IReadOnlyDictionary<K, V> dictionary, K key) => dictionary.TryGetValue(key, out var value) ? value : default;
    public static V GetValueOrDefault<K, V>(this IReadOnlyDictionary<K, V> dictionary, K key, V fallback) => dictionary.TryGetValue(key, out var value) ? value : fallback;
    public static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;
    public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;
    public static string Replace(this string text, string oldValue, string newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue)) throw new ArgumentException("oldValue cannot be empty", nameof(oldValue));
        var result = new StringBuilder(); int begin = 0, found;
        while ((found = text.IndexOf(oldValue, begin, comparison)) >= 0)
        { result.Append(text, begin, found - begin).Append(newValue); begin = found + oldValue.Length; }
        return result.Append(text, begin, text.Length - begin).ToString();
    }
    public static string[] Split(this string text, char separator, StringSplitOptions options) => text.Split(new[] { separator }, options);
    public static bool Remove<K, V>(this Dictionary<K, V> dictionary, K key, out V value)
    {
        if (!dictionary.TryGetValue(key, out value)) return false;
        return dictionary.Remove(key);
    }
    public static int Read(this Stream stream, byte[] bytes) => stream.Read(bytes, 0, bytes.Length);
    public static int Read(this Stream stream, Span<byte> bytes)
    {
        byte[] buffer = new byte[bytes.Length]; int count = stream.Read(buffer, 0, buffer.Length);
        buffer.AsSpan(0, count).CopyTo(bytes); return count;
    }
    public static void Write(this Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    public static void Write(this Stream stream, ReadOnlySpan<byte> bytes) { byte[] buffer = bytes.ToArray(); stream.Write(buffer, 0, buffer.Length); }
    public static void ReadExactly(this Stream stream, byte[] bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
    }
    public static void ReadExactly(this Stream stream, Span<byte> bytes)
    {
        byte[] buffer = new byte[bytes.Length]; stream.ReadExactly(buffer); buffer.AsSpan().CopyTo(bytes);
    }
    public static async Task ReadExactlyAsync(this Stream stream, byte[] bytes, CancellationToken cancellation)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, cancellation).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException(); offset += count;
        }
    }
    public static Task WriteAsync(this Stream stream, byte[] bytes, CancellationToken cancellation) => stream.WriteAsync(bytes, 0, bytes.Length, cancellation);
    public static async Task WaitAsync(this Task task, CancellationToken cancellation)
    {
        if (task.IsCompleted) { await task.ConfigureAwait(false); return; }
        cancellation.ThrowIfCancellationRequested();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellation.Register(() => cancelled.TrySetCanceled()))
        {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
            {
                _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new OperationCanceledException(cancellation);
            }
            await task.ConfigureAwait(false);
        }
    }
    public static async Task WaitAsync(this Task task, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { await task.WaitAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw new TimeoutException(); }
    }
}

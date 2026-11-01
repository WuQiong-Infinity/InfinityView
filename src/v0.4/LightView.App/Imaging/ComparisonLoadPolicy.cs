using System.IO;
using System.Runtime.CompilerServices;

namespace LightView.App.Imaging;

internal sealed class ComparisonBudgetExceededException : IOException
{
    public ComparisonBudgetExceededException() : base("这张图片超出双图预算，已返回单图查看。") { }
}

internal static class ComparisonBudget
{
    public const long PerImage = 192L * 1024 * 1024, Pair = 384L * 1024 * 1024,
        Resident = 640L * 1024 * 1024, Decode = 768L * 1024 * 1024;
    public static long Presentation(int width, int height, int bits) => checked((long)width * height * Math.Max(4, (bits + 7) / 8));
    public static long Cost(LoadedImage image) => Presentation(image.Source.PixelWidth, image.Source.PixelHeight, image.Source.Format.BitsPerPixel);
}

// Numeric snapshots and weak source identities; the cache/UI own bitmap references.
internal sealed class ComparisonLoadPolicy
{
    private sealed class SourceId { public readonly long Value = Interlocked.Increment(ref lastId); }
    private static long lastId;
    private static readonly ConditionalWeakTable<object, SourceId> Ids = new();
    internal static long Identity(LoadedImage image) => Ids.GetValue(image.Source, _ => new SourceId()).Value;
    internal object SyncRoot { get; } = new();
    private bool enabled;
    private long reference, reserved, working, epoch;
    private ImageMemoryCache? cache;
    private readonly Dictionary<long, long> visible = new();
    private readonly HashSet<long> visibleFull = new();
    private readonly Dictionary<long, (long Bytes, int Count, bool Full)> pending = new();
    private readonly Dictionary<long, (long Bytes, int Count, bool Full)> readers = new();
    internal long PeakWorkingBytes { get; private set; }
    public bool Enabled { get { lock (SyncRoot) return enabled; } }
    internal long VisibleBytes { get { lock (SyncRoot) return visible.Values.Sum(); } }
    internal long ResidentBytes { get { lock (SyncRoot) return Resident(); } }
    internal int ResidentFullCount
    { get { lock (SyncRoot) return (cache?.FullSources.Keys ?? Enumerable.Empty<long>()).Concat(visibleFull).Concat(pending.Where(p => p.Value.Full).Select(p => p.Key)).Concat(readers.Where(p => p.Value.Full).Select(p => p.Key)).Distinct().Count(); } }
    internal long WorkingBytes { get { lock (SyncRoot) return working; } }
    internal int PendingCount { get { lock (SyncRoot) return pending.Values.Sum(p => p.Count); } }
    internal int ReaderCount { get { lock (SyncRoot) return readers.Values.Sum(p => p.Count); } }
    internal void AttachCache(ImageMemoryCache value) { lock (SyncRoot) cache = value; }
    public void Enable(bool value)
    {
        lock (SyncRoot)
        {
            if (enabled == value) return;
            enabled = value; ++epoch; pending.Clear();
            if (!value) { visible.Clear(); visibleFull.Clear(); reference = reserved = 0; }
            else TrimAndCheck();
        }
    }
    public void SetSources(LoadedImage? right, LoadedImage? left, long referenceBytes = 0, bool reserveReference = false)
    {
        var sources = new[] { right, left }.Where(i => i is not null).Select(i => (Identity(i!), ComparisonBudget.Cost(i!))).ToArray();
        lock (SyncRoot)
        {
            SetSnapshot(sources, referenceBytes, reserveReference);
            visibleFull.Clear(); foreach (var image in new[] { right, left }) if (image is { IsPreview: false }) visibleFull.Add(Identity(image));
        }
    }
    internal void SetSnapshot(IReadOnlyList<(long Id, long Bytes)> sources, long referenceBytes = 0, bool reserveReference = false)
    {
        lock (SyncRoot)
        {
            visible.Clear(); foreach (var item in sources) visible[item.Id] = item.Bytes;
            visibleFull.Clear(); foreach (var item in sources) visibleFull.Add(item.Id);
            reference = referenceBytes; reserved = reserveReference ? referenceBytes : 0;
            // SetSources does not consume another request's pending ownership.
            if (enabled) TrimAndCheck();
        }
    }
    private long Resident(long extraId = 0, long extraCost = 0)
    {
        var all = cache is null ? new Dictionary<long, long>() : cache.Sources.ToDictionary(p => p.Key, p => p.Value);
        foreach (var item in visible) all[item.Key] = item.Value;
        foreach (var item in pending) all[item.Key] = item.Value.Bytes;
        foreach (var item in readers) all[item.Key] = item.Value.Bytes;
        if (extraId != 0) all[extraId] = extraCost;
        return all.Values.Sum();
    }
    private void TrimAndCheck(long extraId = 0, long extraCost = 0, long? work = null)
    {
        long totalWork = work ?? working;
        if (totalWork < 0 || totalWork > ComparisonBudget.Decode) throw new ComparisonBudgetExceededException();
        if (!enabled)
        {
            // Single-view safety is unchanged: release cache first, never reject
            // a formerly legal image because of unknown UI/native residency.
            cache?.TrimUntil(() => cache.Bytes <= ComparisonBudget.Decode - totalWork, id => id == extraId);
            return;
        }
        bool Fits() { long bytes = Resident(extraId, extraCost) + reserved; return bytes <= ComparisonBudget.Resident && totalWork <= ComparisonBudget.Decode - bytes; }
        cache?.TrimUntil(Fits, id => id == extraId || visible.ContainsKey(id) || pending.ContainsKey(id) || readers.ContainsKey(id));
        if (!Fits()) throw new ComparisonBudgetExceededException();
    }
    internal LoadedImage RegisterDecoded(LoadedImage image)
    {
        long id = Identity(image), cost = ComparisonBudget.Cost(image);
        lock (SyncRoot)
        {
            if (!enabled) return image;
            TrimAndCheck(id, cost);
            pending.TryGetValue(id, out var old); pending[id] = (cost, old.Count + 1, !image.IsPreview);
            return image with { PendingLease = new PendingLease(this, id, epoch) };
        }
    }
    // A short asynchronous read may outlive a mode change. It uses the same
    // source union, but its ownership ends only when that worker actually ends.
    internal IDisposable RetainSource(LoadedImage image)
    {
        long id = Identity(image), cost = ComparisonBudget.Cost(image);
        lock (SyncRoot)
        {
            readers.TryGetValue(id, out var old); readers[id] = (cost, old.Count + 1, !image.IsPreview);
            return new PendingLease(this, id, epoch, true);
        }
    }
    private sealed class PendingLease(ComparisonLoadPolicy owner, long id, long generation, bool reader = false) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lock (owner.SyncRoot)
            {
                var holds = reader ? owner.readers : owner.pending;
                if ((reader || generation == owner.epoch) && holds.TryGetValue(id, out var value))
                { if (value.Count == 1) holds.Remove(id); else holds[id] = (value.Bytes, value.Count - 1, value.Full); }
            }
        }
    }
    internal void ReleaseDecoded(LoadedImage image) => image.PendingLease?.Dispose();
    public void CheckPresentation(long bytes)
    { lock (SyncRoot) if (enabled && (bytes < 0 || bytes > ComparisonBudget.PerImage || reference > ComparisonBudget.Pair - bytes)) throw new ComparisonBudgetExceededException(); }
    public void CheckWorking(long bytes) { lock (SyncRoot) TrimAndCheck(work: checked(working + bytes)); }
    internal WorkLease BeginWorking(long bytes)
    {
        lock (SyncRoot) { TrimAndCheck(work: checked(working + bytes)); working += bytes; PeakWorkingBytes = Math.Max(PeakWorkingBytes, working); return new(this, bytes); }
    }
    internal sealed class WorkLease(ComparisonLoadPolicy owner, long bytes) : IDisposable
    {
        private long held = bytes;
        internal void Resize(long value)
        { lock (owner.SyncRoot) { owner.TrimAndCheck(work: checked(owner.working - held + value)); owner.working += value - held; owner.PeakWorkingBytes = Math.Max(owner.PeakWorkingBytes, owner.working); held = value; } }
        internal LoadedImage Complete(LoadedImage image, Action addCache, bool prefetch)
        {
            long id = Identity(image), cost = ComparisonBudget.Cost(image);
            lock (owner.SyncRoot)
            {
                // Output replaces work in one transition, never double-counting it.
                owner.working -= held; held = 0;
                owner.TrimAndCheck(id, cost);
                addCache();
                return prefetch ? image : owner.RegisterDecoded(image);
            }
        }
        public void Dispose() { lock (owner.SyncRoot) { owner.working -= held; held = 0; } }
    }
}

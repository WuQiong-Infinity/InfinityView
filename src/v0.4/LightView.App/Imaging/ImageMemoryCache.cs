using LightView.App.Organizing;

namespace LightView.App.Imaging;

internal sealed class ImageMemoryCache(long budget, object? syncRoot = null)
{
    private readonly Dictionary<(FileSnapshot Version, string Target), LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> lru = [];
    private readonly object sync = syncRoot ?? new();
    private bool bypass;
    private long generation;
    private sealed record Entry((FileSnapshot Version, string Target) Key, LoadedImage Image, long SourceId, long Cost);
    public long Generation { get { lock (sync) return generation; } }
    public void SetBypass(bool value)
    { lock (sync) { bypass = value; generation++; entries.Clear(); lru.Clear(); } }
    internal IReadOnlyDictionary<long, long> Sources
    { get { lock (sync) return lru.GroupBy(e => e.SourceId).ToDictionary(g => g.Key, g => g.First().Cost); } }
    public long Bytes { get { lock (sync) return Sources.Values.Sum(); } }
    internal IReadOnlyDictionary<long, long> FullSources
    { get { lock (sync) return lru.Where(e => !e.Image.IsPreview).GroupBy(e => e.SourceId).ToDictionary(g => g.Key, g => g.First().Cost); } }
    public int FullCount { get { lock (sync) return FullSources.Count; } }
    private long PreviewBytes => Sources.Where(p => !FullSources.ContainsKey(p.Key)).Sum(p => p.Value);
    public int Count { get { lock (sync) return entries.Count; } }
    public long Budget => budget;

    public bool TryGet(FileSnapshot version, string target, out LoadedImage? image)
    {
        lock (sync)
        {
            if (bypass || !entries.TryGetValue((version, target), out var node)) { image = null; return false; }
            lru.Remove(node); lru.AddFirst(node); image = node.Value.Image; return true;
        }
    }
    internal void TrimUntil(Func<bool> fits, Func<long, bool>? pinned = null)
    {
        lock (sync)
        {
            // Speculative previews yield before demand-loaded full images.
            foreach (bool previews in new[] { true, false })
            {
                var node = lru.Last;
                while (!fits() && node is not null)
                {
                    var previous = node.Previous;
                    if (node.Value.Image.IsPreview == previews && pinned?.Invoke(node.Value.SourceId) != true) Remove(node);
                    node = previous;
                }
            }
        }
    }
    private void Remove(LinkedListNode<Entry> node) { entries.Remove(node.Value.Key); lru.Remove(node); }
    public void Add(FileSnapshot version, string target, LoadedImage image, long? expectedGeneration = null)
    {
        long cost = ComparisonBudget.Cost(image), id = ComparisonLoadPolicy.Identity(image);
        lock (sync)
        {
            // Oversized items do not discard useful smaller images.
            if (bypass || cost > budget || expectedGeneration is long expected && expected != generation) return;
            foreach (var stale in entries.Values.Where(n =>
                StringComparer.OrdinalIgnoreCase.Equals(n.Value.Image.FilePath, image.FilePath) &&
                (n.Value.Key.Version != version || n.Value.Image.DisplayTargetKey != image.DisplayTargetKey ||
                 !image.IsPreview && n.Value.Image.IsPreview)).ToArray()) Remove(stale);
            if (entries.TryGetValue((version, target), out var existing)) Remove(existing);
            var node = lru.AddFirst(new Entry((version, target), image, id, cost));
            entries.Add(node.Value.Key, node);
            foreach (var preview in lru.Reverse().Where(e => e.Image.IsPreview).ToArray())
            {
                if (PreviewBytes <= 32L * 1024 * 1024 && Bytes <= budget) break;
                if (entries.TryGetValue(preview.Key, out var drop)) Remove(drop);
            }
            while (FullCount > 5)
            {
                long oldest = lru.Last(e => !e.Image.IsPreview).SourceId;
                foreach (var drop in entries.Values.Where(n => n.Value.SourceId == oldest).ToArray()) Remove(drop);
            }
            TrimUntil(() => Bytes <= budget);
        }
    }
}

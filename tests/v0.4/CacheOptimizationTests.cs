using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Organizing;
using LightView.App.ViewModels;
using LightView.App.Views;

namespace LightView.Tests;

internal static class CacheOptimizationTests
{
    private const long MiB = 1024 * 1024;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (ComparisonBudgetExceededException) { return; } throw new InvalidOperationException(message); }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void PumpUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Controlled loader timed out");
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Pause(int milliseconds) { var watch = Stopwatch.StartNew(); PumpUntil(() => watch.ElapsedMilliseconds >= milliseconds); }
    internal static int Run(string root, string? filter = null)
    {
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(root, "UserData"));
        var app = new LightView.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases = new (string Name, Action Run)[]
        {
            ("Source union and per-delivery pending survive copies releases and mode generations", Pending),
            ("LRU eviction precedes limits and work leases release on every transition", Budgets),
            ("real comparison full cache hits and foreground priority", () => RealCache(root)),
            ("full precedes prefetch and only rapid adjacent requests debounce", () => Scheduling(root)),
            ("active pane prefetch cancels and restarts after quick focus return", () => Activity(root))
        };
        if (filter is not null) cases = cases.Where(c => c.Name.Contains(filter)).ToArray();
        int failures = 0;
        foreach (var item in cases) try { item.Run(); Console.WriteLine("PASS " + item.Name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        string result = $"{cases.Length - failures}/{cases.Length} cache optimization checks passed";
        File.WriteAllText(Path.Combine(root, "result.txt"), result); Console.WriteLine("RESULT " + result); return failures == 0 ? 0 : 1;
    }
    private static LoadedImage Image(string path, bool preview = false)
    {
        int edge = preview ? 32 : 512;
        var source = BitmapSource.Create(edge, edge, 96, 96, PixelFormats.Pbgra32, null, new byte[edge * edge * 4], edge * 4); source.Freeze();
        return new(path, source, "Synthetic", 0, FileVersion: new(path, path, 1, 1, "hash"), IsPreview: preview,
            OriginalWidth: 512, OriginalHeight: 512, DisplayTargetKey: DisplayColorTarget.Standard.Key);
    }
    private static void Pending()
    {
        var policy = new ComparisonLoadPolicy(); var cache = new ImageMemoryCache(8 * MiB, policy.SyncRoot); policy.AttachCache(cache); policy.Enable(true);
        var image = Image("shared.jpg"); cache.Add(image.FileVersion!, "a", image); cache.Add(image.FileVersion!, "b", image);
        policy.SetSources(image, image);
        var first = policy.RegisterDecoded(image); var second = policy.RegisterDecoded(image);
        Check(cache.Bytes == MiB && policy.VisibleBytes == MiB && policy.ResidentBytes == MiB && policy.PendingCount == 2, "Shared Source was double billed or a delivery was lost");
        policy.SetSources(null, null); cache.SetBypass(false); policy.ReleaseDecoded(first with { ViewIntent = ImageViewIntent.Adjacent }); policy.ReleaseDecoded(first);
        Check(policy.PendingCount == 1 && policy.ResidentBytes == MiB, "One release consumed another delivery or ignored record copies");
        policy.ReleaseDecoded(second); Check(policy.ResidentBytes == 0, "Pending leaked after final release");
        var old = policy.RegisterDecoded(image); policy.Enable(false); policy.Enable(true); var current = policy.RegisterDecoded(image);
        policy.ReleaseDecoded(old); Check(policy.PendingCount == 1, "Old mode release consumed new mode ownership"); policy.ReleaseDecoded(current);
        var tinyCache = new ImageMemoryCache(16); tinyCache.Add(image.FileVersion!, "small", image with { Source = Image("tiny", true).Source });
        var one = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4); one.Freeze();
        tinyCache.Add(image.FileVersion!, "one", image with { Source = one }); tinyCache.Add(image.FileVersion!, "oversized", image);
        Check(tinyCache.Count == 1 && tinyCache.Bytes == 4, "Oversized item cleared useful small cache");
    }
    private static void Budgets()
    {
        var policy = new ComparisonLoadPolicy(); var cache = new ImageMemoryCache(8 * MiB, policy.SyncRoot); policy.AttachCache(cache); policy.Enable(true);
        var a = Image("a.jpg"); var b = Image("b.jpg"); cache.Add(a.FileVersion!, "a", a); cache.Add(b.FileVersion!, "b", b);
        policy.SetSnapshot([(999999, ComparisonBudget.Pair - 2 * MiB)]);
        using (var work = policy.BeginWorking(ComparisonBudget.Decode - ComparisonBudget.Pair + MiB))
            Check(cache.Count == 1 && policy.WorkingBytes == 385 * MiB, "Releasable LRU did not yield to foreground work");
        Check(policy.WorkingBytes == 0, "Work leaked on disposal");
        cache.SetBypass(false); cache.Add(a.FileVersion!, "a", a);
        policy.SetSnapshot([(999999, ComparisonBudget.Pair - MiB), (ComparisonLoadPolicy.Identity(a), MiB)]);
        Reject(() => policy.BeginWorking(ComparisonBudget.Decode - ComparisonBudget.Pair + 1), "Pinned Source was falsely freed by LRU removal");
        Check(cache.Count == 1 && policy.WorkingBytes == 0, "Failed reservation changed held work or removed pinned cache");
        policy.CheckPresentation(ComparisonBudget.PerImage); Reject(() => policy.CheckPresentation(ComparisonBudget.PerImage + 1), "192 MiB boundary missing");
        policy.SetSnapshot([(999999, ComparisonBudget.Pair - MiB)]); cache.SetBypass(false);
        var pending = policy.RegisterDecoded(a);
        using (var work = policy.BeginWorking(ComparisonBudget.Decode - ComparisonBudget.Pair))
            Reject(() => work.Complete(b, () => cache.Add(b.FileVersion!, "b", b), false), "Unreleasable pending did not enforce 384 MiB");
        Check(policy.PendingCount == 1 && policy.WorkingBytes == 0, "Rejected transition lost a pending or work lease");
        policy.ReleaseDecoded(pending);
        using (var work = policy.BeginWorking(ComparisonBudget.Decode - ComparisonBudget.Pair + MiB))
        {
            var delivered = work.Complete(b, () => cache.Add(b.FileVersion!, "b", b), false);
            Check(policy.WorkingBytes == 0 && policy.ResidentBytes == ComparisonBudget.Pair, "Work/output transition was double billed"); policy.ReleaseDecoded(delivered);
        }
        Reject(() => policy.BeginWorking(ComparisonBudget.Decode + 1), "768 MiB boundary missing");
    }
    private static void RealCache(string root)
    {
        string dir = Path.Combine(root, "real"); Directory.CreateDirectory(dir);
        string fixtures = Path.GetFullPath("work/quality-loading/desktop-fixtures");
        string a = Path.Combine(dir, "A.jpg"), b = Path.Combine(dir, "B.jpg"); File.Copy(Path.Combine(fixtures, "A.jpg"), a, true); File.Copy(Path.Combine(fixtures, "B.jpg"), b, true);
        var loader = new WicImageLoader(); loader.SetComparisonMode(true);
        var first = loader.Load(a, default); var second = loader.Load(b, default); loader.ComparisonPolicy.SetSources(first, second); loader.ComparisonPolicy.ReleaseDecoded(first); loader.ComparisonPolicy.ReleaseDecoded(second);
        int decodes = loader.DecodeCount; var watch = Stopwatch.StartNew(); var again = loader.Load(a, default);
        Console.WriteLine($"CACHE full A-B-A: decodes={loader.DecodeCount}, hits={loader.CacheHits}, returnMs={watch.Elapsed.TotalMilliseconds:0.0}");
        Check(ReferenceEquals(first.Source, again.Source) && loader.DecodeCount == decodes && loader.ComparisonPolicy.PendingCount == 1, "Comparison full hit decoded again or skipped pending registration");
        using (var work = loader.ComparisonPolicy.BeginWorking(1))
        {
            var gate = (SemaphoreSlim)typeof(WicImageLoader).GetField("DecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            gate.Wait(); try { var hit = loader.Load(a, default); loader.ComparisonPolicy.ReleaseDecoded(hit); } finally { gate.Release(); }
        }
        loader.ComparisonPolicy.ReleaseDecoded(again); loader.SetComparisonMode(false); loader.SetComparisonMode(true);
        var afterMode = loader.Load(a, default); Check(ReferenceEquals(first.Source, afterMode.Source), "Mode switch cleared reusable full cache"); loader.ComparisonPolicy.ReleaseDecoded(afterMode);
        var heldGate = (SemaphoreSlim)typeof(WicImageLoader).GetField("DecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        string c = Path.Combine(dir, "C.jpg"); File.Copy(b, c, true); heldGate.Wait(); Task<LoadedImage>? foreground = null;
        try
        {
            foreground = Task.Run(() => loader.Load(c, default));
            PumpUntil(() => (int)typeof(WicImageLoader).GetField("foregroundWaiters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! > 0);
            int before = loader.DecodeCount; Check(!loader.TryPrefetchPreview(b, 768, default) && loader.DecodeCount == before, "Prefetch bypassed queued foreground demand");
        }
        finally { heldGate.Release(); }
        Wait(foreground!); loader.ComparisonPolicy.ReleaseDecoded(foreground!.Result);
        var limited = new WicImageLoader(); limited.SetComparisonMode(true); limited.ComparisonPolicy.SetSnapshot([(999999, ComparisonBudget.Pair)]);
        Check(!limited.TryPrefetchPreview(b, 768, default) && limited.ComparisonPolicy.Enabled && limited.ComparisonPolicy.WorkingBytes == 0 && limited.ComparisonPolicy.PendingCount == 0, "Budget-rejected prefetch leaked work/pending or exited comparison");
        Reject(() => limited.Load(b, default), "Foreground did not reject genuinely full residency"); Check(limited.ComparisonPolicy.WorkingBytes == 0, "Foreground failure leaked work");
    }
    private sealed class Controlled : IImageLoader, IProgressiveImageLoader, IImagePrefetcher
    {
        internal bool BlockFull;
        internal bool BlockFirstPrefetch;
        internal readonly ManualResetEventSlim FullEntered = new(), FullRelease = new(), PrefetchEntered = new(), PrefetchRelease = new(), PrefetchCancelled = new();
        internal readonly List<string> Paths = new();
        internal int Count { get { lock (Paths) return Paths.Count; } }
        public LoadedImage LoadPreview(string path, int edge, CancellationToken token) => Image(path, true);
        public LoadedImage Load(string path, CancellationToken token)
        { if (BlockFull) { FullEntered.Set(); FullRelease.Wait(token); } return Image(path); }
        public bool TryPrefetchPreview(string path, int edge, CancellationToken token) => TryPrefetch(path, token);
        public bool TryPrefetch(string path, CancellationToken token)
        {
            int count; lock (Paths) { Paths.Add(Path.GetFileName(path)); count = Paths.Count; }
            if (BlockFirstPrefetch && count == 1)
            { using var registration = token.Register(() => PrefetchCancelled.Set()); PrefetchEntered.Set(); if (!PrefetchRelease.Wait(5000)) throw new IOException("Prefetch hold timed out"); }
            return true;
        }
    }
    private static string Folder(string root, string name)
    {
        string dir = Path.GetFullPath(Path.Combine(root, name)); Directory.CreateDirectory(dir); foreach (string file in new[] { "A.jpg", "B.jpg", "C.jpg", "D.jpg" }) File.WriteAllBytes(Path.Combine(dir, file), [1]); return dir;
    }
    private static void Scheduling(string root)
    {
        string dir = Folder(root, "schedule"); var controlled = new Controlled { BlockFull = true }; var viewer = new ViewerViewModel(controlled);
        try
        {
            var open = viewer.OpenAsync(Path.Combine(dir, "B.jpg")); PumpUntil(() => controlled.FullEntered.IsSet);
            Check(viewer.LastUpgradeDelay == 0 && viewer.IsPreview && viewer.OrganizingImage is null, "Ordinary opening delayed full or preview enabled organizing");
            Pause(120); Check(controlled.Count == 0, "Neighbors ran before full completed");
            controlled.FullRelease.Set(); Wait(open); viewer.SetPrefetchEligibility(false);
            controlled.BlockFull = false; Wait(viewer.OpenAsync(Path.Combine(dir, "C.jpg"), ImageViewIntent.Adjacent)); Check(viewer.LastUpgradeDelay == 0, "First adjacent navigation delayed");
            var rapid = viewer.OpenAsync(Path.Combine(dir, "D.jpg"), ImageViewIntent.Adjacent); Check(viewer.LastUpgradeDelay == 90, "Rapid adjacent navigation did not select 90ms");
            var watch = Stopwatch.StartNew(); var demand = viewer.EnsureFullAsync(); Wait(demand); Wait(rapid);
            Console.WriteLine($"DEMAND expedited fullMs={watch.Elapsed.TotalMilliseconds:0.0}"); Check(demand.Result && viewer.OrganizingImage is not null, "Explicit full demand did not complete safe version");
            Pause(190); Wait(viewer.OpenAsync(Path.Combine(dir, "C.jpg"), ImageViewIntent.Adjacent)); Check(viewer.LastUpgradeDelay == 0, "Low frequency adjacent retained debounce");
        }
        finally { controlled.FullRelease.Set(); viewer.Detach(); Wait(viewer.PrefetchReady); }
    }
    private static void Activity(string root)
    {
        string dir = Folder(root, "activity"); var rightLoader = new Controlled { BlockFirstPrefetch = true }; var leftLoader = new Controlled { BlockFirstPrefetch = true }; var window = new MainWindow();
        try
        {
            var old = Field<PaneContext>(window, "activePane"); old.Attached = false; old.Viewer.Detach();
            var right = (PaneContext)Call(window, "CreatePane", new ViewerViewModel(rightLoader), window.FindName("Viewport"))!; Call(window, "ActivatePane", right); Wait(right.Viewer.SeedAsync(Image(Path.Combine(dir, "B.jpg"))));
            var session = Field<ComparisonSession>(window, "comparison"); PaneContext? left = null;
            var enter = session.EnterAsync(right, _ => left = (PaneContext)Call(window, "CreatePane", new ViewerViewModel(leftLoader), window.FindName("ReferenceViewport"))!, DisplayColorTarget.Standard.Key); Wait(enter);
            Check(enter.Result, "Controlled split entry rejected");
            Console.WriteLine($"ACTIVITY entry={session.State} paused={right.Viewer.IsPrefetchSuspended} ready={right.Viewer.PrefetchReady.Status} eligible={Field<bool>(right.Viewer, "prefetchEligible")} folder={right.Viewer.CanNavigate} loading={right.Viewer.IsLoading}");
            if (right.Viewer.PrefetchReady.IsCompleted) Wait(right.Viewer.PrefetchReady);
            PumpUntil(() => rightLoader.PrefetchEntered.IsSet); Check(leftLoader.Count == 0, "Inactive side prefetched on entry");
            Call(window, "ActivatePane", left!); PumpUntil(() => leftLoader.PrefetchEntered.IsSet && rightLoader.PrefetchCancelled.IsSet);
            Call(window, "ActivatePane", right); Check(rightLoader.Count == 1, "Quick return started a duplicate while old prefetch owned work");
            rightLoader.PrefetchRelease.Set(); PumpUntil(() => rightLoader.Count >= 4); leftLoader.PrefetchRelease.Set(); Wait(right.Viewer.PrefetchReady); Wait(left!.Viewer.PrefetchReady);
            Check(rightLoader.Paths.Skip(1).SequenceEqual(new[] { "C.jpg", "A.jpg", "D.jpg" }) && leftLoader.Count == 1, "Activity restart lost near-first order or three-neighbor bound");
            int completeCount = rightLoader.Count; Pause(110); Check(rightLoader.Count == completeCount, "Natural prefetch completion restarted forever");
            session.Exit(right); Check(right.Viewer.PrefetchNeighborLimit == 16, "Single-view neighbor limit not restored");
        }
        finally { rightLoader.PrefetchRelease.Set(); leftLoader.PrefetchRelease.Set(); window.Close(); }
    }
}

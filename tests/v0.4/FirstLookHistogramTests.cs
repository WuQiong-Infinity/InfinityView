using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Input;
using LightView.App.Persistence;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class FirstLookHistogramTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("Controlled histogram wait timed out");
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void QuietHistogram(MainWindow window) { Wait(Field<Task>(window, "histogramWorker")); }
    internal static int Run(string root, string? filter = null)
    {
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(root, "UserData"));
        var app = new LightView.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases = new (string Name, Action Run)[]
        {
            ("display RGB counts transparent and partial alpha without background composition", Counts),
            ("reader ownership crosses mode generations and OEM3 preserves existing bindings", () => OwnershipAndKeys(root)),
            ("histogram demand serializes latest source and rejects late preview focus and hidden results", () => Lifecycle(root)),
            ("two panes retain independent graphs visibility and inactive late results", () => Independent(root)),
            ("actual pane layout mirrors histogram and reserves permanent toolbar space", () => Layout(root))
        };
        if (filter is not null) cases = cases.Where(c => c.Name.Contains(filter)).ToArray();
        int failures = 0;
        foreach (var item in cases) try { item.Run(); Console.WriteLine("PASS " + item.Name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        string result = $"{cases.Length - failures}/{cases.Length} first-look/histogram checks passed";
        File.WriteAllText(Path.Combine(root, "result.txt"), result); Console.WriteLine("RESULT " + result); return failures == 0 ? 0 : 1;
    }
    private static BitmapSource Bitmap(byte[] bytes, int width, int height)
    { var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, checked(width * 4)); image.Freeze(); return image; }
    private static void Counts()
    {
        byte[] pixels = [0,0,0,255, 255,255,255,255, 0,0,255,255, 0,255,0,255, 255,0,0,255, 32,64,96,128, 0,0,0,0, 0,1,1,1];
        var result = HistogramCalculator.Calculate(Bitmap(pixels, 8, 1), default);
        Check(result.Pixels == 7 && result.Red.Sum() == 7 && result.Green.Sum() == 7 && result.Blue.Sum() == 7, "Channel sums or valid-pixel count incorrect");
        Check(result.Lightness.Sum()==7&&new[]{0,255,54,182,18,137,237}.All(bin=>result.Lightness[bin]==1),"Weighted display brightness or alpha handling incorrect");
        Check(result.Red[0] == 3 && result.Red[255] == 3 && result.Red[191] == 1 &&
            result.Green[0] == 3 && result.Green[255] == 3 && result.Green[128] == 1 &&
            result.Blue[0] == 4 && result.Blue[255] == 2 && result.Blue[64] == 1, "Display RGB or alpha unpremultiplication incorrect");
        var empty = HistogramCalculator.Calculate(Bitmap(new byte[16], 4, 1), default);
        Check(empty.Pixels == 0 && empty.Red.Sum() == 0 && empty.Blue.Sum() == 0 && empty.Green.Sum() == 0 && empty.Lightness.Sum()==0, "Transparent pixels became black");
        var cache = new HistogramCache(); for (int i = 0; i < 6; i++) cache.Add(new(i + 1, "target", false), result);
        Check(cache.Count == 5 && !cache.TryGet(new(1, "target", false), out _) && cache.TryGet(new(6, "target", false), out _), "Small numeric cache is unbounded or not LRU");
    }
    private static LoadedImage Synthetic(string path, byte red, bool preview = false)
    {
        int edge = preview ? 4 : 16; byte[] bytes = new byte[edge * edge * 4];
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i + 2] = red; bytes[i + 3] = 255; }
        return new(path, Bitmap(bytes, edge, edge), "Synthetic", 0, FileVersion: new(path, path, 1, 1, "hash"),
            IsPreview: preview, OriginalWidth: 1000, OriginalHeight: 800, DisplayTargetKey: DisplayColorTarget.Standard.Key);
    }
    private static void OwnershipAndKeys(string root)
    {
        var image = Synthetic("lease.jpg", 10); var policy = new ComparisonLoadPolicy(); policy.SetSources(image, image); policy.Enable(true);
        var first = policy.RetainSource(image); var second = policy.RetainSource(image);
        Check(policy.ResidentBytes == ComparisonBudget.Cost(image) && policy.ReaderCount == 2, "Reader Source double billed");
        policy.Enable(false); policy.Enable(true); first.Dispose(); first.Dispose();
        Check(policy.ReaderCount == 1 && policy.ResidentBytes == ComparisonBudget.Cost(image), "Mode change lost live reader or duplicate release consumed another");
        second.Dispose(); Check(policy.ReaderCount == 0 && policy.ResidentBytes == 0, "Reader lease leaked");
        Check(ShortcutGesture.Normalize("`") == "OEM3" && ShortcutGesture.Display("OEM3") == "`" && InputRouter.KeyboardGesture(Key.Oem3, ModifierKeys.None) == "OEM3" && InputRouter.IsSupported("OEM3"), "OEM3 alias/input/recording rejected");
        var fresh = new ShortcutMap(CommandCatalog.DefaultBindings());
        Check(fresh.Resolve("OEM3")?.Id == CommandCatalog.Histogram && fresh.Resolve("OEM3", true) is null && fresh.Resolve("OEM3", textEntry: true) is null, "Default repeats or text-entry guard incorrect");
        string dir = Path.Combine(root, "old-bindings"); Directory.CreateDirectory(dir);
        var custom = new AppSettings { Bindings = [new(CommandCatalog.Fit, "OEM3")] };
        File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(custom, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var loaded = new SettingsStore(dir).Load();
        Check(loaded.Settings.Bindings.Count == 1 && new ShortcutMap(loaded.Settings.Bindings).Resolve("OEM3")?.Id == CommandCatalog.Fit, "Old customized OEM3 was overwritten");
    }
    private static void Toggle(MainWindow window) => Wait((Task)Call(window, "ExecuteCommandAsync", CommandCatalog.Histogram, null, 1d, Field<PaneContext>(window, "activePane"))!);
    private static void Lifecycle(string root)
    {
        var window = new MainWindow(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            var owner = Field<PaneContext>(window, "activePane"); owner.Viewer.SetPrefetchEligibility(false); var policy = Field<WicImageLoader>(window, "imageLoader").ComparisonPolicy;
            Wait(owner.Viewer.SeedAsync(Synthetic(Path.Combine(root, "preview.jpg"), 10, true)));
            Check(window.HistogramScanCount == 0, "Hidden histogram scanned pixels");
            int active = 0, maximum = 0;
            window.HistogramCompute = (source, token) =>
            {
                int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
                try { entered.Set(); if (!release.Wait(5000)) throw new IOException("Controlled histogram held too long"); return HistogramCalculator.Calculate(source, default); }
                finally { Interlocked.Decrement(ref active); }
            };
            Toggle(window); Until(() => entered.IsSet);
            Wait(owner.Viewer.SeedAsync(Synthetic(Path.Combine(root, "full-b.jpg"), 20)));
            Wait(owner.Viewer.SeedAsync(Synthetic(Path.Combine(root, "full-c.jpg"), 30)));
            Check(policy.ReaderCount == 2 && window.HistogramScanCount == 1, "Multiple queued Sources or concurrent scans retained");
            Toggle(window); Check(policy.ReaderCount == 1, "Hidden latest request retained a queued Source");
            Wait(owner.Viewer.SeedAsync(Synthetic(Path.Combine(root, "full-d.jpg"), 40))); release.Set(); QuietHistogram(window);
            Check(window.HistogramScanCount == 1 && policy.ReaderCount == 0 && ((HistogramView)window.FindName("RightHistogram")).Data is null, "Hidden late result published or leaked its Source");
            window.HistogramCompute = HistogramCalculator.Calculate; Toggle(window); QuietHistogram(window);
            var right = (HistogramView)window.FindName("RightHistogram"); Check(right.Data?.Red[40] == 256, "Current full did not replace hidden old result");
            Wait(owner.Viewer.SeedAsync(Synthetic(Path.Combine(root, "same.jpg"), 50, true))); QuietHistogram(window);
            Check(right.IsPreview && right.Data?.Pixels == 16 && right.Data.Red[50] == 16, "Preview histogram used original dimensions or lost marker");
            Wait(owner.Viewer.SeedAsync(Synthetic(Path.Combine(root, "same.jpg"), 60))); QuietHistogram(window);
            Check(!right.IsPreview && right.Data?.Pixels == 256 && right.Data.Red[60] == 256, "Full did not replace preview distribution");
            Wait((Task)Call(window, "ToggleComparisonAsync", owner)!);
            var session = Field<ComparisonSession>(window, "comparison"); var left = session.Left!;
            owner.Viewer.SetPrefetchEligibility(false); left.Viewer.SetPrefetchEligibility(false);
            entered.Reset(); release.Reset(); window.HistogramCompute = (source, token) => { entered.Set(); if (!release.Wait(5000)) throw new IOException("Focus histogram held too long"); return HistogramCalculator.Calculate(source, default); };
            Wait(owner.Viewer.SeedAsync(Synthetic(Path.Combine(root, "right-new.jpg"), 70))); Until(() => entered.IsSet);
            Call(window, "ActivatePane", left); release.Set(); QuietHistogram(window);
            var leftChart = (HistogramView)window.FindName("LeftHistogram");
            Check(leftChart.Visibility == Visibility.Visible && leftChart.Data?.Red[60] == 256 && right.Visibility == Visibility.Visible && right.Data?.Red[70] == 256 && policy.ReaderCount == 0, "Independent late result crossed panes or focus hid the other graph");
            left.ColorPending = true; Call(window, "RefreshHistogram", false); Check(leftChart.Visibility == Visibility.Collapsed, "Old-target chart remained visible under color mask");
            left.ColorPending = false; Call(window, "RefreshHistogram", false); QuietHistogram(window);
            window.HistogramCompute = (_, _) => throw new IOException("Optional histogram failure");
            Wait(left.Viewer.SeedAsync(Synthetic(Path.Combine(root, "optional.jpg"), 80))); QuietHistogram(window);
            Check(session.State == ComparisonState.Active && left.Viewer.OrganizingImage is not null && left.Viewer.ErrorMessage == "" && leftChart.Data is null && policy.ReaderCount == 0, "Optional statistics failure blocked image or exited comparison");
            Call(window, "ExitComparison", left); QuietHistogram(window); Toggle(window);
            Check(leftChart.Visibility == Visibility.Collapsed && policy.ReaderCount == 0 && maximum == 1, "Exit/hide leaked graph ownership");
        }
        finally { release.Set(); window.Close(); QuietHistogram(window); }
    }
    private static void Layout(string root)
    {
        var window = new MainWindow(); try
        {
            var owner = Field<PaneContext>(window, "activePane"); Wait(owner.Viewer.SuspendPrefetchAsync());
            Wait(owner.Viewer.OpenAsync(Path.GetFullPath("work/quality-loading/desktop-fixtures/A.jpg"))); Wait((Task)Call(window, "ToggleComparisonAsync", owner)!);
            var session = Field<ComparisonSession>(window, "comparison"); Wait(owner.Viewer.SuspendPrefetchAsync()); Wait(session.Left!.Viewer.SuspendPrefetchAsync());
            Wait(owner.Viewer.OpenAsync(Path.GetFullPath("work/quality-loading/desktop-fixtures/B.jpg")));
            Call(window, "ToggleDetails", owner); Call(window, "ToggleDetails", session.Left); Toggle(window); Call(window,"ActivatePane",session.Left); Toggle(window); Call(window,"ActivatePane",owner); QuietHistogram(window);
            var visual = (FrameworkElement)window.Content; window.Content = null;
            var chart = (HistogramView)window.FindName("RightHistogram"); var details = (Border)window.FindName("DetailsPanel");
            var settings = Field<AppSettings>(window, "settings");
            Rect Bounds(FrameworkElement element, FrameworkElement ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
            void Render(int width, int height, string name, bool fullscreen, bool autoHide)
            {
                settings.Window.FullScreenAutoHide = autoHide; window.ApplyChromeLayout(fullscreen);
                var bar = (FrameworkElement)window.FindName("TopBar"); bar.Visibility = Visibility.Visible;
                visual.Measure(new Size(width, height)); visual.Arrange(new Rect(0, 0, width, height)); visual.UpdateLayout();
                var grid = (FrameworkElement)window.FindName("ComparisonGrid");
                Check(Math.Abs(Bounds(grid, visual).Top - (fullscreen && autoHide ? 0 : 44)) < .01, "Permanent toolbar did not reserve space, or temporary toolbar moved the canvas");
                foreach (var side in new[] { ("Left", "LeftDetailsPanel", "LeftDetailsViewbox", "ReferenceName", "ReferencePane", "LeftFocusCorners", session.Left!), ("Right", "DetailsPanel", "DetailsViewbox", "CurrentComparisonName", "CanvasContainer", "RightFocusCorners", owner) })
                {
                    if (side.Item7 is null || !side.Item7.Attached) continue;
                    var pane = (FrameworkElement)window.FindName(side.Item5);
                    var graph = (HistogramView)window.FindName(side.Item1 + "Histogram");
                    var detail = (FrameworkElement)window.FindName(side.Item2); var box = (FrameworkElement)window.FindName(side.Item3);
                    var file = (FrameworkElement)window.FindName(side.Item4); var corners = (FrameworkElement)window.FindName(side.Item6);
                    var gb = Bounds(graph, pane); var db = Bounds(detail, pane); var content = Bounds(box, pane);
                    bool detailsRight = session.IsOn && ReferenceEquals(session.Right, side.Item7);
                    Check(Math.Abs(gb.Top - 10) < .01 && Math.Abs(db.Top - 10) < .01 &&
                        Math.Abs(detailsRight ? gb.Left - 10 : pane.ActualWidth - gb.Right - 10) < .01 &&
                        Math.Abs(detailsRight ? pane.ActualWidth - content.Right - 10 : content.Left - 10) < .01,
                        "Details/graph are not aligned inside the focus corners");
                    Check(!graph.IsHitTestVisible && (detailsRight ? gb.Right + 11.9 <= db.Left : db.Right + 11.9 <= gb.Left), "Histogram overlaps details or steals input");
                    Check(Bounds(corners, pane) == new Rect(pane.RenderSize), "Focus corners no longer follow the canvas");
                    if (!(fullscreen && autoHide)) Check(Bounds(corners, visual).Top >= Bounds(bar, visual).Bottom, "Permanent toolbar covers focus corners or pane information");
                    if (session.IsOn)
                    {
                        var fb = Bounds(file, pane);
                        Check(gb.Bottom < fb.Top && db.Bottom < fb.Top && Math.Abs(pane.ActualHeight - fb.Bottom - 10) < .01, "Information overlaps bottom L filename");
                    }
                }
                var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered)); using var output = File.Create(Path.Combine(root, name + ".png")); png.Save(output);
            }
            Render(1600, 900, "split-window", false, true);
            Render(840, 560, "split-permanent", true, false);
            Render(640, 420, "split-narrow", true, false);
            Render(840, 560, "split-temporary", true, true);
            int scans = window.HistogramScanCount; double expandedWidth = details.MaxWidth;
            Toggle(window); Check(details.Margin.Top == 10 && details.MaxWidth > expandedWidth, "Hiding graph moved details vertically or failed to restore width");
            Toggle(window); QuietHistogram(window); Check(window.HistogramScanCount == scans, "Same-Source numeric cache rescanned pixels");
            Call(window, "ExitComparison", owner); QuietHistogram(window);
            Render(840, 560, "single-permanent", true, false);
        }
        finally { window.Close(); QuietHistogram(window); }
    }
    private static void Independent(string root)
    {
        var window=new MainWindow();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        try
        {
            var right=Field<PaneContext>(window,"activePane");right.Viewer.SetPrefetchEligibility(false);Wait(right.Viewer.SeedAsync(Synthetic(Path.Combine(root,"dual-a.jpg"),10)));Toggle(window);QuietHistogram(window);
            Wait((Task)Call(window,"ToggleComparisonAsync",right)!);var session=Field<ComparisonSession>(window,"comparison");var left=session.Left!;QuietHistogram(window);
            var lc=(HistogramView)window.FindName("LeftHistogram");var rc=(HistogramView)window.FindName("RightHistogram");var policy=Field<WicImageLoader>(window,"imageLoader").ComparisonPolicy;
            Check(lc.Visibility==Visibility.Visible&&rc.Visibility==Visibility.Visible,"New comparison did not seed independent visibility");
            Call(window,"ActivatePane",left);var originalLeft=lc.Data;Wait(right.Viewer.SeedAsync(Synthetic(Path.Combine(root,"dual-b.jpg"),80)));QuietHistogram(window);
            Check(ReferenceEquals(originalLeft,lc.Data)&&lc.Data?.Red[10]==256&&rc.Data?.Red[80]==256,"Inactive update overwrote or hid peer graph");
            int scans=window.HistogramScanCount;Toggle(window);Check(lc.Visibility==Visibility.Collapsed&&rc.Visibility==Visibility.Visible,"Toggling left changed right visibility");
            Call(window,"ActivatePane",right);Toggle(window);Check(rc.Visibility==Visibility.Collapsed,"Right toggle failed");Call(window,"ActivatePane",left);Toggle(window);QuietHistogram(window);
            Check(lc.Visibility==Visibility.Visible&&rc.Visibility==Visibility.Collapsed&&window.HistogramScanCount==scans,"Independent toggle state or cached counts lost on focus");
            Call(window,"ActivatePane",right);Toggle(window);QuietHistogram(window);Call(window,"ActivatePane",left);
            int active=0,maximum=0;window.HistogramCompute=(source,token)=>{int n=Interlocked.Increment(ref active);maximum=Math.Max(maximum,n);try{entered.Set();if(!release.Wait(5000))throw new IOException("Dual worker held too long");return HistogramCalculator.Calculate(source,default);}finally{Interlocked.Decrement(ref active);}};
            Wait(right.Viewer.SeedAsync(Synthetic(Path.Combine(root,"dual-c.jpg"),100)));Until(()=>entered.IsSet);Toggle(window);Wait(left.Viewer.SeedAsync(Synthetic(Path.Combine(root,"hidden-dual.jpg"),120)));release.Set();QuietHistogram(window);
            Check(rc.Data?.Red[100]==256&&rc.Visibility==Visibility.Visible&&lc.Visibility==Visibility.Collapsed&&maximum==1&&policy.ReaderCount==0,"Hiding another pane cancelled a valid inactive result or leaked reader");
            window.HistogramCompute=HistogramCalculator.Calculate;Toggle(window);QuietHistogram(window);Check(lc.Data?.Red[120]==256&&lc.Data.Lightness.Sum()==256,"Hidden updated pane did not resume RGB and brightness together");
            Call(window,"ExitComparison",left);QuietHistogram(window);Check(lc.Visibility==Visibility.Visible&&left.HistogramVisible&&policy.ReaderCount==0,"Exit did not preserve retained pane state");
        }
        finally{release.Set();window.Close();QuietHistogram(window);}
    }
}

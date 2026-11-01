using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Platform;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;

namespace LightView.Tests;

internal static class MemoryFiveTests
{
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > 15000) throw new TimeoutException("Controlled UI wait timed out");
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    internal static int Run(string root, string? filter = null)
    {
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(root, "UserData"));
        var app = new LightView.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases = new (string Name, Action Run)[]
        {
            ("stripe Pbgra exactly matches former full-surface output for all eight orientations alpha ICC and 64-bit", Precision),
            ("compatibility output stays detached and invalid ICC still rejects", () => Compatibility(root)),
            ("five real 24MP full frames hit on revisit and sixth LRU plus large work reduce safely", Five),
            ("link toast has only two transient messages independent of operation feedback", () => Toast(root))
        };
        if (filter is not null) cases = cases.Where(c => c.Name.Contains(filter)).ToArray();
        int failures = 0;
        foreach (var item in cases) try { item.Run(); Console.WriteLine("PASS " + item.Name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        string result = $"{cases.Length - failures}/{cases.Length} memory-five checks passed";
        File.WriteAllText(Path.Combine(root, "result.txt"), result); Console.WriteLine("RESULT " + result); return failures == 0 ? 0 : 1;
    }
    private static byte[] Pixels(BitmapSource source)
    {
        byte[] bytes = new byte[checked(source.PixelWidth * source.PixelHeight * 4)]; source.CopyPixels(bytes, checked(source.PixelWidth * 4), 0); return bytes;
    }
    private static BitmapSource Pattern(bool high)
    {
        const int width = 337, height = 1073; int stride = width * (high ? 8 : 4); byte[] bytes = new byte[stride * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int at = y * stride + x * (high ? 8 : 4);
            if (high)
                for (int c = 0; c < 4; c++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + c * 2, 2), (ushort)((x * 977 + y * 313 + c * 7771) & 65535));
            else { bytes[at] = (byte)(x * 7); bytes[at + 1] = (byte)(y * 3); bytes[at + 2] = (byte)(x + y); bytes[at + 3] = (byte)(x * 11 + y); }
        }
        var source = BitmapSource.Create(width, height, 96, 96, high ? PixelFormats.Rgba64 : PixelFormats.Bgra32, null, bytes, stride); source.Freeze(); return source;
    }
    private static Matrix Orientation(ushort value) => value switch
    {
        2 => new(-1, 0, 0, 1, 0, 0), 3 => new(-1, 0, 0, -1, 0, 0), 4 => new(1, 0, 0, -1, 0, 0),
        5 => new(0, 1, 1, 0, 0, 0), 6 => new(0, 1, -1, 0, 0, 0), 7 => new(0, -1, -1, 0, 0, 0),
        8 => new(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
    };
    private static BitmapSource Former(BitmapSource input, ushort orientation, byte[]? profile)
    {
        BitmapSource converted = input;
        if (profile is not null)
        {
            bool high = StripeBitmapWriter.HighPrecision(input);
            var pixels = new FormatConvertedBitmap(input, high ? PixelFormats.Rgba64 : PixelFormats.Bgra32, null, 0);
            int stride = pixels.PixelWidth * (high ? 8 : 4); byte[] bytes = new byte[stride * pixels.PixelHeight]; pixels.CopyPixels(bytes, stride, 0);
            byte[] output = LittleCmsTransform.ConvertBgra(bytes, pixels.PixelWidth, pixels.PixelHeight, DisplayColorTarget.Standard.Profile, profile, high);
            converted = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Bgra32, null, output, pixels.PixelWidth * 4);
        }
        var matrix = Orientation(orientation);
        if (!matrix.IsIdentity) converted = new TransformedBitmap(converted, new MatrixTransform(matrix));
        var pbgra = new FormatConvertedBitmap(converted, PixelFormats.Pbgra32, null, 0);
        // Former whole-image managed copy, restricted to small synthetic test data.
        byte[] final = Pixels(pbgra); var detached = BitmapSource.Create(pbgra.PixelWidth, pbgra.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, final, pbgra.PixelWidth * 4); detached.Freeze(); return detached;
    }
    private static void Precision()
    {
        byte[] p3 = LittleCmsTransform.ProfileForPng(new(false, null, [.3127, .3290, .68, .32, .265, .69, .15, .06]));
        foreach (bool high in new[] { false, true }) foreach (bool icc in new[] { false, true })
        {
            var input = Pattern(high);
            using var session = icc ? new LittleCmsTransform.Session(DisplayColorTarget.Standard.Profile, p3, high) : null;
            for (ushort orientation = 1; orientation <= 8; orientation++)
            {
                var expected = Former(input, orientation, icc ? p3 : null);
                var actual = StripeBitmapWriter.Convert(input, orientation, session, high, default);
                Check(actual is WriteableBitmap && actual.IsFrozen && actual.Format == PixelFormats.Pbgra32, "Output still retains a transform/decoder surface");
                Check(actual.PixelWidth == expected.PixelWidth && actual.PixelHeight == expected.PixelHeight && Pixels(actual).SequenceEqual(Pixels(expected)), $"Stripe differs: high={high}, ICC/P3={icc}, EXIF={orientation}");
            }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool rejected = false;
        try { StripeBitmapWriter.Convert(Pattern(false), 6, null, false, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "Stripe cancellation ignored");
    }
    private static void Compatibility(string root)
    {
        var paths = MetadataCompatibilityTests.Fixture(root, "compatible", 6, width: 337, height: 1073); var loader = new WicImageLoader();
        var ordinary = loader.Load(paths.Base, default); var actual = loader.Load(paths.Broken, default);
        var expected = Former(ordinary.Source, 6, null);
        Check(Pixels(actual.Source).SequenceEqual(Pixels(expected)) && actual.Metadata?.Warning?.Contains("兼容读取") == true, "Compatibility or orthogonal output changed");
        using (var exclusive = new FileStream(paths.Broken, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Pixels(actual.Source);
        var bad = MetadataCompatibilityTests.Fixture(root, "invalid", invalidIcc: true); bool refused = false;
        try { loader.Load(bad.Broken, default); } catch (NotSupportedException) { refused = true; }
        Check(refused && loader.ComparisonPolicy.WorkingBytes == 0, "Invalid ICC accepted or failure leaked work");
    }
    private static void Five()
    {
        string gallery = Path.GetFullPath("work/memory-five/gallery"); var loader = new WicImageLoader(); var policy = loader.ComparisonPolicy;
        var reference = loader.Load(Path.Combine(gallery, "00.jpg"), default); policy.SetSources(reference, reference); loader.SetComparisonMode(true);
        LoadedImage? current = null; var ids = new Dictionary<int, long>();
        for (int i = 1; i <= 5; i++)
        {
            var next = loader.Load(Path.Combine(gallery, $"{i:00}.jpg"), default); Check(next.PixelWidth * next.PixelHeight == 24_000_000, "Fixture is not 24MP");
            policy.SetSources(next, reference); policy.ReleaseDecoded(next); ids[i] = ComparisonLoadPolicy.Identity(next); current = next;
            Check(loader.CacheFullCount <= 5 && policy.ResidentBytes <= ComparisonBudget.Resident && policy.WorkingBytes == 0, "Count/residency/work budget exceeded");
        }
        Check(loader.CacheFullCount == 5 && policy.ResidentFullCount == 6, "Five recent plus one fixed reference not retained or counted correctly");
        int decodes = loader.DecodeCount, hits = loader.CacheHits;
        for (int i = 5; i >= 1; i--)
        {
            var next = loader.Load(Path.Combine(gallery, $"{i:00}.jpg"), default); Check(ComparisonLoadPolicy.Identity(next) == ids[i], "Recent full was decoded again");
            policy.SetSources(next, reference); policy.ReleaseDecoded(next); current = next;
        }
        Check(loader.DecodeCount == decodes && loader.CacheHits == hits + 5, "Five full revisits missed cache");
        var sixth = loader.Load(Path.Combine(gallery, "06.jpg"), default); policy.SetSources(sixth, reference); policy.ReleaseDecoded(sixth);
        var cache = Field<ImageMemoryCache>(loader, "cache");
        Check(loader.CacheFullCount == 5 && !cache.FullSources.ContainsKey(ids[5]) && cache.FullSources.ContainsKey(ids[1]), "Sixth admission did not evict least-recent full");
        policy.SetSources(sixth, sixth); Check(policy.ResidentFullCount == 5, "Shared reference Source double counted"); policy.SetSources(sixth, reference);
        long largeWork = checked(45_000_000L * 12 + StripeBitmapWriter.ScratchEstimate(9000));
        using (policy.BeginWorking(largeWork)) Check(loader.CacheFullCount <= 1 && policy.ResidentBytes + policy.WorkingBytes <= ComparisonBudget.Decode, "Large-image work failed to reduce cache before reservation");
        Check(policy.WorkingBytes == 0 && policy.PendingCount == 0 && policy.PeakWorkingBytes <= ComparisonBudget.Decode, "Work/pending leaked");
        Console.WriteLine($"FIVE full-revisits=5 hits={loader.CacheHits - hits} cacheAfterLargeWork={loader.CacheFullCount} peakReservedMiB={policy.PeakWorkingBytes / 1048576d:0.0}");
    }
    private static void Toast(string root)
    {
        var window = new MainWindow(); try
        {
            var owner = Field<PaneContext>(window, "activePane"); owner.Viewer.SetPrefetchEligibility(false);
            Wait(owner.Viewer.OpenAsync(Path.GetFullPath("work/quality-loading/desktop-fixtures/A.jpg"))); Wait((Task)Call(window, "ToggleComparisonAsync", owner)!);
            owner.Viewer.SetPrefetchEligibility(false);
            owner.Feedback = "人工文件操作提示";
            ((MenuItem)window.FindName("LinkComparisonMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(((TextBlock)window.FindName("LinkToastText")).Text == "联动已关闭" && owner.Feedback == "人工文件操作提示", "Menu toast changed operation feedback or has wrong text");
            Wait((Task)Call(window, "ExecuteCommandAsync", CommandCatalog.LinkComparison, null, 1d, owner)!);
            Check(((TextBlock)window.FindName("LinkToastText")).Text == "联动已开启", "Command/menu disagree");
            var visual = (FrameworkElement)window.Content; window.Content = null; visual.Measure(new Size(1280, 800)); visual.Arrange(new Rect(0, 0, 1280, 800)); visual.UpdateLayout();
            var image = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); image.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var file = File.Create(Path.Combine(root, "link-toast.png"))) png.Save(file);
            Until(() => ((Border)window.FindName("LinkToast")).Visibility == Visibility.Collapsed);
            Call(window, "ShowLinkState", false); Call(window, "ExitComparison", owner);
            Check(((Border)window.FindName("LinkToast")).Visibility == Visibility.Collapsed && ((TextBlock)window.FindName("LinkToastText")).Text == "", "Exit left stale link toast");
        }
        finally { window.Close(); }
    }
}

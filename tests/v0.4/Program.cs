using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.ViewModels;
using LightView.Core.Viewing;

namespace LightView.Tests;

internal static class Program
{
    private static readonly WicImageLoader Loader = new();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--echo-args") { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(args.Skip(1))); return 0; }
        if (args.Length is 2 or 3 && args[0] == "--infinity-v1") return InfinityV1Tests.Run(args[1], args.Length == 3 ? args[2] : null);
        if (args.Length is 2 or 3 && args[0] == "--pointer-redesign") return PointerRedesignTests.Run(args[1],args.Length==3?args[2]:null);
        if (args.Length == 2 && args[0] == "--photo-optimizations") return PhotoOptimizationTests.Run(args[1]);
        if (args.Length is 2 or 3 && args[0] == "--cache-optimization") return CacheOptimizationTests.Run(args[1], args.Length == 3 ? args[2] : null);
        if (args.Length is 2 or 3 && args[0] == "--memory-five") return MemoryFiveTests.Run(args[1], args.Length == 3 ? args[2] : null);
        if (args.Length is 2 or 3 && args[0] == "--first-look-histogram") return FirstLookHistogramTests.Run(args[1], args.Length == 3 ? args[2] : null);
        if (args.Length == 2 && args[0] == "--split-view") return SplitViewTests.Run(args[1]);
        if (args.Length == 2 && args[0] == "--split-view-layout") return SplitViewTests.Run(args[1], true);
        if (args.Length is 2 or 3 && args[0] == "--usability-details") return UsabilityDetailsTests.Run(args[1], args.Length==3?args[2]:null);
        if (args.Length is 2 or 3 && args[0] == "--ui-redesign") return UiRedesignTests.Run(args[1],args.Length==3?args[2]:null);

        if (args.Length == 3 && args[0] == "--v10-diagnostics") return V10Diagnostics.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--v10-path-status") return V10Diagnostics.PathsOnly(args[1]);
        if (args.Length == 2 && args[0] == "--compat") return CompatibilityValidation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--distribution-paths") return DistributionValidation.Paths(args[1]);
        if (args.Length == 3 && args[0] == "--shell-launch-ui") return ShellLaunchValidation.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--ime-input") return ImeInputValidation.Run(args[1]);
        if (args.Length == 3 && args[0] == "--ime-ui") return ImeInputValidation.Interactive(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--progressive-performance")
            return ProgressivePerformanceProfile.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--progressive-preview")
        {
            Directory.CreateDirectory(args[1]); int failed = 0;
            foreach (var (name, test) in ProgressivePreviewTests.Cases(args[1]))
                try { test(); Console.WriteLine($"PASS {name}"); }
                catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error}"); }
            return failed == 0 ? 0 : 1;
        }
        if (args.Length == 3 && args[0] == "--navigation-latency-profile")
            return NavigationLatencyProfile.Run(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--diagnose-navigation")
            return NavigationDiagnostic.Run(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--probe-decoder")
            return NavigationDiagnostic.Probe(args[1], args[2]);
        if (args.Length == 2 && args[0] is "--visual-resources" or "--style-gallery")
            return VisualResourceValidation.Run(args[1], args[0] == "--style-gallery");
        if (args.Length == 2 && args[0] == "--window-appearance")
            return WindowAppearanceValidation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--main-layout")
            return MainWindowValidation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--settings-v02")
            return SettingsV02Validation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--appearance-ui")
            return AppearanceUiValidation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--fullscreen-v02")
            return FullscreenValidation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--settings-navigation")
            return SettingsNavigationValidation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--settings-transfer")
            return SettingsTransferValidation.Run(args[1]);
        if (args.Length == 2 && args[0] == "--registration-v02")
            return RegistrationValidation.Run(args[1]);
        if (args.Length == 6 && args[0] == "--crash-file-op")
            return UndoRecoveryTests.CrashChild(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 4 && args[0] == "--performance")
            return PerformanceValidation.Run(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--performance-cache")
            return PerformanceValidation.Run(args[1], args[2], args[3], true);
        if (args.Length == 3 && args[0] == "--instance-client")
            return InstanceTests.RunClient(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--instance-client")
            return InstanceTests.RunClient(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--first-render")
            return PerformanceValidation.FirstRender(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--adjacent-navigation")
            return PerformanceValidation.AdjacentNavigation(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--display")
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                LightView.App.Platform.DisplayColorProbe.ReadActiveDisplays(),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--color")
            return ColorValidation.Run(args[1]);
        string root = Path.GetFullPath(args.Length > 0
            ? args[0] : Path.Combine(Environment.CurrentDirectory, "work", "tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var tests = new (string Name, Action Test)[]
        {
            ("formats are case insensitive and restricted", FormatPolicy),
            ("PNG decodes and releases source handle", () => DecodeAndRelease(root, "sample.png", new PngBitmapEncoder())),
            ("JPEG decodes and releases source handle", () => DecodeAndRelease(root, "sample.jpg", new JpegBitmapEncoder())),
            ("BMP decodes and releases source handle", () => DecodeAndRelease(root, "sample.bmp", new BmpBitmapEncoder())),
            ("missing source reports failure", () => MissingFile(root)),
            ("corrupt image reports failure and releases handle", () => CorruptFile(root)),
            ("cancelled request does not decode", () => Cancelled(root)),
            ("late load cannot replace newer displayed image", StaleLoad),
            ("failed load preserves displayed image", () => FailedLoad(root))
        };
        tests = tests.Concat(ViewportTests.Cases).Concat(FolderTests.Cases(root))
            .Concat(SettingsTests.Cases(root)).Concat(PresetTests.Cases(root)).Concat(ViewportReloadTests.Cases)
            .Concat(CopyTests.Cases(root)).Concat(MoveTests.Cases(root)).Concat(UndoRecoveryTests.Cases(root)).Concat(CacheTests.Cases(root))
            .Concat(InstanceTests.Cases(root)).Concat(PrefetchTests.Cases(root)).Concat(DetailsNavigationTests.Cases(root))
            .Concat(MetadataCompatibilityTests.Cases(root))
            .Concat(AcceptanceGapTests.Cases(root)).Concat(OutlinedTextTests.Cases).ToArray();
        if (args.Length == 3 && args[1] == "--filter") tests = tests.Where(test => args[2].Split('|').Contains(test.Name)).ToArray();
        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Test(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine($"FAIL {test.Name}: {error}");
            }
        }
        Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed; fixtures: {root}");
        return failures == 0 ? 0 : 1;
    }

    private static BitmapSource MakeImage()
    {
        byte[] pixels = new byte[64 * 32 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 30; pixels[i + 1] = 100; pixels[i + 2] = 220; pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        image.Freeze();
        return image;
    }

    private static void FormatPolicy()
    {
        Check(ImageFormats.IsSupported("PHOTO.JPEG"), "uppercase JPEG must be supported");
        Check(ImageFormats.IsSupported("x.png"), "PNG must be supported");
        Check(!ImageFormats.IsSupported("x.gif"), "GIF is outside T01");
        Check(!ImageFormats.IsSupported("x.jpg.exe"), "double extension must not bypass filter");
    }

    private static void DecodeAndRelease(string root, string name, BitmapEncoder encoder)
    {
        string path = Path.Combine(root, name);
        encoder.Frames.Add(BitmapFrame.Create(MakeImage()));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var loaded = Loader.Load(path, CancellationToken.None);
        Check(loaded.PixelWidth == 64 && loaded.PixelHeight == 32, "decoded dimensions differ");
        Check(loaded.Source.IsFrozen, "decoded image must be transferable");
        string moved = path + ".moved";
        File.Move(path, moved);
        File.Move(moved, path);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        byte[] pixels = new byte[4];
        loaded.Source.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixels, 4, 0);
    }

    private static void MissingFile(string root)
    {
        bool failed = false;
        try { Loader.Load(Path.Combine(root, "not-found.png"), CancellationToken.None); }
        catch (FileNotFoundException) { failed = true; }
        Check(failed, "missing file did not report FileNotFoundException");
    }

    private static void CorruptFile(string root)
    {
        string path = Path.Combine(root, "corrupt.png");
        File.WriteAllText(path, "not an image");
        bool failed = false;
        try { Loader.Load(path, CancellationToken.None); }
        catch (Exception error) when (error is IOException or NotSupportedException or FormatException)
        { failed = true; }
        Check(failed, "corrupt image was accepted");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void Cancelled(string root)
    {
        bool cancelled = false;
        try { Loader.Load(Path.Combine(root, "sample.png"), new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancelled request reached file access");
    }

    private static void StaleLoad()
    {
        var fake = new ControlledLoader();
        var viewer = new ViewerViewModel(fake);
        Task oldLoad = viewer.OpenAsync("old.png");
        Check(fake.OldStarted.Wait(TimeSpan.FromSeconds(5)), "old request did not start");
        Task newLoad = viewer.OpenAsync("new.png");
        newLoad.GetAwaiter().GetResult();
        fake.ReleaseOld.Set();
        oldLoad.GetAwaiter().GetResult();
        Check(viewer.DisplayedImage?.FilePath == "new.png", "old completion replaced new picture");
        Check(!viewer.IsLoading, "loading indicator remained active");
    }

    private static void FailedLoad(string root)
    {
        var viewer = new ViewerViewModel(Loader);
        viewer.OpenAsync(Path.Combine(root, "sample.png")).GetAwaiter().GetResult();
        LoadedImage? previous = viewer.DisplayedImage;
        viewer.OpenAsync(Path.Combine(root, "corrupt.png")).GetAwaiter().GetResult();
        Check(previous is not null && ReferenceEquals(previous, viewer.DisplayedImage), "last good image was lost");
        Check(!FrameworkApi.IsNullOrWhiteSpace(viewer.ErrorMessage), "failure is not visible to user");
        Check(!viewer.IsLoading, "loading flag not reset on failure");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ControlledLoader : IImageLoader
    {
        public ManualResetEventSlim OldStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseOld { get; } = new(false);

        public LoadedImage Load(string path, CancellationToken cancellationToken)
        {
            if (path == "old.png")
            {
                OldStarted.Set();
                if (!ReleaseOld.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Test did not release old load.");
            }
            return new LoadedImage(path, MakeImage(), "Synthetic test image", 0);
        }
    }
}

using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.ViewModels;

namespace LightView.Tests;

internal static class NavigationDiagnostic
{
    public static int Probe(string path, string output)
    {
        Directory.CreateDirectory(output); var rows = new List<object>();
        foreach (var cache in new[] { BitmapCacheOption.OnLoad, BitmapCacheOption.OnDemand, BitmapCacheOption.None })
        foreach (string step in new[] { "create", "freeze", "contexts", "metadata", "pixels", "color-pipeline" })
        {
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, cache);
                var frame = decoder.Frames[0]; string detail = $"{frame.PixelWidth}x{frame.PixelHeight}, {frame.Format}";
                if (step != "create") frame.Freeze();
                if (step == "contexts") detail += ", contexts=" + frame.ColorContexts?.Count;
                if (step == "metadata") detail += ", orientation=" + (frame.Metadata as BitmapMetadata)?.GetQuery("/app1/ifd/{ushort=274}");
                if (step is "pixels" or "color-pipeline")
                {
                    BitmapSource source = step == "color-pipeline" ? EmbeddedColorPipeline.Convert(frame, DisplayColorTarget.Standard, PngColorMetadata.Read(stream), EmbeddedProfileDeclaration.IsPresent(stream)).Source : frame;
                    var display = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                    int stride = checked(display.PixelWidth * 4); byte[] pixels = new byte[checked(stride * display.PixelHeight)]; display.CopyPixels(pixels, stride, 0);
                    detail += ", pixelsSha256=" + FrameworkApi.Hex(FrameworkApi.Sha256(pixels));
                }
                rows.Add(new { cache = cache.ToString(), step, passed = true, detail }); Console.WriteLine($"PASS {cache}/{step}: {detail}");
            }
            catch (Exception error) { rows.Add(new { cache = cache.ToString(), step, passed = false, error = error.ToString() }); Console.WriteLine($"FAIL {cache}/{step}: {error.Message}"); }
        }
        using (var stream = File.OpenRead(path)) { var exif = ExifReader.Read(stream); Console.WriteLine($"Independent EXIF fields={exif.Fields.Count}, warning={exif.Warning}"); }
        File.WriteAllText(Path.Combine(output, "decoder-probe.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })); return 0;
    }
    public static int Run(string first, string second, string output)
    {
        first = Path.GetFullPath(first); second = Path.GetFullPath(second);
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var rows = new List<object>(); int failed = 0;
        void Trial(string name, Action body)
        {
            try { body(); rows.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; rows.Add(new { name, passed = false, exception = error.ToString(), hresult = $"0x{error.HResult:X8}" }); Console.WriteLine("FAIL " + name + ": " + error); }
        }
        void Require(ViewerViewModel viewer, string expected)
        {
            if (viewer.ErrorMessage.Length != 0 || !string.Equals(viewer.DisplayedImage?.FilePath, expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Displayed=" + viewer.DisplayedImage?.FilePath + "; error=" + viewer.ErrorMessage);
        }
        foreach (string path in new[] { first, second })
        {
            Trial("isolated STA decode " + Path.GetFileName(path), () => new WicImageLoader().Load(path, CancellationToken.None));
            Trial("isolated pool decode " + Path.GetFileName(path), () => Task.Run(() => new WicImageLoader().Load(path, CancellationToken.None)).GetAwaiter().GetResult());
        }
        foreach (var (a, b) in new[] { (first, second), (second, first) })
        {
            Trial("shared loader pool " + Path.GetFileName(a) + " -> " + Path.GetFileName(b), () =>
            { var loader = new WicImageLoader(); Task.Run(() => loader.Load(a, CancellationToken.None)).GetAwaiter().GetResult(); Task.Run(() => loader.Load(b, CancellationToken.None)).GetAwaiter().GetResult(); });
            Trial("prefetch then foreground " + Path.GetFileName(a) + " -> " + Path.GetFileName(b), () =>
            { var loader = new WicImageLoader(); Task.Run(() => loader.Load(a, CancellationToken.None)).GetAwaiter().GetResult(); Task.Run(() => loader.TryPrefetch(b, CancellationToken.None)).GetAwaiter().GetResult(); Task.Run(() => loader.Load(b, CancellationToken.None)).GetAwaiter().GetResult(); });
            Trial("viewer natural navigation " + Path.GetFileName(a) + " -> " + Path.GetFileName(b), () =>
            {
                var viewer = new ViewerViewModel(new WicImageLoader());
                try
                {
                    viewer.OpenAsync(a).GetAwaiter().GetResult(); Require(viewer, a); viewer.FolderReady.GetAwaiter().GetResult(); viewer.PrefetchReady.GetAwaiter().GetResult();
                    viewer.NavigateAsync(a == first ? 1 : -1).GetAwaiter().GetResult(); Require(viewer, b);
                    viewer.PrefetchReady.GetAwaiter().GetResult();
                }
                finally { viewer.CancelPrefetch(); }
            });
        }
        File.WriteAllText(Path.Combine(output, "diagnostic.json"), JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.UtcNow, productAssembly = typeof(WicImageLoader).Assembly.Location, failed, trials = rows }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RESULT {rows.Count - failed}/{rows.Count} diagnostics passed"); return failed == 0 ? 0 : 1;
    }
}

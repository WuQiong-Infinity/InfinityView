using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Persistence;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Settings;
using LightView.Core.Viewing;

namespace LightView.Tests;

internal static class PhotoOptimizationTests
{
    internal static int Run(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var cases = new (string Name, Action Test)[]
        {
            ("Nikon 0310 exact fields retain original spelling across all byte orders", ByteOrders),
            ("Nikon requires make signature version and exact block size", SupportScope),
            ("Nikon offset count duplicate truncation and budgets fail independently", Bounds),
            ("Nikon empty fields padding unknown bases and hostile text", Strings),
            ("MakerNote damage retains standard EXIF and read stream position", ExifIsolation),
            ("WIC binds style to preview and full without pixel or source changes", () => Integration(root)),
            ("style follows last displayed request and copy uses visible snapshot", () => Snapshot(root)),
            ("copy whitelist omits location GPS serial timestamps errors and unknown fields", CopyWhitelist),
            ("copy empty oversized and busy clipboard preserve existing clipboard", CopyFailures),
            ("copy command rebinds without default shortcut repeat or text capture", CopyCommand),
            ("keep view off and fit mode retain previous defaults", GeometryDefaults),
            ("keep physical zoom normalized centers across dimensions and DPI", GeometryCenters),
            ("cross-image restore clamps bounds and centers short axes only", GeometryBounds),
            ("keep view invalid or empty geometry falls back safely", GeometryInvalid),
            ("adjacent preview full refresh and rename apply transform once", Publication),
            ("external open and failed or cancelled requests do not retain adjacent intent", () => RequestIntent(root)),
            ("settings absent field load is false without rewriting source", () => SettingsDefault(root)),
            ("keep view persists clones exports imports only selected viewing group", () => SettingsRoundTrip(root)),
            ("keep view cancelled failed and read-only settings never change applied value", SettingsCommit),
            ("malformed MakerNote fuzz stays bounded without clearing standard EXIF", Fuzz)
        };
        var results = new List<object>(); int failed = 0;
        foreach (var item in cases)
        {
            var watch = Stopwatch.StartNew();
            try { item.Test(); Console.WriteLine("PASS " + item.Name); results.Add(new { name = item.Name, passed = true, ms = watch.Elapsed.TotalMilliseconds }); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + item.Name + ": " + error); results.Add(new { name = item.Name, passed = false, error = error.ToString() }); }
        }
        File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { version = "1.1.0-rc.1", passed = cases.Length - failed, total = cases.Length, scope = "Synthetic fixtures and automated in-process tests; not desktop UI acceptance", results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT {cases.Length-failed}/{cases.Length} photo optimization checks passed");
        return failed == 0 ? 0 : 1;
    }
    private static void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
    private static void Near(double actual, double expected) => Check(Math.Abs(actual - expected) < 0.000001, $"Expected {expected}, got {actual}");
    private static void U16(byte[] b, int p, ushort n, bool le) { if (le) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p, 2), n); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p, 2), n); }
    private static void U32(byte[] b, int p, uint n, bool le) { if (le) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p, 4), n); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p, 4), n); }
    internal static byte[] Note(bool little = true, string name = "Demo_recipe-A", string basis = "FLEXIBLE COLOR")
    {
        byte[] b = new byte[144]; new byte[] { 78, 105, 107, 111, 110, 0, 2, 17, 0, 0 }.CopyTo(b, 0);
        b[10] = b[11] = little ? (byte)73 : (byte)77; U16(b, 12, 42, little); U32(b, 14, 8, little);
        U16(b, 18, 1, little); U16(b, 20, 0x23, little); U16(b, 22, 7, little); U32(b, 24, 108, little); U32(b, 28, 26, little);
        Encoding.ASCII.GetBytes("03100310").CopyTo(b, 36); Encoding.ASCII.GetBytes(name).CopyTo(b, 44); Encoding.ASCII.GetBytes(basis).CopyTo(b, 64); return b;
    }
    internal static byte[] Tiff(byte[] note, bool little = true, string make = "NIKON CORPORATION", bool duplicate = false)
    {
        byte[] b = new byte[128 + note.Length]; b[0] = b[1] = little ? (byte)73 : (byte)77;
        U16(b, 2, 42, little); U32(b, 4, 8, little); U16(b, 8, 2, little);
        U16(b, 10, 271, little); U16(b, 12, 2, little); U32(b, 14, (uint)make.Length + 1, little); U32(b, 18, 48, little);
        U16(b, 22, 34665, little); U16(b, 24, 4, little); U32(b, 26, 1, little); U32(b, 30, 80, little); Encoding.ASCII.GetBytes(make).CopyTo(b, 48);
        U16(b, 80, duplicate ? (ushort)2 : (ushort)1, little);
        void Entry(int at) { U16(b, at, 37500, little); U16(b, at + 2, 7, little); U32(b, at + 4, (uint)note.Length, little); U32(b, at + 8, 128, little); }
        Entry(82); if (duplicate) Entry(94); note.CopyTo(b, 128); return b;
    }
    private static CameraStyle Parse(byte[] note) => CameraStyleReader.Read(note, 0, note.Length, "NIKON CORPORATION");
    private static void ByteOrders()
    {
        foreach (bool outer in new[] { false, true }) foreach (bool inner in new[] { false, true })
        { var result = ExifReader.ParseTiff(Tiff(Note(inner), outer)); Check(result.Style is { State: CameraStyleState.Parsed, Name: "Demo_recipe-A", BaseName: "FLEXIBLE COLOR" }, "Endian/offset or spelling incorrect"); Check(result.Style!.Rows[1].Value == "灵活颜色（FLEXIBLE COLOR）", "Known base mapping absent"); }
    }
    private static void SupportScope()
    {
        Check(CameraStyleReader.Read(Note(), 0, 144, "CANON").State == CameraStyleState.Unsupported, "Wrong make accepted");
        foreach (int at in new[] { 0, 6, 7, 36 }) { byte[] b = Note(); b[at] ^= 1; Check(Parse(b).State == CameraStyleState.Unsupported, "Unsupported signature/version accepted"); }
        foreach (uint size in new uint[] { 74, 107, 109, uint.MaxValue }) { byte[] b = Note(); U32(b, 24, size, true); Check(Parse(b).State == CameraStyleState.Unsupported, "Unknown length accepted"); }
        byte[] count = Note(); U16(count, 20, 0x24, true); Check(Parse(count).State == CameraStyleState.Missing, "Missing tag invented");
    }
    private static void Bounds()
    {
        foreach (int size in new[] { 0, 9, 17, 18, 35, 143 }) Check(Parse(Note().Take(size).ToArray()).State == CameraStyleState.Malformed, "Truncation accepted");
        foreach (uint offset in new uint[] { 0, 8, 25, 27, uint.MaxValue }) { byte[] b = Note(); U32(b, 28, offset, true); Check(Parse(b).State == CameraStyleState.Malformed, "Offset escaped block bounds"); }
        byte[] bad = Note(); U32(bad, 14, uint.MaxValue, true); Check(Parse(bad).State == CameraStyleState.Malformed, "IFD wraparound");
        bad = Note(); U16(bad, 18, 513, true); Check(Parse(bad).State == CameraStyleState.OverBudget, "Entry budget absent");
        bad = new byte[65537]; Check(Parse(bad).State == CameraStyleState.OverBudget, "Byte budget absent");
        Check(CameraStyleReader.Read(Note(), long.MaxValue, 108, "NIKON").State == CameraStyleState.Malformed, "Long offset overflow");
        bad = new byte[264]; Note().CopyTo(bad, 0); U16(bad, 18, 2, true);
        Array.Copy(bad, 20, bad, 32, 12); U32(bad, 28, 38, true); U32(bad, 40, 146, true);
        Array.Copy(Note(), 36, bad, 48, 108); Array.Copy(Note(name:"Other"), 36, bad, 156, 108);
        Check(Parse(bad).State == CameraStyleState.Malformed, "Duplicate PC accepted");
        Check(ExifReader.ParseTiff(Tiff(Note(), duplicate: true)).Style?.State == CameraStyleState.Malformed, "Duplicate MakerNote accepted");
        byte[] outer = Tiff(Note()); U16(outer,8,3,true); Array.Copy(outer,22,outer,34,12);
        Check(ExifReader.ParseTiff(outer).Style?.State == CameraStyleState.Malformed && ExifReader.ParseTiff(outer).Fields.Count==1,"Ambiguous EXIF pointer accepted or cleared ordinary fields");
        byte[] max = new byte[65536]; Note().CopyTo(max, 0); Check(Parse(max).State == CameraStyleState.Parsed, "Max allowed bytes rejected");
        // A nonzero next-IFD pointer is ignored; it is never followed.
        bad = Note(); U32(bad, 32, 8, true); Check(Parse(bad).State == CameraStyleState.Parsed, "Reader followed an irrelevant IFD chain");
    }
    private static void Strings()
    {
        Check(Parse(Note(name: "", basis: "Custom Base")).Rows.Single().Value == "Custom Base", "Empty name/unknown base invented");
        Check(Parse(Note(name: "Name", basis: "")).Rows.Single().Value == "Name", "Empty base invented");
        Check(Parse(Note(name: "Same", basis: "Same")).Rows.Count == 1, "Duplicate base row");
        Check(Parse(Note(name: "", basis: "")).State == CameraStyleState.Missing, "Both empty treated as real");
        Check(Parse(Note(name: "Mixed_Name   ")).Name == "Mixed_Name", "Padding not stripped");
        foreach (byte value in new byte[] { 0, 10, 31, 127, 0xff }) { byte[] b = Note(); b[46] = value; Check(Parse(b).State == CameraStyleState.Malformed, "Hostile text accepted"); }
    }
    private static byte[] Jpeg(byte[] tiff, byte[] jpeg, bool invalidIcc = false)
    {
        using var output = new MemoryStream(); output.Write(jpeg.AsSpan(0, 2));
        void Segment(byte marker, byte[] data) { int size = data.Length + 2; output.Write(new byte[] { 255, marker, (byte)(size >> 8), (byte)size }); output.Write(data); }
        Segment(0xe1, "Exif\0\0"u8.ToArray().Concat(tiff).ToArray());
        if (invalidIcc) Segment(0xe2, "ICC_PROFILE\0"u8.ToArray().Concat(new byte[] { 1, 1, 0, 1, 2, 3 }).ToArray());
        output.Write(jpeg.AsSpan(2)); return output.ToArray();
    }
    private static void ExifIsolation()
    {
        byte[] b = Tiff(Note()); U32(b, 90, uint.MaxValue, true);
        var result = ExifReader.ParseTiff(b); Check(result.Fields.Single().Value == "NIKON CORPORATION" && result.Style?.State == CameraStyleState.Malformed && result.Warning is not null, "Bad style cleared ordinary EXIF");
        using var stream = new MemoryStream(Jpeg(Tiff(Note()), new byte[] { 255, 216, 255, 217 })); stream.Position = 3;
        Check(ExifReader.Read(stream).Style?.Name == "Demo_recipe-A" && stream.Position == 3, "Read changed stream position");
    }
    private static void Integration(string root)
    {
        byte[] pixels = new byte[2400 * 1600 * 3]; for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 40; pixels[i+1] = 100; pixels[i+2] = 180; }
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2400, 1600, 96, 96, PixelFormats.Bgr24, null, pixels, 2400 * 3)));
        using var raw = new MemoryStream(); encoder.Save(raw); byte[] jpeg = raw.ToArray();
        string path = Path.Combine(root, "人工尼康 横图.jpg"), bad = Path.Combine(root, "人工损坏风格.jpg"), plain = Path.Combine(root, "人工无风格.jpg");
        File.WriteAllBytes(path, Jpeg(Tiff(Note()), jpeg)); File.WriteAllBytes(plain, jpeg);
        byte[] note = Note(); U32(note, 28, uint.MaxValue, true); File.WriteAllBytes(bad, Jpeg(Tiff(note), jpeg));
        byte[] hash = FrameworkApi.Sha256(File.ReadAllBytes(path)); var loader = new WicImageLoader();
        var preview = loader.LoadPreview(path, 768, CancellationToken.None); var full = loader.Load(path, CancellationToken.None); var broken = loader.Load(bad, CancellationToken.None); var baseline = loader.Load(plain, CancellationToken.None);
        Check(preview.IsPreview && preview.Metadata?.Style?.Name == full.Metadata?.Style?.Name && full.Metadata?.Style?.Name == "Demo_recipe-A", "Style lost in preview/full");
        Check(Pixels(full.Source).SequenceEqual(Pixels(broken.Source)) && Pixels(full.Source).SequenceEqual(Pixels(baseline.Source)) && broken.Metadata?.Shooting.Count == 1, "Optional metadata changed pixels/ordinary EXIF");
        Check(hash.SequenceEqual(FrameworkApi.Sha256(File.ReadAllBytes(path))), "Source changed"); using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        string icc = Path.Combine(root, "人工坏ICC.jpg"); File.WriteAllBytes(icc, Jpeg(Tiff(note), jpeg, true)); bool refused = false;
        try { loader.LoadPreview(icc, 768, CancellationToken.None); } catch (NotSupportedException) { refused = true; } Check(refused, "Bad style weakened ICC refusal");
        string portrait = Path.Combine(root, "人工尼康 竖图.jpg"); var portraitEncoder = new JpegBitmapEncoder(); portraitEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1600, 2400, 96, 96, PixelFormats.Bgr24, null, pixels, 1600 * 3))); using var port = new MemoryStream(); portraitEncoder.Save(port); File.WriteAllBytes(portrait, Jpeg(Tiff(Note(name: "Portrait_B")), port.ToArray()));
        // Measure only optional EXIF parsing, not startup or overall performance gates.
        var watch = Stopwatch.StartNew(); for (int i = 0; i < 200; i++) ExifReader.ParseTiff(Tiff(Note())); watch.Stop();
        File.WriteAllText(Path.Combine(root, "metadata-cost.json"), JsonSerializer.Serialize(new { samples = 200, totalMs = watch.Elapsed.TotalMilliseconds, meanMs = watch.Elapsed.TotalMilliseconds / 200, scope = "Synthetic bounded EXIF parse only; not startup performance" }));
    }
    private static byte[] Pixels(BitmapSource bitmap) { var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); byte[] b = new byte[converted.PixelWidth*converted.PixelHeight*4]; converted.CopyPixels(b, converted.PixelWidth*4, 0); return b; }
    private static LoadedImage Picture(string path, string name = "A", int width = 1200, int height = 800, bool preview = false)
    {
        int pw = preview ? width / 4 : width, ph = preview ? height / 4 : height;
        var source = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgr24, null, new byte[pw*ph*3], pw*3); source.Freeze();
        return new(path, source, "Synthetic", 0, Metadata: new(width, height, 96, 96, "Bgr24", [new("相机型号", name)], Style: new(name, "FLEXIBLE COLOR")), IsPreview: preview, OriginalWidth: width, OriginalHeight: height);
    }
    private sealed class DelayedLoader(LoadedImage a, LoadedImage b) : IImageLoader
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public LoadedImage Load(string path, CancellationToken token) { if (path == b.FilePath) { Entered.Set(); Release.Wait(); } return path == a.FilePath ? a : b; }
    }
    private static void Snapshot(string root)
    {
        var a = Picture(Path.Combine(root, "a.png")); var b = Picture(Path.Combine(root, "b.png"), "B"); var loader = new DelayedLoader(a,b); var viewer = new ViewerViewModel(loader);
        viewer.OpenAsync(a.FilePath).GetAwaiter().GetResult(); Task pending = viewer.OpenAsync(b.FilePath); Check(loader.Entered.Wait(5000), "Delayed loader did not start");
        Check(viewer.IsLoading && ShootingInfoFormatter.Format(viewer.DisplayedImage)!.Contains("相机型号: A") && viewer.DetailsRows.Any(row=>row.Value == "A"), "Copied requested rather than displayed image");
        loader.Release.Set(); pending.GetAwaiter().GetResult(); Check(ShootingInfoFormatter.Format(viewer.DisplayedImage)!.Contains("相机型号: B"), "Copy/style did not follow display");
        Check(ShootingInfoFormatter.Format(b with { IsPreview = true })!.Contains("相机型号: B"), "Preview metadata copy denied");
    }
    private static void CopyWhitelist()
    {
        var image = Picture("C:/NEVER_PATH/NEVER_FILE.jpg") with { ColorDescription = "NEVER_ERROR" };
        image = image with { Metadata = image.Metadata! with { Shooting = [new("ISO", "640"),new("相机型号", "Demo"),new("焦距","35 mm"),new("等效35mm焦距","52 mm"),new("位置","NEVER_LOCATION"),new("GPS","NEVER_GPS"),new("序列号","NEVER_SERIAL"),new("拍摄时间","NEVER_TIME"),new("任意字段","NEVER_UNKNOWN")], Warning = "NEVER_WARNING" } };
        string text = ShootingInfoFormatter.Format(image)!; Check(!text.Contains("NEVER") && text.IndexOf("相机型号",StringComparison.Ordinal)<text.IndexOf("ISO",StringComparison.Ordinal) && text.Contains("等效35mm焦距: 52 mm"), "Whitelist/order invalid");
        Check(text.Split(new[]{"等效35mm焦距"},StringSplitOptions.None).Length == 2 && text.Contains("Picture Control: A"), "Focal/style duplicates");
    }
    private static void CopyFailures()
    {
        string clipboard = "ORIGINAL"; void Write(string s) => clipboard = s;
        ShootingInfoFormatter.Copy(null,Write); ShootingInfoFormatter.Copy(Picture("x") with { Metadata=null },Write); Check(clipboard == "ORIGINAL", "Empty cleared clipboard");
        string status = ShootingInfoFormatter.Copy(Picture("x"), _ => throw new ExternalException()); Check(status.Contains("重试") && clipboard == "ORIGINAL", "Busy clipboard failure fatal");
        var image = Picture("x") with { Metadata = new(1,1,96,96,"Bgr24",[new("相机型号",new string('x',513)),new("ISO","1\nSECRET")]) };
        Check(ShootingInfoFormatter.Format(image) is null, "Unsafe fields copied");
    }
    private static void CopyCommand()
    {
        Check(CommandCatalog.Find(CommandCatalog.CopyShootingInfo)?.AllowRepeat == false && !CommandCatalog.DefaultBindings().Any(b=>b.CommandId==CommandCatalog.CopyShootingInfo && b.Gesture.Length>0), "Default key/repeat invalid");
        var bindings = CommandCatalog.DefaultBindings(); bindings.Add(new(CommandCatalog.CopyShootingInfo,"CTRL+C")); var map = new ShortcutMap(bindings);
        Check(map.Resolve("CTRL+C")?.Id == CommandCatalog.CopyShootingInfo && map.Resolve("CTRL+C",true) is null && map.Resolve("CTRL+C",textEntry:true) is null, "Routing bypassed text/repeat guard");
    }
    private static ViewportTransform Geometry(double dpi=1) { var t = new ViewportTransform(); t.Resize(500,300,dpi); t.SetImage(2000,1200); return t; }
    private static void GeometryDefaults() { var t=Geometry(); t.ActualPixels();t.Pan(30,-15);t.SetImage(3000,1600);Check(t.IsFit,"Default changed"); t.SetImage(1200,2400,true);Check(t.IsFit,"Fit mode became manual");Near(t.Zoom,.125); }
    private static void GeometryCenters()
    {
        foreach(double dpi in new[]{1d,1.5,2}) foreach(double zoom in new[]{1d,2.5})
        { var t=Geometry(dpi);t.ActualPixels();t.ZoomAt(250,150,zoom);t.Pan(-80,-40);double u=(250-t.OffsetX)/(t.DipPerPixel*t.ImageWidth),v=(150-t.OffsetY)/(t.DipPerPixel*t.ImageHeight);t.SetImage(4000,2600,true);Near(t.Zoom,zoom);Near((250-t.OffsetX)/(t.DipPerPixel*t.ImageWidth),u);Near((150-t.OffsetY)/(t.DipPerPixel*t.ImageHeight),v); }
    }
    private static void GeometryBounds()
    {
        var t=Geometry();t.ActualPixels();t.Pan(10000,-10000);t.SetImage(3000,2000,true);Near(t.OffsetX,0);Near(t.OffsetY,-1700);
        t.SetImage(100,80,true);Near(t.OffsetX,200);Near(t.OffsetY,110); t.Pan(700,-800);Near(t.OffsetX,900);Near(t.OffsetY,-690);
    }
    private static void GeometryInvalid() { var t=new ViewportTransform();t.SetImage(20,30);t.ActualPixels();t.SetImage(30,20,true);Check(t.IsFit&&FrameworkApi.IsFinite(t.Zoom),"Zero layout did not fit"); t=Geometry();t.ActualPixels();t.SetImage(0,0,true);Check(t.IsFit,"Empty image manual"); }
    private static ImageViewport Viewport() { var v=new ImageViewport{Width=500,Height=300,KeepViewOnImageChange=true,Image=Picture("A")};v.Measure(new Size(500,300));v.Arrange(new Rect(0,0,500,300));v.ActualPixels();return v; }
    private static void Publication()
    {
        var v=Viewport();v.Transform.Pan(-40,20); var b=Picture("B",width:1600,height:2400,preview:true) with {ViewIntent=ImageViewIntent.Adjacent};v.Image=b;Check(!v.Transform.IsFit,$"Adjacent lost zoom, geometry {v.Transform.ViewWidth}x{v.Transform.ViewHeight}, actual {v.ActualWidth}x{v.ActualHeight}, zoom {v.Transform.Zoom}");v.Transform.Pan(2000,-3000);double x=v.Transform.OffsetX,y=v.Transform.OffsetY;
        v.Image=Picture("B",width:1600,height:2400) with {ViewIntent=ImageViewIntent.Adjacent};Near(v.Transform.OffsetX,x);Near(v.Transform.OffsetY,y);
        v.Image=v.Image with {FilePath="renamed-B",ViewIntent=ImageViewIntent.Open};Near(v.Transform.OffsetX,x);Near(v.Transform.OffsetY,y);
        v.Image=Picture("C") with {ViewIntent=ImageViewIntent.Open};Check(v.Transform.IsFit,"External open kept zoom");v.ActualPixels();v.KeepViewOnImageChange=false;Check(!v.Transform.IsFit,"Changing setting moved current view");v.Image=Picture("D") with {ViewIntent=ImageViewIntent.Adjacent};Check(v.Transform.IsFit,"Disabled keep ignored");
    }
    private sealed class FixedLoader(LoadedImage image):IImageLoader { public LoadedImage Load(string path,CancellationToken token)=>image; }
    private static void RequestIntent(string root)
    {
        var cached=Picture(Path.Combine(root,"cache.png"));var viewer=new ViewerViewModel(new FixedLoader(cached));viewer.OpenAsync(cached.FilePath,ImageViewIntent.Adjacent).GetAwaiter().GetResult();Check(viewer.DisplayedImage!.ViewIntent==ImageViewIntent.Adjacent&&cached.ViewIntent==ImageViewIntent.Open,"Intent polluted cache");viewer.OpenAsync(cached.FilePath).GetAwaiter().GetResult();Check(viewer.DisplayedImage!.ViewIntent==ImageViewIntent.Open,"Adjacent leaked into Open");
        var a=Picture(Path.Combine(root,"intent-a.png"));var b=Picture(Path.Combine(root,"intent-b.png"));var delayed=new DelayedLoader(a,b);viewer=new(delayed);viewer.OpenAsync(a.FilePath).GetAwaiter().GetResult();Task old=viewer.OpenAsync(b.FilePath,ImageViewIntent.Adjacent);Check(delayed.Entered.Wait(5000),"Old request not started");viewer.OpenAsync(a.FilePath).GetAwaiter().GetResult();delayed.Release.Set();old.GetAwaiter().GetResult();Check(viewer.DisplayedImage!.FilePath==a.FilePath&&viewer.DisplayedImage.ViewIntent==ImageViewIntent.Open,"Cancelled request altered later intent");
    }
    private static void Fuzz()
    {
        var random = new Random(1942);
        for(int i=0;i<2000;i++)
        {
            byte[] note=Note();for(int m=0;m<4;m++)note[random.Next(note.Length)]=(byte)random.Next(256);
            var parsed=ExifReader.ParseTiff(Tiff(note));Check(parsed.Fields.Single().Value=="NIKON CORPORATION","Fuzz removed standard EXIF");
        }
    }
    private static void SettingsCommit()
    {
        var session=new SettingsSession(new AppSettings(),_=>{},false);session.Draft.KeepViewOnImageChange=true;Check(session.TryClose()&&!session.LastAppliedSnapshot.KeepViewOnImageChange,"Cancel committed draft");
        string? error=session.ApplyAsync(session.Draft,_=>Task.FromResult<string?>("failure")).GetAwaiter().GetResult();Check(error is not null&&!session.LastAppliedSnapshot.KeepViewOnImageChange,"Failure committed keep");
        error=session.ApplyAsync(session.Draft,_=>Task.FromResult<string?>(null)).GetAwaiter().GetResult();Check(error is null&&session.LastAppliedSnapshot.KeepViewOnImageChange,"Success lost keep");
        var readOnly=new SettingsSession(new AppSettings(),_=>{},true);var requested=new AppSettings{KeepViewOnImageChange=true};error=readOnly.ApplyAsync(requested,_=>throw new InvalidOperationException("must not write")).GetAwaiter().GetResult();Check(error is not null&&!readOnly.LastAppliedSnapshot.KeepViewOnImageChange,"Readonly applied keep");
    }
    private static void SettingsDefault(string root)
    {
        string dir=Path.Combine(root,"old-settings");Directory.CreateDirectory(dir);var settings=new AppSettings();string json=JsonSerializer.Serialize(settings,new JsonSerializerOptions(JsonSerializerDefaults.Web));json=json.Replace("\"keepViewOnImageChange\":false,","");string path=Path.Combine(dir,"settings.json");File.WriteAllText(path,json);byte[] before=File.ReadAllBytes(path);Check(!new SettingsStore(dir).Load().Settings.KeepViewOnImageChange&&before.SequenceEqual(File.ReadAllBytes(path)),"Old config rewritten/default enabled");
    }
    private static void SettingsRoundTrip(string root)
    {
        string dir=Path.Combine(root,"keep-settings");var store=new SettingsStore(dir);var settings=store.Load().Settings;settings.KeepViewOnImageChange=true;store.Save(settings);Check(new SettingsStore(dir).Load().Settings.KeepViewOnImageChange&&settings.Clone().KeepViewOnImageChange,"Save/clone lost bool");
        string package=Path.Combine(root,"keep-package.json");SettingsTransfer.Export(package,settings,false,store.FilePath);var incoming=SettingsTransfer.Read(package);Check(incoming.Settings.KeepViewOnImageChange,"Export lost bool");
        Check(SettingsTransfer.Merge(new AppSettings(),incoming,SettingsGroups.Viewing).KeepViewOnImageChange&&!SettingsTransfer.Merge(new AppSettings(),incoming,SettingsGroups.Appearance).KeepViewOnImageChange,"Wrong import group");
        string old=File.ReadAllText(package).Replace("\"keepViewOnImageChange\": true,","");string oldPackage=Path.Combine(root,"old-package.json");File.WriteAllText(oldPackage,old);Check(!SettingsTransfer.Read(oldPackage).Settings.KeepViewOnImageChange,"Missing field incompatible");
    }
}

using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Organizing;
using LightView.App.Persistence;
using LightView.App.Platform;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Settings;
using LightView.Core.Viewing;

namespace LightView.Tests;

internal static class InfinityV1Tests
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static int Run(string root, string? filter)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(root, "UserData"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string style in new[] { "Colors", "Typography", "Controls", "Icons" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/InfinityView;component/Styles/{style}.xaml", UriKind.Relative) });
        LightView.App.Styles.ThemeResources.Refresh(app);
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases = new (string Name, Action Run)[] {
            ("settings order reset about branding and alternate labels", () => Ui(root)),
            ("factory focus cleanup and Delete migration preserve customized bindings", () => Bindings(root)),
            ("WebP lossless alpha lossy orientation animation ICC malformed and cache", () => Webp(root)),
            ("registration renamed executable upgrades legacy receipt and preserves ownership", () => Registration(root)),
            ("InfinityView data paths keep portable and explicit overrides independent", Paths),
            ("Delete advances only captured pane and invalidates same-file peer", () => DeletePane(root)),
            ("native recycle verifies source version and reports actual recycle item", () => Recycle(root)) };
        int failed = 0; if (filter is not null) cases = cases.Where(c => c.Name.Contains(filter)).ToArray();
        foreach (var c in cases) try { c.Run(); Console.WriteLine("PASS " + c.Name); } catch (Exception e) { ++failed; Console.WriteLine("FAIL " + c.Name + ": " + e); }
        Console.WriteLine($"RESULT {cases.Length - failed}/{cases.Length} InfinityView v1 checks passed"); return failed == 0 ? 0 : 1;
    }
    private static void Ui(string root)
    {
        var settings = new SettingsWindow(new AppSettings(), _ => Task.FromResult<string?>(null), null, false);
        var main = new MainWindow();
        try
        {
            var tabs = (TabControl)settings.FindName("SettingsTabs"); Check(((TabItem)tabs.Items[1]).Header.ToString() == "图片详情", "Details is not second");
            tabs.SelectedIndex = 1; Check(((Button)settings.FindName("ResetPageButton")).IsEnabled, "Details reset disabled after reorder");
            tabs.SelectedIndex = 4; Check(((Button)settings.FindName("ResetPageButton")).IsEnabled, "Target reset disabled after reorder");
            settings.FocusAbout(); Check(((TabItem)tabs.SelectedItem).Name == "BackupTab", "About opened wrong page");
            main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            System.Windows.Data.BindingOperations.GetBindingExpression(main, Window.TitleProperty)?.UpdateTarget();
            Check(main.Title == "InfinityView" && main.Icon is not null, "Window title/icon rename missing: " + main.Title);
            Check(typeof(MainWindow).Assembly.GetName().Name == "InfinityView", "Executable assembly not renamed");
            Render(settings, root, "settings-about"); tabs.SelectedIndex = 1; Render(settings, root, "settings-details");
        }
        finally { settings.Close(); main.Close(); }
    }
    private static void Render(Window window, string root, string name)
    {
        var content = (FrameworkElement)window.Content; window.Content = null;
        content.Measure(new Size(1080, 720)); content.Arrange(new Rect(0, 0, 1080, 720)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1080, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(Path.Combine(root, name + ".png"))) png.Save(output);
        window.Content = content;
    }
    private static AppSettings Load(string root, string name, List<ShortcutBinding> bindings)
    {
        string dir = Path.Combine(root, name); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(new AppSettings { Bindings = bindings }, Json));
        return new SettingsStore(dir).Load().Settings;
    }
    private static void Bindings(string root)
    {
        var defaults = CommandCatalog.DefaultBindings(); Check(defaults.Count(b => b.CommandId == CommandCatalog.FocusLeft) == 1 && defaults.Count(b => b.CommandId == CommandCatalog.FocusRight) == 1, "Duplicate default focus commands");
        var map = new ShortcutMap(defaults); Check(map.Resolve("DELETE")?.Id == CommandCatalog.Delete && map.Resolve("DELETE", true) is null && map.Resolve("DELETE", textEntry: true) is null, "Delete repeated or intercepted editing");
        var old = defaults.Where(b => b.CommandId != CommandCatalog.Delete).Concat(new[] { new ShortcutBinding(CommandCatalog.FocusLeft, "W"), new ShortcutBinding(CommandCatalog.FocusRight, "S") }).ToList();
        var migrated = Load(root, "factory", old); Check(migrated.Bindings.Count(b => b.CommandId == CommandCatalog.FocusLeft) == 1 && migrated.Bindings.Any(b => b.CommandId == CommandCatalog.Delete && b.Gesture == "DELETE"), "Factory focus/Delete migration failed");
        var custom = new List<ShortcutBinding> { new(CommandCatalog.FocusLeft, "W"), new(CommandCatalog.FocusLeft, "A"), new(CommandCatalog.Fit, "DELETE") };
        var retained = Load(root, "custom", custom); Check(retained.Bindings.SequenceEqual(custom), "Custom bindings overwritten or Delete collision introduced");
        var cleared = Load(root, "cleared", [new(CommandCatalog.Delete, "")]); Check(cleared.Bindings.Count == 1 && cleared.Bindings[0].Gesture == "", "Explicitly cleared Delete overwritten");
        var row = new LightView.App.ViewModels.ShortcutRow(new(CommandCatalog.FocusLeft, "W")) { IsAlternate = true }; Check(row.Name.Contains("备用"), "Alternate row indistinguishable");
    }
    private static byte[] Pixels(BitmapSource source)
    { var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0); byte[] bytes = new byte[source.PixelWidth * source.PixelHeight * 4]; converted.CopyPixels(bytes, source.PixelWidth * 4, 0); return bytes; }
    private static void Webp(string root)
    {
        string fixture = Path.GetFullPath("work/infinity-v1/fixtures"); var loader = new WicImageLoader();
        var alpha = loader.Load(Path.Combine(fixture, "alpha.webp"), default); var png = loader.Load(Path.Combine(fixture, "alpha.png"), default);
        Check(Pixels(alpha.Source).SequenceEqual(Pixels(png.Source)), "Lossless alpha differs from PNG pipeline");
        Check(ReferenceEquals(alpha.Source, loader.Load(Path.Combine(fixture, "alpha.webp"), default).Source), "WebP full cache did not hit");
        byte[] tagged = File.ReadAllBytes(Path.Combine(fixture, "alpha.webp")); byte[] profile = DisplayColorTarget.Standard.Profile;
        using (var output = new MemoryStream())
        {
            bool extended = tagged.AsSpan(12, 4).SequenceEqual("VP8X"u8);
            output.Write(tagged, 0, extended ? 30 : 12);
            if (!extended) { output.Write("VP8X"u8); output.Write(BitConverter.GetBytes(10)); output.Write(new byte[] { 0x30, 0, 0, 0, 7, 0, 0, 3, 0, 0 }); }
            output.Write("ICCP"u8); byte[] length = BitConverter.GetBytes(profile.Length); output.Write(length, 0, 4); output.Write(profile, 0, profile.Length);
            if ((profile.Length & 1) != 0) output.WriteByte(0); output.Write(tagged, extended ? 30 : 12, tagged.Length - (extended ? 30 : 12));
            tagged = output.ToArray(); tagged[20] |= 0x20; BitConverter.GetBytes(tagged.Length - 8).CopyTo(tagged, 4);
        }
        string taggedPath = Path.Combine(root, "valid-icc.webp"); File.WriteAllBytes(taggedPath, tagged);
        var taggedImage = loader.Load(taggedPath, default); Check(taggedImage.EmbeddedProfileCount == 1 && taggedImage.ColorDescription!.Contains("嵌入 ICC") && Pixels(taggedImage.Source).SequenceEqual(Pixels(alpha.Source)), "Valid WebP ICC lost or altered pixels");
        var rotated = loader.Load(Path.Combine(fixture, "orientation.webp"), default); Check(rotated.PixelWidth == 4 && rotated.PixelHeight == 8, "EXIF orientation ignored");
        var animation = loader.Load(Path.Combine(fixture, "animated.webp"), default); var first = Pixels(animation.Source);
        Check(animation.DecoderName.Contains("首帧") && first[2] == 220 && first[1] == 60 && first[0] == 30 && first[3] == 255, "Animated first canvas incorrect");
        Check(loader.Load(Path.Combine(fixture, "lossy.webp"), default).PixelWidth == 8 && ImageFormats.IsSupported("中文 名称.WEBP") && ImageFormats.DialogFilter.Contains("*.webp"), "Lossy/WebP format routing failed");
        bool refused = false; try { loader.Load(Path.Combine(fixture, "bad-icc.webp"), default); } catch (NotSupportedException) { refused = true; }
        Check(refused, "Invalid ICC silently treated as sRGB");
        byte[] raw = File.ReadAllBytes(Path.Combine(fixture, "alpha.webp")); raw[4] ^= 4;
        refused = false; try { WebpDecoder.Inspect(raw); } catch (FileFormatException) { refused = true; } Check(refused, "Malformed RIFF accepted");
        File.WriteAllText(Path.Combine(root, "webp-result.json"), JsonSerializer.Serialize(new { alpha.PixelWidth, alpha.PixelHeight, alpha.ColorDescription, alpha.DecoderName, orientation = new { rotated.PixelWidth, rotated.PixelHeight }, animationDecoder = animation.DecoderName }, Json));
    }
    private static void Registration(string root)
    {
        string prefix = @"Software\InfinityView.Tests\" + Guid.NewGuid().ToString("N"); var store = new WindowsRegistrationStore(prefix); string data = Path.Combine(root, "registration");
        string oldExe = Path.Combine(root, "安装 空格", "LightView", "LightView.exe"), newExe = Path.Combine(root, "安装 空格", "InfinityView", "InfinityView.exe");
        try
        {
            Directory.CreateDirectory(data);
            var legacyOwner = new RegistrationReceipt { Executable = oldExe, AttemptId = Guid.NewGuid().ToString("N"), Registered = true };
            foreach (var entry in AppRegistration.Plan(oldExe).Where(e => e.Name != ".webp" && !e.Key.Contains(".webp")))
            {
                var applied = RegistryDatum.Text(entry.Value.Replace("InfinityView", "LightView")); store.Write(entry.Key, entry.Name, applied);
                legacyOwner.Changes.Add(new(entry.Key, entry.Name, null, applied));
            }
            File.WriteAllText(Path.Combine(data, "registration-owner.json"), JsonSerializer.Serialize(legacyOwner, Json));
            var current = new AppRegistration(store, newExe, data, false); current.Register();
            Check(store.Read(@"Software\Classes\Applications\LightView.exe", "FriendlyAppName")?.Data.GetString() == "InfinityView", "Friendly app name not updated");
            Check(store.Read(@"Software\Classes\" + AppRegistration.ProgId + @"\shell\open\command", "")?.Data.GetString() == "\"" + newExe + "\" \"%1\"", "Upgrade shell handler points to old executable");
            Check(AppRegistration.Extensions.Contains(".webp") && store.Read(AppRegistration.CapabilityPath + @"\FileAssociations", ".webp") is not null, "WebP registration absent");
            bool refused = false; try { new AppRegistration(store, Path.Combine(root, "other", "InfinityView.exe"), data, false).Register(); } catch (IOException) { refused = true; } Check(refused, "Other location stole registration ownership");
            Check(AppRegistration.IsDirectoryBrandRename(oldExe, newExe), "Sibling brand rename refused");
            Check(!AppRegistration.IsDirectoryBrandRename(oldExe, Path.Combine(root, "different parent", "InfinityView", "InfinityView.exe")), "Cross-parent migration accepted");
            Check(!AppRegistration.IsDirectoryBrandRename(newExe, oldExe), "Reverse brand migration accepted");
            current.Unregister(); Check(store.Read(@"Software\RegisteredApplications", "LightView") is null, "Unregister left active capability");
            File.WriteAllText(Path.Combine(root, "registration-plan.json"), JsonSerializer.Serialize(AppRegistration.Plan(newExe), Json));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(prefix, false); }
    }
    private static void Paths()
    {
        string? modern = Environment.GetEnvironmentVariable("INFINITYVIEW_DATA_DIR"), legacy = Environment.GetEnvironmentVariable("LIGHTVIEW_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("INFINITYVIEW_DATA_DIR", null); Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", null);
            Check(AppPaths.UserData == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfinityView"), "Default data path not renamed");
            Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", Path.Combine(Path.GetTempPath(), "legacy-explicit-data"));
            Check(AppPaths.UserData.EndsWith("legacy-explicit-data"), "Legacy explicit override lost");
            Environment.SetEnvironmentVariable("INFINITYVIEW_DATA_DIR", Path.Combine(Path.GetTempPath(), "modern-explicit-data"));
            Check(AppPaths.UserData.EndsWith("modern-explicit-data"), "New explicit override does not take precedence");
        }
        finally { Environment.SetEnvironmentVariable("INFINITYVIEW_DATA_DIR", modern); Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR", legacy); }
    }
    private static void Recycle(string root)
    {
        string path = Path.Combine(root, "回收 人工样本.txt"); File.WriteAllText(path, "InfinityView artificial recycle fixture");
        FileSnapshot version; using (var input = File.OpenRead(path)) version = FileSnapshot.Capture(path, input);
        File.AppendAllText(path, "modified"); bool refused = false;
        try { RecycleService.RecycleAsync(version, IntPtr.Zero).GetAwaiter().GetResult(); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(path), "Changed file was recycled");
        using (var input = File.OpenRead(path)) version = FileSnapshot.Capture(path, input);
        string recycled = RecycleService.RecycleAsync(version, IntPtr.Zero).GetAwaiter().GetResult();
        Check(!File.Exists(path) && File.Exists(recycled), "File did not reach real recycle bin");
        Check(File.ReadAllText(recycled) == "InfinityView artificial recycle fixturemodified", "Recycled bytes differ");
        File.WriteAllText(Path.Combine(root, "recycle-result.json"), JsonSerializer.Serialize(new { original = path, recycled, version.Length, unchangedBytes = true }, Json));
    }
    private static void Wait(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("Controlled pane operation timed out");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void DeletePane(string root)
    {
        string folder = Path.Combine(root, "delete-pane"); Directory.CreateDirectory(folder);
        string original = Path.Combine(folder, "a.png"), next = Path.Combine(folder, "b.png");
        File.Copy("work/infinity-v1/fixtures/alpha.png", original); File.Copy("work/infinity-v1/fixtures/alpha.png", next);
        var window = new MainWindow();
        try
        {
            var owner = Field<LightView.App.ViewModels.PaneContext>(window, "activePane"); Wait(owner.Viewer.SuspendPrefetchAsync()); Wait(owner.Viewer.OpenAsync(original));
            Wait((Task)Call(window, "ToggleComparisonAsync", owner)!); var session = Field<LightView.App.ViewModels.ComparisonSession>(window, "comparison");
            Wait(owner.Viewer.SuspendPrefetchAsync()); Wait(session.Left!.Viewer.SuspendPrefetchAsync());
            Wait((Task)Call(window, "ExecuteCommandAsync", CommandCatalog.Delete, null, 1d, owner)!);
            Check(!File.Exists(original) && File.Exists(next) && owner.Viewer.DisplayedImage?.FilePath == next, "Delete failed to advance captured pane");
            Check(session.Left.Viewer.IsDisplayedInvalid && session.Left.Viewer.OrganizingImage is null && session.Left.Viewer.DisplayedImage?.FilePath == original, "Delete navigated peer or left its source organizable");
            Check(ReferenceEquals(owner, Field<LightView.App.ViewModels.PaneContext>(window, "activePane")), "Delete switched focus");
        }
        finally { window.Close(); Wait(Field<Task>(window, "histogramWorker")); }
    }
}

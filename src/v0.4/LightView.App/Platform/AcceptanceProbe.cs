using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LightView.App.Views;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace LightView.App.Platform;

// Explicit, local-only acceptance instrumentation. Never enabled by normal startup.
internal static class AcceptanceProbe
{
    public static void Attach(MainWindow window)
    {
        string? output=Environment.GetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_OUTPUT");
        string? isolated=Environment.GetEnvironmentVariable("LIGHTVIEW_DATA_DIR");
        if(FrameworkApi.IsNullOrEmpty(output)||FrameworkApi.IsNullOrEmpty(isolated)||
            Environment.GetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_EXIT")!="1") return;
        try
        {
            output=Path.GetFullPath(output);
            if(!output.StartsWith(FrameworkApi.TrimEndingDirectorySeparator(Path.GetFullPath(isolated))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return;
        }
        catch(ArgumentException){return;}
        var viewport=(ImageViewport)window.FindName("Viewport");
        int frames=0;bool finishing=false; double? previewMs=null;
        EventHandler handler=null!;
        handler=async (_,_)=>
        {
            if(finishing||viewport.Source is null)return;
            if(viewport.Image?.IsPreview==true)
            {
                previewMs ??= (DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
                frames=0;return;
            }
            if(++frames<3)return;
            finishing=true;CompositionTarget.Rendering-=handler;
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            int compositor=DwmFlush();
            double startup=(DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
            if (Environment.GetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_PIXELS") == "1")
                CapturePixels(window, viewport, output + ".capture");
            var intervals=new List<double>();
            bool stress=Environment.GetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_STRESS")=="1";
            if(stress)
            {
                ((FrameworkElement)window.FindName("DetailsPanel")).Visibility=Visibility.Visible;
                var watch=Stopwatch.StartNew();double last=watch.Elapsed.TotalMilliseconds;
                for(int i=0;i<300;i++)
                {
                    viewport.ZoomAt(new Point(viewport.ActualWidth*.7,viewport.ActualHeight*.45),i%2==0?1.025:1/1.025);
                    viewport.Transform.Pan(i%2==0?4:-4,2);viewport.InvalidateVisual();
                    await Task.Delay(16);
                    double time=watch.Elapsed.TotalMilliseconds;intervals.Add(time-last);last=time;
                }
                viewport.Fit();
            }
            int idleMs = int.TryParse(Environment.GetEnvironmentVariable("LIGHTVIEW_ACCEPTANCE_IDLE_MS"), out int requestedIdle)
                ? FrameworkApi.Clamp(requestedIdle, 0, 5000) : 0;
            double idleStartedAtMs=(DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
            if(idleMs>0) await Task.Delay(idleMs);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output,JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,
                    executable=FrameworkApi.ProcessPath,
                    version=typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                    method="Fresh shipping executable process to three WPF composition frames after image binding plus DwmFlush. OS disk cache not flushed. No physical photon measurement.",
                    startupMs=startup,previewFirstCompositionMs=previewMs,dwmFlushHresult=compositor,pixelWidth=viewport.Source.PixelWidth,pixelHeight=viewport.Source.PixelHeight,
                    dpi=VisualTreeHelper.GetDpi(viewport).DpiScaleX,stress,uiIntervalsMs=intervals,
                    acceptanceIdleMs=idleMs,idleStartedAtMs},new JsonSerializerOptions{WriteIndented=true}));
            }
            finally{window.Close();}
        };
        CompositionTarget.Rendering+=handler;
        window.Closed+=(_,_)=>CompositionTarget.Rendering-=handler;
    }
    private static void CapturePixels(MainWindow window, ImageViewport viewport, string directory)
    {
        // Reads the real full displayed Source; no substitute decoder/image is installed.
        var image = viewport.Image ?? throw new InvalidOperationException("No displayed image");
        if (image.IsPreview) throw new InvalidOperationException("Full image required");
        var source = viewport.Source ?? throw new InvalidOperationException("No displayed Source");
        int width = source.PixelWidth, height = source.PixelHeight;
        if ((long)width * height > 4_000_000) throw new InvalidOperationException("Pixel diagnostics limited to small synthetic fixtures");
        Directory.CreateDirectory(directory);
        var pixels = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var buffer = new byte[checked(width * height * 4)]; pixels.CopyPixels(buffer, width * 4, 0);
        File.WriteAllBytes(Path.Combine(directory, "source.bgra"), buffer);
        var view = new ImageViewport { Source = source, Width = width, Height = height, Background = Brushes.Transparent };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.NearestNeighbor);
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); render.Render(view);
        render.CopyPixels(buffer, width * 4, 0); File.WriteAllBytes(Path.Combine(directory, "render.bgra"), buffer);
        var target = DisplayColorProbe.TargetForWindow(new WindowInteropHelper(window).Handle);
        File.WriteAllBytes(Path.Combine(directory, "target.icc"), target.Profile);
        using var process = Process.GetCurrentProcess();
        var native = process.Modules.Cast<ProcessModule>().Where(module => module.ModuleName.Equals("lcms2.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module => new { path = module.FileName, module.FileVersionInfo.FileVersion }).ToArray();
        File.WriteAllText(Path.Combine(directory, "capture.json"), JsonSerializer.Serialize(new
        {
            executable = FrameworkApi.ProcessPath, image.FilePath, image.IsPreview, width, height, image.ColorDescription,
            targetIsStandard = target.IsStandardRgb, target.Description, target.Key,
            lcmsModules = native,
            managedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic)
                .Select(assembly => new { assembly.FullName, assembly.Location }).ToArray(),
            method = "Real EXE displayed Source and same shipping ImageViewport rendered offscreen at native pixels; independent external references decide pass. Diagnostic only, excluded from performance."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [DllImport("dwmapi.dll")]private static extern int DwmFlush();
}

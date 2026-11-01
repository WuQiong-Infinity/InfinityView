using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Input;
using LightView.App.Persistence;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.App.Imaging;
using LightView.Core.Commands;
using LightView.Core.Settings;
using LightView.Core.Viewing;

namespace LightView.Tests;
internal static class PointerRedesignTests
{
    private static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
    private static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(o,args);
    private static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
    private static void Wait(Task task)
    {
        if(!task.IsCompleted){var frame=new System.Windows.Threading.DispatcherFrame();_=task.ContinueWith(_=>System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.FromCurrentSynchronizationContext());System.Windows.Threading.Dispatcher.PushFrame(frame);}
        task.GetAwaiter().GetResult();
    }
    internal static int Run(string root,string? filter=null)
    {
        Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR",Path.Combine(root,"UserData"));
        var app=new LightView.App.App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases=new(string Name,Action Run)[]{("half click boundaries and movement cancel while edge mode remains available",Gesture),("new defaults and mouse settings clone export import apply and page reset preserve custom keys",()=>Settings(root)),("inactive pane first click changes focus only and second click can reach navigation",()=>Focus(root))};int fail=0;
        if(filter is not null)cases=cases.Where(c=>c.Name.Contains(filter)).ToArray();
        foreach(var c in cases)try{c.Run();Console.WriteLine("PASS "+c.Name);}catch(Exception e){fail++;Console.WriteLine("FAIL "+c.Name+": "+e);}
        Console.WriteLine($"RESULT {cases.Length-fail}/{cases.Length} pointer checks passed");return fail==0?0:1;
    }
    private static void Gesture()
    {
        var p=new CanvasPointerGesture();
        foreach(var sample in new[]{(10d,-1),(190d,-1),(200d,1),(390d,1)}){p.Begin(sample.Item1,30);Check(p.Release(sample.Item1,30,400,200,4,4,true)==sample.Item2,"Half navigation used edge bands or wrong midline");}
        p.Begin(100,30);p.Move(130,35,4,4);Check(p.Release(130,35,400,200,4,4,true)==0,"Movement was treated as a click");
        p.Begin(100,30);p.Cancel();Check(p.Release(100,30,400,200,4,4,true)==0,"Cancelled capture navigated");
        p.Begin(100,30);Check(p.Release(100,30,400,200,4,4)==0,"Classic edge fallback changed");
        p.Begin(10,30);Check(p.Release(-5,30,400,200,4,4,true)==0,"Outside release navigated");
    }
    private static void Settings(string root)
    {
        var fresh=new AppSettings();var keys=new ShortcutMap(fresh.Bindings,fresh.Presets);
        Check(keys.Resolve("A")?.Id==CommandCatalog.FocusLeft&&keys.Resolve("D")?.Id==CommandCatalog.FocusRight&&keys.Resolve("LEFT")?.Id==CommandCatalog.Previous&&keys.Resolve("RIGHT")?.Id==CommandCatalog.Next,"AD/arrows default ownership wrong");
        Check(keys.Resolve("SPACE")is null&&keys.Resolve("TAB")?.Id==CommandCatalog.Details&&keys.Resolve("MOUSEMIDDLE")?.Id==CommandCatalog.FullScreen,"Freed space or reserved Tab/middle changed");
        Check(!InputRouter.IsSupported("MOUSELEFT")&&!InputRouter.IsSupported("MOUSERIGHT"),"Drag buttons exposed as instant commands");
        var custom=new AppSettings{MouseNavigationRegion="edges",MousePanButton="left",Bindings=[new(CommandCatalog.Next,"A"),new(CommandCatalog.Details,"TAB")]};
        Check(custom.Clone().Bindings.Single(b=>b.Gesture=="A").CommandId==CommandCatalog.Next&&custom.Clone().MousePanButton=="left","Custom keys or gesture settings overwritten by clone");
        string path=Path.Combine(root,"mouse-settings.json");SettingsTransfer.Export(path,custom,false,Path.Combine(root,"active.json"));var imported=SettingsTransfer.Read(path);
        var merged=SettingsTransfer.Merge(fresh,imported,SettingsGroups.Viewing);Check(merged.MousePanButton=="left"&&merged.MouseNavigationRegion=="edges"&&merged.Bindings.First(b=>b.Gesture=="A").CommandId==CommandCatalog.FocusLeft,"Viewing import altered keys or missed pointer fields");
        var untouched=SettingsTransfer.Merge(fresh,imported,SettingsGroups.Shortcuts);Check(untouched.MousePanButton=="right"&&untouched.Bindings.First(b=>b.Gesture=="A").CommandId==CommandCatalog.Next,"Shortcut import crossed viewing group");
        var invalid=custom.Clone();invalid.MousePanButton="middle";Check(SettingsValidation.Error(invalid)is not null,"Unsupported pan button accepted");
        AppSettings? applied=null;var window=new SettingsWindow(custom,s=>{applied=s;return Task.FromResult<string?>(null);},null,false);
        try
        {
            Check(((ComboBox)window.FindName("MousePanButtonBox")).SelectedIndex==1&&((ComboBox)window.FindName("MouseNavigationRegionBox")).SelectedIndex==1,"Existing mouse choices not loaded");
            ((TabItem)window.FindName("ViewingTab")).IsSelected=true;Call(window,"OnResetPageClick",window,new RoutedEventArgs());Call(window,"OnApplyClick",window,new RoutedEventArgs());
            Check(applied?.MousePanButton=="right"&&applied.MouseNavigationRegion=="halves"&&applied.Bindings.Single(b=>b.Gesture=="A").CommandId==CommandCatalog.Next,"Page reset/apply did not preserve custom bindings");
        }finally{window.Close();}
    }
    private static void Focus(string root)
    {
        var window=new MainWindow();try
        {
            var owner=Field<PaneContext>(window,"activePane");string dir=Path.GetFullPath(Path.Combine(root,"focus"));Directory.CreateDirectory(dir);string path=Path.Combine(dir,"A.jpg");File.WriteAllBytes(path,[1]);
            var source=BitmapSource.Create(80,60,96,96,PixelFormats.Pbgra32,null,new byte[80*60*4],320);source.Freeze();
            Wait(owner.Viewer.SeedAsync(new(path,source,"Synthetic",0,FileVersion:new(path,"id",1,1,"hash"),DisplayTargetKey:DisplayColorTarget.Standard.Key)));Wait((Task)Call(window,"ToggleComparisonAsync",owner)!);
            var session=Field<ComparisonSession>(window,"comparison");var left=session.Left!;int version=left.Viewer.NavigationVersion;
            var first=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent};left.Viewport.RaiseEvent(first);
            Check(first.Handled&&ReferenceEquals(Field<PaneContext>(window,"activePane"),left)&&left.Viewer.NavigationVersion==version,"First inactive click navigated or did not focus");
            var second=new MouseButtonEventArgs(Mouse.PrimaryDevice,1,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent};left.Viewport.RaiseEvent(second);Check(!second.Handled,"Second active click was swallowed");
            Check(left.Viewport.MousePanButton=="right"&&owner.Viewport.MouseNavigationRegion=="halves","Pointer settings were not applied to both panes");
        }finally{window.Close();}
    }
}

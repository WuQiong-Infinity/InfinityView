using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Organizing;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Viewing;

namespace LightView.Tests;

internal static class SplitViewTests
{
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    private static void Near(double a,double b) => Check(Math.Abs(a-b)<.000001,$"Expected {b}, got {a}");
    private static object? Call(object instance,string name,params object?[] arguments) => instance.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(instance,arguments);
    private static T Field<T>(object instance,string name) => (T)instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Wait(Task task)
    {
        if(!task.IsCompleted)
        {
            var frame=new System.Windows.Threading.DispatcherFrame();
            _=task.ContinueWith(_=>System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    internal static int Run(string root, bool layoutOnly = false)
    {
        Directory.CreateDirectory(root);string? previous=Environment.GetEnvironmentVariable("LIGHTVIEW_DATA_DIR");Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR",Path.Combine(root,"UserData"));
        var app=new LightView.App.App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();
        // Explicit dispatcher context supports deterministic async UI tests; no HWND is shown.
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases=new (string Name,Action Test)[]
        {
            ("split resize freezes Fit and preserves Zoom plus each pane center image coordinate",Resize),
            ("peer move and late old result stay invalid until explicit fresh reload",()=>Peer(root)),
            ("awaiting full image and hovered-pane zoom retain captured owner across focus change",()=>FocusAwait(root)),
            ("budget recovery cannot discard a new active pane or new split session",()=>BudgetOwner(root)),
            ("layout keeps filenames and independent right details inside pane and hides empty error",()=>Layout(root)),
            ("linked gestures follow both ways while focus and layout do not follow",()=>Link(root)),
            ("exit keeps selected left viewer and retires the other request owner",()=>Exit(root))
        };
        if (layoutOnly) cases = cases.Where(c => c.Name.StartsWith("layout", StringComparison.Ordinal)).ToArray();
        int failed=0;foreach(var item in cases)try{item.Test();Console.WriteLine("PASS "+item.Name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+item.Name+": "+error);}
        Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR",previous);
        File.WriteAllText(Path.Combine(root,"result.txt"),$"{cases.Length-failed}/{cases.Length}; hidden construction/layout and artificial loaders only; no desktop acceptance");Console.WriteLine($"RESULT {cases.Length-failed}/{cases.Length} split checks passed");return failed==0?0:1;
    }
    private static LoadedImage Image(string path,FileSnapshot? version=null)
    {
        var pixels=BitmapSource.Create(80,60,96,96,PixelFormats.Bgr24,null,new byte[80*60*3],240);pixels.Freeze();
        return new(path,pixels,"Synthetic",0,FileVersion:version??new(path,"identity",100,1,"hash"),OriginalWidth:1600,OriginalHeight:1200,DisplayTargetKey:DisplayColorTarget.Standard.Key);
    }
    private sealed class Loader:IImageLoader
    {
        public Func<string,CancellationToken,LoadedImage> Read= (p,_)=>Image(p);
        public LoadedImage Load(string p,CancellationToken t)=>Read(p,t);
    }
    private static (MainWindow Window,PaneContext Right,PaneContext Left,Loader Loader) Window(string root)
    {
        var w=new MainWindow();var initial=Field<PaneContext>(w,"activePane");initial.Attached=false;initial.Viewer.Detach();
        var loader=new Loader();var model=new ViewerViewModel(loader);var right=(PaneContext)Call(w,"CreatePane",model,w.FindName("Viewport"))!;Call(w,"ActivatePane",right);
        Wait(model.SeedAsync(Image(Path.Combine(root,"seed.jpg"))));Wait((Task)Call(w,"ToggleComparisonAsync",right)!);
        var session=Field<ComparisonSession>(w,"comparison");Check(session.State==ComparisonState.Active,"Split entry failed");
        var visual=(FrameworkElement)w.Content;visual.DataContext=w.DataContext;w.Content=null;visual.Measure(new Size(960,640));visual.Arrange(new Rect(0,0,960,640));visual.UpdateLayout();
        foreach(var p in new[]{right,session.Left!})p.Viewport.RefreshGeometry();
        return(w,right,session.Left!,loader);
    }
    private static (double X,double Y) Center(ViewportTransform t)=>((t.ViewWidth/2-t.OffsetX)/t.DipPerPixel,(t.ViewHeight/2-t.OffsetY)/t.DipPerPixel);
    private static void Resize()
    {
        foreach(double dpi in new[]{1d,1.5,2d})
        {
            var left=new ViewportTransform();left.Resize(460,400,dpi);left.SetImage(2400,1600);var right=new ViewportTransform();right.Resize(460,400,dpi);right.SetImage(1600,2400);right.ActualPixels();right.Pan(73,-81);
            double lz=left.Zoom,rz=right.Zoom;var lc=Center(left);var rc=Center(right);left.FreezeFit();right.FreezeFit();left.Resize(600,400,dpi);right.Resize(320,400,dpi);left.Resize(610,400,dpi);right.Resize(310,400,dpi);
            Near(left.Zoom,lz);Near(right.Zoom,rz);Near(Center(left).X,lc.X);Near(Center(left).Y,lc.Y);Near(Center(right).X,rc.X);Near(Center(right).Y,rc.Y);Check(!left.IsFit,"Delayed layout refit frozen Fit state");
        }
    }
    private static void Peer(string root)
    {
        string dir=Path.Combine(root,"peer");Directory.CreateDirectory(dir);string path=Path.Combine(dir,"source.png");File.WriteAllBytes(path,[1,2,3,4]);FileSnapshot version;using(var f=File.OpenRead(path))version=FileSnapshot.Capture(path,f);
        var loader=new Loader{Read=(p,_)=>Image(p,version)};var peer=new ViewerViewModel(loader);Wait(peer.SeedAsync(Image(path,version)));
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();loader.Read=(p,_)=>{entered.Set();release.Wait();return Image(p,version);};Task pending=peer.OpenAsync(path);Check(entered.Wait(5000),"Old peer load not waiting");
        string target=Path.Combine(dir,"moved");Directory.CreateDirectory(target);string logs=Path.Combine(dir,"journal");using var lease=OperationJournal.AcquireSession(logs);var service=new SafeCopyService(new OperationJournal(logs));var moving=service.MoveAsync(new(version,target,"人工"));Wait(moving);var moved=moving.Result;Check(moved.State=="Completed","Actual artificial move failed");peer.InvalidateVersion(version);release.Set();Wait(pending);Check(peer.OrganizingImage is null && peer.IsDisplayedInvalid,"Late old result restored organizing or lost invalid marker");
        var undoing=service.UndoAsync();Wait(undoing);var undo=undoing.Result;Check(undo.State=="Completed"&&File.Exists(path),"Artificial undo failed");loader.Read=(p,_)=>Image(p,version);Wait(peer.OpenAsync(path));Check(peer.OrganizingImage is not null,"Explicit fresh reload stayed invalid");peer.Detach();
    }
    private static void FocusAwait(string root)
    {
        var t=Window(root);try
        {
            Field<ComparisonSession>(t.Window,"comparison").SetLinked(false);t.Right.Viewport.Fit();t.Left.Viewport.Fit();double left=t.Left.Viewport.Transform.Zoom;
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();t.Loader.Read=(p,_)=>{entered.Set();release.Wait();return Image(p);};Task open=t.Right.Viewer.OpenAsync(Path.Combine(root,"pending.jpg"));Check(entered.Wait(5000),"Load not started");
            Task command=(Task)Call(t.Window,"ExecuteCommandAsync",CommandCatalog.ActualPixels,null,1d,t.Right)!;Call(t.Window,"ActivatePane",t.Left);release.Set();Wait(open);Wait(command);Near(t.Right.Viewport.Transform.Zoom,1);Near(t.Left.Viewport.Transform.Zoom,left);Check(ReferenceEquals(Field<PaneContext>(t.Window,"activePane"),t.Left),"Await switched active side");
            Task hover=(Task)Call(t.Window,"ExecuteCommandAsync",CommandCatalog.ZoomIn,new Point(35,42),1d,t.Right)!;Wait(hover);Check(ReferenceEquals(Field<PaneContext>(t.Window,"activePane"),t.Left),"Hovered command changed keyboard active pane");
        }finally{t.Window.Close();}
    }
    private static void BudgetOwner(string root)
    {
        var t=Window(root);try{var recover=t.Right.Viewer.BudgetRecoveryFactory!();Call(t.Window,"ActivatePane",t.Left);Check(!recover()&&Field<ComparisonSession>(t.Window,"comparison").IsOn,"Old recovery discarded new active side");Call(t.Window,"ExitComparison",t.Left);Check(!recover(),"Old session recovery remained valid");}finally{t.Window.Close();}
    }
    private static void Layout(string root)
    {
        var t=Window(root);try
        {
            Call(t.Window,"ToggleDetails",t.Left);Call(t.Window,"ToggleDetails",t.Right);var grid=(Grid)t.Window.FindName("ComparisonGrid");grid.Measure(new Size(640,420));grid.Arrange(new Rect(0,0,640,420));grid.UpdateLayout();t.Left.Viewport.RefreshGeometry();t.Right.Viewport.RefreshGeometry();
            var error=(TextBlock)t.Window.FindName("ComparisonError");var details=(Border)t.Window.FindName("DetailsPanel");Check(error.Visibility==Visibility.Collapsed,"Empty error has background bar");Check(details.HorizontalAlignment==HorizontalAlignment.Right&&details.MaxWidth<=t.Right.Viewport.ActualWidth-23.9,"Right details overflow pane");Check(DetailRowGrid.GetValueFirst(details),"Right details column order incorrect");
            var rows = Descendants(details).OfType<DetailRowGrid>().ToArray();
            Check(rows.Length>0&&rows.All(row=>row.Children.Count==2&&Grid.GetColumn(row.Children[1])==0&&row.Children[1] is OutlinedTextBlock {TextAlignment:TextAlignment.Right}),"Actual shared detail values did not align right in left column");
            foreach(string name in new[]{"ReferenceName","CurrentComparisonName"}){var label=(OutlinedTextBlock)t.Window.FindName(name);Check(label.TextTrimming==TextTrimming.CharacterEllipsis&&label.Background is null,"Filename is heavy or unbounded");}
        }finally{t.Window.Close();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static void Link(string root)
    {
        var t=Window(root);try
        {
            t.Left.Viewport.ActualPixels();Near(t.Right.Viewport.Transform.Zoom,1);t.Right.Viewport.ZoomAt(new Point(70,60),1.3);Near(t.Left.Viewport.Transform.Zoom,1.3);
            double z=t.Right.Viewport.Transform.Zoom;t.Left.Viewport.Transform.FreezeFit();t.Left.Viewport.Transform.Resize(350,300,1);t.Left.Viewport.RefreshGeometry();Call(t.Window,"ActivatePane",t.Left);Near(t.Right.Viewport.Transform.Zoom,z);
        }finally{t.Window.Close();}
    }
    private static void Exit(string root)
    {
        var t=Window(root);try{var kept=t.Left.Viewer;Call(t.Window,"ActivatePane",t.Left);Call(t.Window,"ExitComparison",t.Left);Check(ReferenceEquals(Field<PaneContext>(t.Window,"activePane").Viewer,kept)&&!t.Right.Attached&&t.Right.Viewer.DisplayedImage is null,"Exit restored old right or retained retired owner");}finally{t.Window.Close();}
    }
}

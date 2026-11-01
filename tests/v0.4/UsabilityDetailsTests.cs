using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Persistence;
using LightView.App.Platform;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.App.Views.Settings;
using LightView.Core.Commands;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class UsabilityDetailsTests
{
    private static void Check(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
    private static object? Call(object instance,string name,params object?[] args)=>instance.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(instance,args);
    private static T Field<T>(object instance,string name)=>(T)instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Wait(Task task)
    {
        if(!task.IsCompleted){var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;var frame=new System.Windows.Threading.DispatcherFrame();_=task.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.Default);System.Windows.Threading.Dispatcher.PushFrame(frame);}task.GetAwaiter().GetResult();
    }
    internal static int Run(string root,string? filter=null)
    {
        Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR",Path.Combine(root,"UserData"));
        var app=new LightView.App.App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases=new (string Name,Action Test)[]
        {
            ("new defaults preserve old custom settings and text copy guards",()=>Defaults(root)),
            ("layout normalization preserves unknown IDs and all-off with bounded validation",LayoutValidation),
            ("projection uses stable IDs ordering and fixed warnings independent of copy",Projection),
            ("layout preview cancel failed save and readonly retain applied transaction",Transaction),
            ("appearance editor shows ordered groups fields and preserves unavailable IDs",Editor),
            ("appearance-only transfer includes layout while old packages remain compatible",()=>Transfer(root)),
            ("foreground grant rejects unverified PID and denial keeps request sequence",ForegroundGate),
            ("real pipe grants only verified same EXE PID before send and denial still ACKs",()=>Pipe(root)),
            ("W S only focus and strip; right EXIF columns exchange without mirroring",()=>PaneInput(root))
        };
        if(filter is not null)cases=cases.Where(c=>c.Name.Contains(filter)).ToArray();int failed=0;
        foreach(var item in cases)try{item.Test();Console.WriteLine("PASS "+item.Name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+item.Name+": "+error);}
        File.WriteAllText(Path.Combine(root,"result.txt"),$"{cases.Length-failed}/{cases.Length}; artificial metadata, hidden UI and verified local pipe only; actual foreground behavior pending CU");Console.WriteLine($"RESULT {cases.Length-failed}/{cases.Length} usability/details checks passed");return failed==0?0:1;
    }
    private static void Defaults(string root)
    {
        var map=new ShortcutMap(CommandCatalog.DefaultBindings());foreach(var pair in new[]{("MOUSEX1",CommandCatalog.ActualPixelsAtPointer),("MOUSEX2",CommandCatalog.Fit),("C",CommandCatalog.Comparison),("X",CommandCatalog.LinkComparison),("R",CommandCatalog.Reveal),("CTRL+C",CommandCatalog.CopyShootingInfo),("W",CommandCatalog.FocusLeft),("S",CommandCatalog.FocusRight)})Check(map.Resolve(pair.Item1)?.Id==pair.Item2,"Wrong default "+pair.Item1);
        Check(map.Resolve("CTRL+C",textEntry:true)is null&&map.Resolve("C",true)is null,"Editor copy/repeat was intercepted");
        string dir=Path.Combine(root,"custom");Directory.CreateDirectory(dir);string file=Path.Combine(dir,"settings.json");var old=new AppSettings{Bindings=[new(CommandCatalog.Open,"K"),new(CommandCatalog.Fit,"MOUSEX1")]};string text=JsonSerializer.Serialize(old,new JsonSerializerOptions(JsonSerializerDefaults.Web));File.WriteAllText(file,text);var loaded=new SettingsStore(dir).Load().Settings;Check(loaded.Bindings.Count==2&&loaded.Bindings[0].Gesture=="K"&&File.ReadAllText(file)==text,"Upgrade rewrote custom bindings");
    }
    private static void LayoutValidation()
    {
        Check(DetailsCatalog.Normalize(null).Groups.Count==4,"Null default missing");var layout=DetailsCatalog.Normalize(null);foreach(var g in layout.Groups){g.Enabled=false;foreach(var f in g.Fields)f.Enabled=false;}Check(DetailsCatalog.Normalize(layout).Groups.All(g=>!g.Enabled),"All-off replaced with defaults");
        layout=DetailsCatalog.Normalize(null);layout.Groups[0].Fields.Insert(0,new(){Id="future.field",Enabled=false});layout.Groups.Insert(0,new(){Id="future.group",Fields=[new(){Id="future.child"}]});var normalized=DetailsCatalog.Normalize(layout);Check(normalized.Groups[0].Id=="future.group"&&normalized.Groups[1].Fields[0].Id=="future.field","Unknown IDs lost/reordered");
        layout.Groups.Add(new(){Id="file"});Check(DetailsCatalog.Error(layout)is not null,"Duplicate group accepted");layout=DetailsCatalog.Normalize(null);layout.Groups[0].Fields.Add(new(){Id="file.name"});Check(DetailsCatalog.Error(layout)is not null,"Duplicate field accepted");layout=DetailsCatalog.Normalize(null);layout.Groups[0].Fields.Add(new(){Id="camera.model"});Check(DetailsCatalog.Error(layout)is not null,"Wrong-group field accepted");layout=new(){Groups=[new(){Id="BAD-ID"}]};Check(DetailsCatalog.Error(layout)is not null,"Invalid ID accepted");layout=new(){Groups=Enumerable.Range(0,17).Select(i=>new DetailGroupChoice{Id="future."+i}).ToList()};Check(DetailsCatalog.Error(layout)is not null,"Group bound missing");layout=new(){Groups=[new(){Id="future",Fields=Enumerable.Range(0,129).Select(i=>new DetailFieldChoice{Id="future."+i}).ToList()}]};Check(DetailsCatalog.Error(layout)is not null,"Field bound missing");
    }
    private static LoadedImage Picture(string path)
    {
        var bitmap=BitmapSource.Create(80,60,96,96,PixelFormats.Bgr24,null,new byte[80*60*3],240);bitmap.Freeze();
        return new(path,bitmap,"Synthetic",0,FileVersion:new(path,"id",100,DateTime.Now.ToFileTimeUtc(),"abc"),OriginalWidth:1600,OriginalHeight:1200,DisplayTargetKey:DisplayColorTarget.Standard.Key,
            Metadata:new(1600,1200,96,96,"Bgr24",[new("相机型号","DEMO","camera.model"),new("快门","1/100 s","exposure.shutter"),new("ISO","640","exposure.iso"),new("焦距","35 mm","exposure.focal"),new("等效35mm焦距","52 mm","exposure.equivalent")],"Required warning",new("Test_recipe","FLEXIBLE COLOR")));
    }
    private sealed class Loader(LoadedImage image):IImageLoader{public LoadedImage Load(string p,CancellationToken t)=>image;}
    private static void Projection()
    {
        var picture=Picture("C:/SENSITIVE-PATH/example.jpg");var model=new ViewerViewModel(new Loader(picture));Wait(model.OpenAsync(picture.FilePath));string original=ShootingInfoFormatter.Format(picture)!;var layout=DetailsCatalog.Normalize(null);layout.Groups.Reverse();var exposure=layout.Groups.First(g=>g.Id=="exposure");exposure.Fields.Reverse();exposure.Fields.First(f=>f.Id=="exposure.shutter").Enabled=false;model.SetDetailsLayout(layout);
        Check(model.DetailsSections.First().Id=="style"&&!model.DetailsRows.Any(r=>r.Id=="exposure.shutter"),"Layout ordering/filtering ignored");Check(model.DetailsRows.Count(r=>r.Id=="exposure.focal")==1,"Equivalent focal duplicate");Check(model.DetailsSections.Last().Title=="元数据提示"&&ShootingInfoFormatter.Format(picture)==original&&!original.Contains("SENSITIVE"),"Warning/copy affected by presentation");foreach(var group in layout.Groups)group.Enabled=false;model.SetDetailsLayout(layout);Check(model.DetailsSections.Count==1&&model.DetailsSections[0].Title=="元数据提示","All-off invented missing EXIF or hid warning");model.Detach();
        var raw=new ImageDetailSection("X",[new("Future","must not show","future.field")],"file");layout=DetailsCatalog.Normalize(null);layout.Groups[0].Fields.Add(new(){Id="future.field"});Check(DetailsProjection.Apply([raw],layout).Count==0,"Unavailable ID executed as real field");
    }
    private static void Transaction()
    {
        AppearanceSettings? seen=null;var session=new SettingsSession(new AppSettings(),p=>seen=p,false);session.Draft.Appearance.DetailsLayout=DetailsCatalog.Normalize(null);session.Draft.Appearance.DetailsLayout.Groups[0].Enabled=false;session.PreviewAppearance();Check(seen?.DetailsLayout?.Groups[0].Enabled==false,"Preview absent");Check(session.TryClose()&&seen?.DetailsLayout is null,"Cancel kept preview");session.PreviewAppearance();Wait(session.ApplyAsync(session.Draft,_=>Task.FromResult<string?>("denied")));Check(seen?.DetailsLayout is null&&session.LastAppliedSnapshot.Appearance.DetailsLayout is null,"Failed save committed preview");var ro=new SettingsSession(new AppSettings(),p=>seen=p,true);ro.Draft.Appearance.DetailsLayout=DetailsCatalog.Normalize(null);ro.PreviewAppearance();Check(ro.IsReadOnly,"Readonly state absent");
    }
    private static void Editor()
    {
        var page=new AppearancePage();var appearance=new AppearanceSettings{DetailsLayout=DetailsCatalog.Normalize(null)};appearance.DetailsLayout.Groups[0].Fields.Add(new(){Id="future.hidden",Enabled=false});page.Load(appearance);AppearanceSettings? edited=null;page.Changed+=p=>edited=p;
        var groups=(ListBox)page.FindName("DetailGroupsList");var first=groups.Items[0];first.GetType().GetProperty("Enabled")!.SetValue(first,false);Check(edited?.DetailsLayout?.Groups[0].Enabled==false&&edited.DetailsLayout.Groups[0].Fields.Any(f=>f.Id=="future.hidden"),"Editor dropped unknown or did not preview");Call(page,"OnMoveDetailGroup",new Button{Tag="down"},new RoutedEventArgs());Check(edited?.DetailsLayout?.Groups[1].Id=="file","Group reorder absent");page.SetReadOnly(true);Check(!((Expander)page.FindName("DetailsContentExpander")).IsEnabled,"Readonly editor editable");
    }
    private static void Transfer(string root)
    {
        var value=new AppSettings();value.Appearance.DetailsLayout=DetailsCatalog.Normalize(null);value.Appearance.DetailsLayout.Groups[0].Enabled=false;value.Appearance.DetailsLayout.Groups[0].Fields.Add(new(){Id="future.item"});string path=Path.Combine(root,"layout-package.json");SettingsTransfer.Export(path,value,false,Path.Combine(root,"not-active.json"));var incoming=SettingsTransfer.Read(path);Check(SettingsTransfer.Merge(new AppSettings(),incoming,SettingsGroups.Appearance).Appearance.DetailsLayout?.Groups[0].Enabled==false,"Appearance import missing layout");Check(SettingsTransfer.Merge(new AppSettings(),incoming,SettingsGroups.Viewing).Appearance.DetailsLayout is null,"Viewing changed appearance");Check(incoming.Settings.Appearance.DetailsLayout!.Groups[0].Fields.Any(f=>f.Id=="future.item"),"Export dropped unknown ID");
        var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;node["settings"]!["appearance"]!.AsObject().Remove("detailsLayout");string older=Path.Combine(root,"older-package.json");File.WriteAllText(older,node.ToJsonString());Check(SettingsTransfer.Read(older).Settings.Appearance.DetailsLayout is null,"Old package rejected or changed defaults");
    }
    private static void ForegroundGate()
    {
        int calls=0;bool Grant(uint id){calls++;Check(id==123,"Wrong PID");return false;}
        Check(!ForegroundActivation.Grant(false,"exe",123,Grant)&&!ForegroundActivation.Grant(true,null,123,Grant)&&!ForegroundActivation.Grant(true,"exe",null,Grant)&&calls==0,"Unverified grant reached native route");Check(!ForegroundActivation.Grant(true,"exe",123,Grant)&&calls==1,"Denied grant not propagated safely");var order=new List<string>();bool result=ForegroundActivation.Request(()=>order.Add("restore"),()=>order.Add("show"),()=>order.Add("activate"),()=>{order.Add("handle");return new IntPtr(1);},_=>{order.Add("foreground");return false;});order.Add("decode");Check(!result&&string.Join(",",order)=="restore,show,activate,handle,foreground,decode","Denied foreground reordered or blocked reading");
    }
    private static void Pipe(string root)
    {
        string directory=Path.Combine(root,"pipe");using var server=new SingleInstanceSession(directory);Check(server.IsPrimary,"No server");bool granted=false;string? received=null;int grants=0;server.Listen(path=>{Check(granted,"Payload sent before verified grant");received=path;return Task.CompletedTask;});
        Task<bool> Send(string? expected)=>Task.Run(async()=>{using var client=new SingleInstanceSession(directory,pid=>{Check(pid==(uint)Process.GetCurrentProcess().Id,"Not actual verified server PID");granted=true;Interlocked.Increment(ref grants);return false;});Check(!client.IsPrimary,"Client unexpectedly primary");return await client.ForwardAsync("人工 request.jpg",expected);});
        var wrong=Send(Path.Combine(root,"wrong.exe"));Wait(wrong);Check(!wrong.Result&&grants==0&&received is null,"Cross-copy verification weakened");var correct=Send(FrameworkApi.ProcessPath);Wait(correct);Check(correct.Result&&received=="人工 request.jpg"&&grants==1,"Denied foreground prevented ACK/read");
    }
    private static void PaneInput(string root)
    {
        var window=new MainWindow();try
        {
            var right=Field<PaneContext>(window,"activePane");Wait(right.Viewer.SeedAsync(Picture(Path.Combine(root,"pane.jpg"))));Wait((Task)Call(window,"ToggleComparisonAsync",right)!);var split=Field<ComparisonSession>(window,"comparison");var left=split.Left!;double zoom=right.Viewport.Transform.Zoom;
            Wait((Task)Call(window,"ExecuteCommandAsync",CommandCatalog.FocusLeft,null,1d,right)!);Check(ReferenceEquals(Field<PaneContext>(window,"activePane"),left)&&right.Viewport.Transform.Zoom==zoom,"W focused and zoomed/linked");var strip=(Border)window.FindName("SplitFocusStrip");Check(strip.Visibility==Visibility.Visible&&strip.HorizontalAlignment==HorizontalAlignment.Left,"Focus strip not left");Wait((Task)Call(window,"ExecuteCommandAsync",CommandCatalog.FocusRight,null,1d,left)!);Check(ReferenceEquals(Field<PaneContext>(window,"activePane"),right)&&strip.HorizontalAlignment==HorizontalAlignment.Right,"S focus strip not right");
            var row=new DetailRowGrid();var label=new OutlinedTextBlock{Text="快门"};var value=new OutlinedTextBlock{Text="1/100 s"};row.Children.Add(label);row.Children.Add(value);DetailRowGrid.SetValueFirst(row,true);Check(Grid.GetColumn(value)==0&&Grid.GetColumn(label)==1&&value.Text=="1/100 s"&&value.FlowDirection==FlowDirection.LeftToRight,"EXIF columns mirrored or not swapped");
        }finally{window.Close();}
    }
}

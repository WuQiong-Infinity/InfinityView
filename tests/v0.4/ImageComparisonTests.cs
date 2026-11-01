using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Organizing;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.Core.Commands;
using LightView.Core.Viewing;

namespace LightView.Tests;

internal static class ImageComparisonTests
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var cases=new (string Name,Action Test)[]
        {
            ("enter pins one frozen full reference and leaves right identity unchanged",()=>Enter(root)),
            ("empty preview and failed full cannot enter",()=>EntryReadiness(root)),
            ("enter wait navigation exit and reentry reject stale generations",()=>LateEntry(root)),
            ("update reference replaces once and retains only current snapshot",()=>Update(root)),
            ("target refresh hides old reference until matching target and version",()=>Refresh(root)),
            ("changed missing and invalid ICC reference refresh exits without navigating reference",()=>RefreshFailure(root)),
            ("late uncancellable target refresh cannot restore exited comparison",()=>LateRefresh(root)),
            ("T3 reference wins when uncancellable T2 finishes last",()=>MultipleTargets(root)),
            ("reference move is snapshot label only and organizing remains right",()=>Organizing(root)),
            ("new commands have no default key and preserve text repeat isolation",Commands),
            ("linked geometry follows physical zoom and unbounded normalized centers",LinkedGeometry),
            ("fit and actual pixels independently center both panes at all DPI",FitAndPixels),
            ("readonly reference ignores pointer down and never changes right",Readonly),
            ("presentation 192 and 384 MiB limits support 24 and 45 MP estimates",BudgetArithmetic),
            ("unique source residency and reference reservation are counted once",Residency),
            ("cache bypass clears entries and refuses late generation refill",CacheGeneration),
            ("comparison WIC bypasses normal cache and resumes after exit",()=>WicCache(root)),
            ("compatibility and presentation extra allocations obey remaining decode budget",()=>WorkingBudget(root)),
            ("budget fallback exits and retries latest right request exactly once",()=>Fallback(root)),
            ("cancelled old budget failure never retries over newer open",()=>StaleFallback(root)),
            ("prefetch suspension waits cancellation and exit restores normal strategy",()=>Prefetch(root))
            ,("superseded enter cannot overwrite the newer prefetch owner",()=>SupersededOwner(root))
            ,("real artificial copy and move act once on right while left snapshot survives",()=>RealOperations(root))
        };
        int failed=0;var results=new List<object>();foreach(var item in cases)try{item.Test();Console.WriteLine("PASS "+item.Name);results.Add(new{name=item.Name,passed=true});}catch(Exception error){failed++;Console.WriteLine("FAIL "+item.Name+": "+error);results.Add(new{name=item.Name,passed=false,error=error.ToString()});}
        File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{version="1.1.0-rc.2",passed=cases.Length-failed,total=cases.Length,scope="Artificial snapshots and controlled loaders, pure boundaries and offscreen viewport tests; not physical desktop acceptance",results},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"RESULT {cases.Length-failed}/{cases.Length} comparison checks passed");return failed==0?0:1;
    }
    private static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<.000001,$"Expected {b}, got {a}");
    private static LoadedImage Image(string path,string target="T",string identity="same",bool preview=false)
    {var source=BitmapSource.Create(80,60,96,96,PixelFormats.Bgr24,null,new byte[80*60*3],80*3);source.Freeze();return new(path,source,"Synthetic",0,FileVersion:new(path,identity,100,1,"abc"),IsPreview:preview,OriginalWidth:preview?320:80,OriginalHeight:preview?240:60,DisplayTargetKey:target);}
    private sealed class Loader: IImageLoader
    {
        public Func<string,CancellationToken,LoadedImage> Read=null!;
        public LoadedImage Load(string path,CancellationToken token)=>Read(path,token);
    }
    private static (ViewerViewModel Viewer,Loader Loader,ComparisonSession Session,ComparisonLoadPolicy Policy) Setup(string root)
    {var loader=new Loader();loader.Read=(path,_)=>Image(path);var viewer=new ViewerViewModel(loader);var policy=new ComparisonLoadPolicy();var session=new ComparisonSession(viewer,loader,policy,policy.Enable,()=>"T");return(viewer,loader,session,policy);}
    private static void Enter(string root)
    {var s=Setup(root);string a=Path.Combine(root,"a.png");s.Viewer.OpenAsync(a).GetAwaiter().GetResult();var right=s.Viewer.DisplayedImage;Check(s.Session.EnterAsync().GetAwaiter().GetResult(),"Entry failed");Check(ReferenceEquals(right,s.Session.Reference)&&ReferenceEquals(right,s.Viewer.OrganizingImage)&&s.Policy.Enabled&&s.Viewer.IsPrefetchSuspended,"Entry mutated right or failed suspension");Check(s.Session.Linked,"Link default disabled");s.Session.SetLinked(false);s.Session.Exit();Check(s.Session.Reference is null&&!s.Policy.Enabled&&!s.Viewer.IsPrefetchSuspended,"Exit retained state");Check(s.Session.EnterAsync().GetAwaiter().GetResult()&&s.Session.Linked,"Link persisted across sessions");s.Session.Dispose();}
    private static void EntryReadiness(string root)
    {var s=Setup(root);Check(!s.Session.EnterAsync().GetAwaiter().GetResult(),"Empty entered");s.Loader.Read=(p,_)=>Image(p,preview:true);s.Viewer.OpenAsync(Path.Combine(root,"preview.png")).GetAwaiter().GetResult();Check(!s.Session.EnterAsync().GetAwaiter().GetResult(),"Preview entered");s.Loader.Read=(p,_)=>throw new IOException("bad full");s.Viewer.OpenAsync(Path.Combine(root,"bad.png")).GetAwaiter().GetResult();Check(!s.Session.EnterAsync().GetAwaiter().GetResult(),"Failed full entered");s.Session.Dispose();}
    private sealed class Progressive : IProgressiveImageLoader
    {
        public readonly ManualResetEventSlim Started=new(),Release=new();public LoadedImage LoadPreview(string p,int edge,CancellationToken token)=>Image(p,preview:true);
        public LoadedImage Load(string p,CancellationToken token){Started.Set();Release.Wait();return Image(p);}
        public bool TryPrefetchPreview(string p,int edge,CancellationToken token)=>false;
    }
    private static void LateEntry(string root)
    {var loader=new Progressive();var viewer=new ViewerViewModel(loader);var policy=new ComparisonLoadPolicy();var session=new ComparisonSession(viewer,loader,policy,policy.Enable,()=>"T");Task open=viewer.OpenAsync(Path.Combine(root,"entry.jpg"));Check(loader.Started.Wait(5000),"Full did not start");Task<bool> enter=session.EnterAsync();session.Exit();loader.Release.Set();open.GetAwaiter().GetResult();Check(!enter.GetAwaiter().GetResult()&&!session.IsOn,"Exit lost generation guard");Check(session.EnterAsync().GetAwaiter().GetResult(),"Reentry failed");session.Dispose();}
    private static void Update(string root)
    {var s=Setup(root);s.Viewer.OpenAsync(Path.Combine(root,"ua.png")).GetAwaiter().GetResult();s.Session.EnterAsync().GetAwaiter().GetResult();var old=s.Session.Reference;s.Viewer.OpenAsync(Path.Combine(root,"ub.png")).GetAwaiter().GetResult();Check(ReferenceEquals(old,s.Session.Reference),"Open changed pin");s.Session.SetLinked(false);Check(s.Session.UpdateReferenceAsync().GetAwaiter().GetResult()&&ReferenceEquals(s.Session.Reference,s.Viewer.DisplayedImage)&&!ReferenceEquals(old,s.Session.Reference),"Update did not replace current");s.Session.Dispose();}
    private static void Refresh(string root)
    {string target="T";var loader=new Loader();loader.Read=(p,_)=>Image(p,target);var viewer=new ViewerViewModel(loader);var policy=new ComparisonLoadPolicy();var session=new ComparisonSession(viewer,loader,policy,policy.Enable,()=>target);string a=Path.Combine(root,"ra.png"),b=Path.Combine(root,"rb.png");viewer.OpenAsync(a).GetAwaiter().GetResult();session.EnterAsync().GetAwaiter().GetResult();viewer.OpenAsync(b).GetAwaiter().GetResult();bool hidden=false;session.Changed+=()=>{if(session.State==ComparisonState.RefreshingReference&&session.Reference is null)hidden=true;};target="T2";session.RefreshTargetAsync(b,ImageViewIntent.Refresh).GetAwaiter().GetResult();Check(hidden&&session.Reference?.DisplayTargetKey=="T2"&&viewer.DisplayedImage?.FilePath==b&&viewer.DisplayedImage.DisplayTargetKey=="T2","Mixed target or right navigated");session.Dispose();}
    private static void RefreshFailure(string root)
    {foreach(string failure in new[]{"changed","missing","icc"}){string target="T";string a=Path.Combine(root,"fa.png"),b=Path.Combine(root,"fb.png");var loader=new Loader();loader.Read=(p,_)=>Image(p,target);var viewer=new ViewerViewModel(loader);var policy=new ComparisonLoadPolicy();var session=new ComparisonSession(viewer,loader,policy,policy.Enable,()=>target);viewer.OpenAsync(a).GetAwaiter().GetResult();session.EnterAsync().GetAwaiter().GetResult();viewer.OpenAsync(b).GetAwaiter().GetResult();target="T2";loader.Read=(p,_)=>p!=a?Image(p,target):failure=="changed"?Image(p,target,"replacement"):throw failure switch{"missing"=>new FileNotFoundException(),_=>new NotSupportedException("invalid ICC")};session.RefreshTargetAsync(b,ImageViewIntent.Refresh).GetAwaiter().GetResult();Check(!session.IsOn&&session.Reference is null&&viewer.DisplayedImage?.FilePath==b,"Failed reference replaced right or stayed active");session.Dispose();}}
    private static void LateRefresh(string root)
    {string target="T";string a=Path.Combine(root,"la.png"),b=Path.Combine(root,"lb.png");var loader=new Loader();loader.Read=(p,_)=>Image(p,target);var viewer=new ViewerViewModel(loader);var policy=new ComparisonLoadPolicy();var session=new ComparisonSession(viewer,loader,policy,policy.Enable,()=>target);viewer.OpenAsync(a).GetAwaiter().GetResult();session.EnterAsync().GetAwaiter().GetResult();viewer.OpenAsync(b).GetAwaiter().GetResult();using var started=new ManualResetEventSlim();using var release=new ManualResetEventSlim();target="T2";loader.Read=(p,_)=>{if(p==a){started.Set();release.Wait();}return Image(p,target);};Task refresh=session.RefreshTargetAsync(b,ImageViewIntent.Refresh);Check(started.Wait(5000),"Reference load not started");session.Exit();release.Set();refresh.GetAwaiter().GetResult();Check(!session.IsOn&&session.Reference is null,"Late reference restored closed session");session.Dispose();}
    private static void MultipleTargets(string root)
    {
        string target="T",a=Path.Combine(root,"multi-a.png"),b=Path.Combine(root,"multi-b.png");var loader=new Loader();loader.Read=(p,_)=>Image(p,target);var viewer=new ViewerViewModel(loader);var policy=new ComparisonLoadPolicy();using var session=new ComparisonSession(viewer,loader,policy,policy.Enable,()=>target);
        viewer.OpenAsync(a).GetAwaiter().GetResult();session.EnterAsync().GetAwaiter().GetResult();viewer.OpenAsync(b).GetAwaiter().GetResult();using var started=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        loader.Read=(p,_)=>{string captured=target;if(p==a&&captured=="T2"){started.Set();release.Wait();}return Image(p,captured);};
        target="T2";Task older=session.RefreshTargetAsync(b,ImageViewIntent.Refresh);Check(started.Wait(5000),"T2 reference did not start");target="T3";session.RefreshTargetAsync(b,ImageViewIntent.Refresh).GetAwaiter().GetResult();Check(session.Reference?.DisplayTargetKey=="T3"&&viewer.DisplayedImage?.DisplayTargetKey=="T3","T3 failed to publish matching pair");var winner=session.Reference;release.Set();older.GetAwaiter().GetResult();Check(ReferenceEquals(winner,session.Reference)&&session.State==ComparisonState.Active&&session.Reference?.DisplayTargetKey=="T3","Late T2 replaced T3 reference");
    }
    private static void Organizing(string root)
    {var s=Setup(root);string a=Path.Combine(root,"oa.png"),b=Path.Combine(root,"ob.png");s.Viewer.OpenAsync(a).GetAwaiter().GetResult();s.Session.EnterAsync().GetAwaiter().GetResult();var reference=s.Session.Reference!;s.Viewer.OpenAsync(b).GetAwaiter().GetResult();Check(s.Viewer.OrganizingImage?.FilePath==b&&s.Session.Reference?.FilePath==a,"Reference took organizing role");s.Session.NoteMoved(reference);Check(s.Session.ReferenceMoved&&ReferenceEquals(reference,s.Session.Reference),"Move changed pinned bytes/path");s.Session.Dispose();}
    private static void Commands(){foreach(string id in new[]{CommandCatalog.Comparison,CommandCatalog.UpdateReference,CommandCatalog.LinkComparison}){Check(CommandCatalog.Find(id)?.AllowRepeat==false&&!CommandCatalog.DefaultBindings().Any(b=>b.CommandId==id&&b.Gesture.Length>0),"Default command semantics changed");var map=new ShortcutMap([new(id,"CTRL+L")]);Check(map.Resolve("CTRL+L")?.Id==id&&map.Resolve("CTRL+L",true)is null&&map.Resolve("CTRL+L",textEntry:true)is null,"Input isolation bypassed");}}
    private static void LinkedGeometry(){foreach(double dpi in new[]{1d,1.5,2}){var r=new ViewportTransform();r.Resize(500,300,dpi);r.SetImage(2400,1600);r.ActualPixels();r.ZoomAt(60,50,1.7);r.Pan(9000,-8000);var l=new ViewportTransform();l.Resize(430,320,dpi);l.SetImage(1600,2400);l.Follow(r);Near(l.Zoom,r.Zoom);Near((l.ViewWidth/2-l.OffsetX)/(l.DipPerPixel*l.ImageWidth),(r.ViewWidth/2-r.OffsetX)/(r.DipPerPixel*r.ImageWidth));Near((l.ViewHeight/2-l.OffsetY)/(l.DipPerPixel*l.ImageHeight),(r.ViewHeight/2-r.OffsetY)/(r.DipPerPixel*r.ImageHeight));double x=r.OffsetX;l.Pan(100,20);Near(r.OffsetX,x);}}
    private static void FitAndPixels(){foreach(double dpi in new[]{1d,1.5,2}){var r=new ViewportTransform();r.Resize(500,300,dpi);r.SetImage(2400,1600);var l=new ViewportTransform();l.Resize(500,300,dpi);l.SetImage(1600,2400);l.Follow(r);Check(l.IsFit&&r.IsFit,"Fit not independent");r.ActualPixels();l.Follow(r);Near(l.Zoom,1);Near(l.OffsetX,(500-1600/dpi)/2);Near(l.OffsetY,(300-2400/dpi)/2);}}
    private static void Readonly(){var v=new ImageViewport{Image=Image("left"),IsReadOnly=true,Focusable=false};Check(v.IsReadOnly&&!v.Focusable,"Left takes keyboard focus");var state=v.Transform;double x=state.OffsetX;var method=typeof(ImageViewport).GetMethod("OnMouseLeftButtonDown",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;var args=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left);method.Invoke(v,[args]);Check(!v.IsMouseCaptured&&!args.Handled&&state.OffsetX==x,"Readonly pointer began a gesture");}
    private static void Reject(Action action,string why){bool rejected=false;try{action();}catch(ComparisonBudgetExceededException){rejected=true;}Check(rejected,why);}
    private static void BudgetArithmetic(){var p=new ComparisonLoadPolicy();p.Enable(true);p.CheckPresentation(ComparisonBudget.Presentation(6000,4000,32));p.CheckPresentation(ComparisonBudget.Presentation(8256,5504,32));p.CheckPresentation(ComparisonBudget.PerImage);Reject(()=>p.CheckPresentation(ComparisonBudget.PerImage+1),"Per-image limit missing");p.SetSnapshot([],ComparisonBudget.PerImage+1);Reject(()=>p.CheckPresentation(ComparisonBudget.PerImage),"Pair limit missing");p.SetSnapshot([(1,1000)],2000,true);p.CheckWorking(ComparisonBudget.Decode-3000);Reject(()=>p.CheckWorking(ComparisonBudget.Decode-2999),"Working limit missing");p.Enable(false);p.CheckWorking(long.MaxValue);}
    private static void Residency(){var p=new ComparisonLoadPolicy();var a=Image("same");p.SetSources(a,a);Check(p.VisibleBytes==ComparisonBudget.Cost(a),"Shared source charged twice");var b=Image("other");p.SetSources(a,b);Check(p.VisibleBytes==ComparisonBudget.Cost(a)+ComparisonBudget.Cost(b),"Distinct source not counted");}
    private static void CacheGeneration(){var cache=new ImageMemoryCache(1000000);var image=Image("cached");var v=image.FileVersion!;long old=cache.Generation;cache.Add(v,"T",image,old);Check(cache.Count==1,"Normal cache failed");cache.SetBypass(true);Check(cache.Count==0&&!cache.TryGet(v,"T",out _),"Comparison cache not cleared");cache.SetBypass(false);cache.Add(v,"T",image,old);Check(cache.Count==0,"Late pre-comparison write refilled cache");cache.Add(v,"T",image,cache.Generation);Check(cache.Count==1,"Exit did not restore cache");}
    private static string Png(string root,string name){string path=Path.Combine(root,name);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(Image("pixels").Source));using var f=File.Create(path);encoder.Save(f);return path;}
    private static void WicCache(string root){string path=Png(root,"cache.png");var loader=new WicImageLoader();loader.Load(path,CancellationToken.None);Check(loader.CacheCount==1,"Missing normal cache");loader.SetComparisonMode(true);loader.Load(path,CancellationToken.None);Check(loader.CacheCount==0,"Comparison refilled cache");loader.SetComparisonMode(false);loader.Load(path,CancellationToken.None);Check(loader.CacheCount==1,"Exit did not restore cache");}
    private static void WorkingBudget(string root){string path=Png(root,"working.png");var loader=new WicImageLoader();loader.SetComparisonMode(true);long pixels=80*60;loader.ComparisonPolicy.SetSnapshot([(1,ComparisonBudget.Decode-pixels*16)]);Reject(()=>loader.Load(path,CancellationToken.None),"Optional independent presentation was skipped instead of budget rejection");var broken=MetadataCompatibilityTests.Fixture(root,"comparison-compatible");loader.ComparisonPolicy.SetSnapshot([(1,ComparisonBudget.Decode-8000)]);Reject(()=>loader.Load(broken.Broken,CancellationToken.None),"Compatibility extra allocation ignored residency");loader.SetComparisonMode(false);Check(loader.Load(broken.Broken,CancellationToken.None).PixelWidth==32,"Ordinary compatibility behavior changed");}
    private static void Fallback(string root){var loader=new Loader();bool compare=true;int calls=0,fallback=0;loader.Read=(p,_)=>{calls++;if(compare)throw new ComparisonBudgetExceededException();return Image(p);};var viewer=new ViewerViewModel(loader);viewer.ComparisonBudgetFallback=()=>{fallback++;compare=false;};viewer.OpenAsync(Path.Combine(root,"fallback.png"),ImageViewIntent.Adjacent).GetAwaiter().GetResult();Check(calls==2&&fallback==1&&viewer.DisplayedImage?.ViewIntent==ImageViewIntent.Adjacent,"Fallback did not retry once");calls=0;fallback=0;loader.Read=(p,_)=>{calls++;throw new ComparisonBudgetExceededException();};viewer.OpenAsync(Path.Combine(root,"still-too-large.png")).GetAwaiter().GetResult();Check(calls==2&&fallback==1&&viewer.ErrorMessage.Length>0,"Fallback infinite or swallowed ordinary failure");}
    private static void StaleFallback(string root){var loader=new Loader();using var started=new ManualResetEventSlim();using var release=new ManualResetEventSlim();string old=Path.Combine(root,"old-budget.png"),newer=Path.Combine(root,"new-budget.png");int fallback=0;loader.Read=(p,_)=>{if(p==old){started.Set();release.Wait();throw new ComparisonBudgetExceededException();}return Image(p);};var viewer=new ViewerViewModel(loader);viewer.ComparisonBudgetFallback=()=>fallback++;Task task=viewer.OpenAsync(old);Check(started.Wait(5000),"Old decode not started");viewer.OpenAsync(newer).GetAwaiter().GetResult();release.Set();task.GetAwaiter().GetResult();Check(fallback==0&&viewer.DisplayedImage?.FilePath==newer,"Stale budget retried over newer");}
    private sealed class PrefetchLoader:IImageLoader,IImagePrefetcher{public readonly ManualResetEventSlim Started=new(),Stopped=new();public LoadedImage Load(string p,CancellationToken t)=>Image(Path.GetFullPath(p));public int Count;public bool TryPrefetch(string p,CancellationToken t){Interlocked.Increment(ref Count);Started.Set();t.WaitHandle.WaitOne(5000);Stopped.Set();t.ThrowIfCancellationRequested();return false;}}
    private static void Prefetch(string root){string dir=Path.Combine(root,"prefetch");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"a.png"),"a");File.WriteAllText(Path.Combine(dir,"b.png"),"b");var loader=new PrefetchLoader();var viewer=new ViewerViewModel(loader);viewer.OpenAsync(Path.Combine(dir,"a.png")).GetAwaiter().GetResult();Check(loader.Started.Wait(5000),"Prefetch did not start");Task original=viewer.PrefetchReady;viewer.ResumePrefetch();Check(ReferenceEquals(original,viewer.PrefetchReady)&&loader.Count==1,"Unpaused Resume replaced active owner");viewer.SuspendPrefetchAsync().GetAwaiter().GetResult();Check(loader.Stopped.IsSet&&viewer.IsPrefetchSuspended,"Suspend did not await cancellation");viewer.ResumePrefetch();Check(!viewer.IsPrefetchSuspended,"Resume failed");viewer.CancelPrefetch();viewer.PrefetchReady.GetAwaiter().GetResult();}
    private sealed class OwnerLoader(string blocked):IImageLoader,IImagePrefetcher
    {
        public readonly ManualResetEventSlim Loading=new(),Release=new(),Prefetching=new(); public int PrefetchCount;
        public LoadedImage Load(string path,CancellationToken token){if(Path.GetFileName(path)==blocked){Loading.Set();Release.Wait();}return Image(Path.GetFullPath(path));}
        public bool TryPrefetch(string path,CancellationToken token){Interlocked.Increment(ref PrefetchCount);Prefetching.Set();token.WaitHandle.WaitOne(5000);token.ThrowIfCancellationRequested();return false;}
    }
    private static void SupersededOwner(string root)
    {
        string dir=Path.Combine(root,"owner-race");Directory.CreateDirectory(dir);foreach(string name in new[]{"a.png","b.png","c.png"})File.WriteAllText(Path.Combine(dir,name),name);
        var loader=new OwnerLoader("a.png");var viewer=new ViewerViewModel(loader);var policy=new ComparisonLoadPolicy();using var session=new ComparisonSession(viewer,loader,policy,policy.Enable,()=>"T");
        viewer.OpenAsync(Path.Combine(dir,"c.png")).GetAwaiter().GetResult();Task old=viewer.OpenAsync(Path.Combine(dir,"a.png"));Check(loader.Loading.Wait(5000),"Old request not blocked");Task<bool> entry=session.EnterAsync();viewer.OpenAsync(Path.Combine(dir,"b.png")).GetAwaiter().GetResult();Check(loader.Prefetching.Wait(5000),"New prefetch not started");Task owner=viewer.PrefetchReady;int count=loader.PrefetchCount;loader.Release.Set();old.GetAwaiter().GetResult();Check(!entry.GetAwaiter().GetResult()&&!session.IsOn&&ReferenceEquals(owner,viewer.PrefetchReady)&&loader.PrefetchCount==count,"Aborted entry replaced current prefetch owner");viewer.SuspendPrefetchAsync().GetAwaiter().GetResult();
    }
    private static void RealOperations(string root)
    {
        string dir=Path.Combine(root,"right-operations");Directory.CreateDirectory(dir);string left=Png(dir,"left.png"),right=Png(dir,"right.png"),target=Path.Combine(dir,"target");Directory.CreateDirectory(target);
        var loader=new WicImageLoader();var viewer=new ViewerViewModel(loader);using var session=new ComparisonSession(viewer,loader,loader.ComparisonPolicy,loader.SetComparisonMode,()=>DisplayColorTarget.Standard.Key);
        viewer.OpenAsync(left).GetAwaiter().GetResult();Check(session.EnterAsync().GetAwaiter().GetResult(),"Real WIC entry failed");var pinned=session.Reference!;viewer.OpenAsync(right).GetAwaiter().GetResult();var current=viewer.OrganizingImage!;byte[] original=File.ReadAllBytes(right);
        string logs=Path.Combine(dir,"journal");using var lease=OperationJournal.AcquireSession(logs);var service=new SafeCopyService(new OperationJournal(logs));var copy=service.CopyAsync(new(current.FileVersion!,target,"人工")).GetAwaiter().GetResult();Check(copy.State=="Completed"&&File.ReadAllBytes(copy.Destination!).SequenceEqual(original)&&File.Exists(left),"Copy target/content wrong");
        var move=service.MoveAsync(new(current.FileVersion!,target,"人工",TargetName:"moved.png")).GetAwaiter().GetResult();Check(move.State=="Completed"&&!File.Exists(right)&&File.Exists(left),"Move acted on reference or failed");session.NoteMoved(current);viewer.ApplyMoveAsync(current,move.Record!.DestinationVersion!,false).GetAwaiter().GetResult();Check(ReferenceEquals(pinned,session.Reference)&&viewer.DisplayedImage!.FilePath==move.Destination,"Move changed pin instead of current");
        var undo=service.UndoAsync().GetAwaiter().GetResult();Check(undo.State=="Completed"&&File.Exists(right)&&File.ReadAllBytes(right).SequenceEqual(original)&&File.Exists(left),"Undo did not preserve artificial originals");viewer.CancelPrefetch();
    }
}

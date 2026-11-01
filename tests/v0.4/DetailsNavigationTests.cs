using System.IO;
using LightView.Core.Commands;
using LightView.Core.Settings;
using LightView.Core.Viewing;
using LightView.App.Imaging;
using LightView.App.Persistence;
using LightView.App.ViewModels;

namespace LightView.Tests;

internal static class DetailsNavigationTests
{
    public static (string Name, Action Test)[] Cases(string root) =>
    [
        ("left right clicks navigate once and center clicks do not", ClickEdges),
        ("dragging across edges or back to origin never clicks", DragEdges),
        ("capture loss and outside releases do not navigate", CancelPointer),
        ("Tab details command is rebindable and does not repeat or consume text input", TabCommand),
        ("old settings acquire Tab without overwriting explicit bindings", () => SettingsMigration(root)),
        ("edge navigation switch persists and defaults on", () => EdgeSetting(root)),
        ("independent JPEG and PNG EXIF supply shooting metadata", Metadata),
        ("little endian and malformed optional EXIF do not invent values", MetadataBounds),
        ("details follow displayed file and preserve last good info on load failure", () => DetailsState(root)),
        ("focal detail combines equivalent without inventing absent metadata", FocalDetails),
        ("copy move and undo preserve embedded shooting metadata bytes", () => PreserveExif(root))
    ];
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures", "metadata");
    private static int Click(double x, double width = 1000)
    { var g = new CanvasPointerGesture(); g.Begin(x, 200); return g.Release(x, 200, width, 600, 4, 4); }
    private static void ClickEdges()
    {
        Check(Click(20) == -1 && Click(980) == 1 && Click(500) == 0 && Click(110) == 0, "Wrong edge band");
        Check(Click(5, 200) == -1 && Click(195, 200) == 1 && Click(100, 200) == 0, "Narrow viewport edge overlap");
    }
    private static void DragEdges()
    {
        var g = new CanvasPointerGesture(); g.Begin(20, 200);
        var delta = g.Move(40, 205, 4, 4); Check(delta == (20, 5), "Initial drag lost the threshold distance");
        g.Move(20, 200, 4, 4); Check(g.Release(20, 200, 1000, 600, 4, 4) == 0, "Drag returning to edge became click");
        g.Begin(500, 200); g.Move(980, 200, 4, 4); Check(g.Release(980, 200, 1000, 600, 4, 4) == 0, "Dragged into next zone");
    }
    private static void CancelPointer()
    {
        var g = new CanvasPointerGesture(); g.Begin(20, 200); g.Cancel();
        Check(g.Release(20, 200, 1000, 600, 4, 4) == 0, "Lost capture navigated");
        g.Begin(20, 200); Check(g.Release(-1, 200, 1000, 600, 4, 4) == 0, "Outside release navigated");
    }
    private static void TabCommand()
    {
        var bindings = CommandCatalog.DefaultBindings(); var map = new ShortcutMap(bindings);
        Check(map.Resolve("TAB")?.Id == CommandCatalog.Details && map.Resolve("TAB", true) is null && map.Resolve("TAB", textEntry:true) is null, "Tab dispatch wrong");
        bindings.RemoveAll(b=>b.CommandId==CommandCatalog.Details); bindings.Add(new(CommandCatalog.Details,"I")); map=new(bindings);
        Check(map.Resolve("TAB") is null && map.Resolve("I")?.Id==CommandCatalog.Details, "Details not rebindable");
    }
    private static void SettingsMigration(string root)
    {
        foreach (string mode in new[]{"missing","occupied","cleared"})
        {
            string directory=Path.Combine(root,"details-settings-"+mode);Directory.CreateDirectory(directory);
            var settings=new AppSettings();settings.Bindings.RemoveAll(b=>b.CommandId==CommandCatalog.Details);
            if(mode=="occupied")settings.Bindings.Add(new(CommandCatalog.Next,"TAB"));
            if(mode=="cleared")settings.Bindings.Add(new(CommandCatalog.Details,""));
            File.WriteAllText(Path.Combine(directory,"settings.json"),SettingsTests.LegacyJson(settings));
            var loaded=new SettingsStore(directory).Load();Check(!loaded.IsReadOnly,"Migration became read only");
            var details=loaded.Settings.Bindings.Where(b=>b.CommandId==CommandCatalog.Details).ToArray();
            Check(mode=="missing"?details.Single().Gesture=="TAB":mode=="cleared"?details.Single().Gesture=="":details.Length==0,"Migration overwrote intent");
        }
    }
    private static void EdgeSetting(string root)
    {
        var store=new SettingsStore(Path.Combine(root,"edge-setting"));var settings=store.Load().Settings;
        Check(settings.EdgeNavigationEnabled,"Edge default disabled");settings.EdgeNavigationEnabled=false;store.Save(settings);
        Check(!new SettingsStore(Path.Combine(root,"edge-setting")).Load().Settings.EdgeNavigationEnabled,"Edge setting not saved");
    }
    private static void Metadata()
    {
        foreach(string name in new[]{"shooting.jpg","shooting.png"})
        {
            using var file=File.OpenRead(Path.Combine(Fixtures,name));file.Position=3;
            var result=ExifReader.Read(file);Check(file.Position==3,"Metadata moved decode stream position");
            var fields=result.Fields.ToDictionary(x=>x.Label,x=>x.Value);
            Check(fields.GetValueOrDefault("相机型号")=="NIKON Z50II" && fields.GetValueOrDefault("镜头型号")!.Contains("16-50mm"),"Camera/lens absent");
            Check(fields.GetValueOrDefault("快门")=="1/125 s" && fields.GetValueOrDefault("光圈")=="f/5.6" && fields.GetValueOrDefault("ISO")=="640","Exposure values wrong");
            Check(fields.GetValueOrDefault("曝光补偿")=="-0.33 EV" && fields.GetValueOrDefault("焦距")=="50 mm" && fields.GetValueOrDefault("拍摄时间")=="2026-10-19 14:23:45 +08:00","Signed rational/date incorrect");
            var loaded=new WicImageLoader().Load(Path.Combine(Fixtures,name),CancellationToken.None);
            Check(loaded.Metadata?.Shooting.Count>8,"Metadata did not attach to displayed snapshot");
        }
    }
    private static void MetadataBounds()
    {
        using(var f=File.OpenRead(Path.Combine(Fixtures,"little-endian.png"))) Check(ExifReader.Read(f).Fields.Single().Value=="Independent LE camera","Little endian wrong");
        using(var f=File.OpenRead(Path.Combine(Fixtures,"bad-exif.png"))){var x=ExifReader.Read(f);Check(x.Fields.Count==0&&x.Warning is not null,"Invalid pointer accepted");}
        using(var f=File.OpenRead(Path.Combine(Fixtures,"plain.png")))Check(ExifReader.Read(f).Fields.Count==0,"Invented shooting fields");
        Check(ExifReader.ParseTiff(new byte[]{0,1,2}).Fields.Count==0,"Truncated TIFF accepted");
        using(var f=File.OpenRead(Path.Combine(Fixtures,"zero-denominator.png"))){var x=ExifReader.Read(f);Check(x.Fields.Count==0&&x.Warning is not null,"Rational with zero denominator accepted");}
        Check(new WicImageLoader().Load(Path.Combine(Fixtures,"bad-exif.png"),CancellationToken.None).PixelWidth==96,"Optional EXIF damage blocked valid pixels");
    }
    private static void PreserveExif(string root)
    {
        string directory=Path.Combine(root,"exif-organizing"),target=Path.Combine(directory,"target");Directory.CreateDirectory(target);
        string path=Path.Combine(directory,"shooting.jpg");File.Copy(Path.Combine(Fixtures,"shooting.jpg"),path);
        var source=new WicImageLoader().Load(path,CancellationToken.None);
        var service=new LightView.App.Organizing.SafeCopyService(new LightView.App.Organizing.OperationJournal(Path.Combine(directory,"journal")));
        var copy=service.CopyAsync(new(source.FileVersion!,target,"Exif目标")).GetAwaiter().GetResult();
        Check(copy.State=="Completed"&&File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(copy.Destination!)),"Copy changed EXIF bytes");
        var moved=service.MoveAsync(new(source.FileVersion!,target,"Exif目标")).GetAwaiter().GetResult();Check(moved.State=="Completed","Move failed");
        var undo=service.UndoAsync().GetAwaiter().GetResult();Check(undo.State=="Completed","Undo failed");
        Check(new WicImageLoader().Load(path,CancellationToken.None).Metadata!.Shooting.Any(x=>x.Label=="相机型号"&&x.Value=="NIKON Z50II"),"Undo lost shooting metadata");
    }
    private static void DetailsState(string root)
    {
        var viewer=new ViewerViewModel(new WicImageLoader());viewer.OpenAsync(Path.Combine(Fixtures,"shooting.jpg")).GetAwaiter().GetResult();viewer.CancelPrefetch();
        Check(viewer.DetailsRows.Any(r=>r.Label=="相机型号"&&r.Value=="NIKON Z50II"),"Displayed shot missing");
        Check(viewer.DetailsSections.Count==3 && viewer.DetailsRows.Count(r=>r.Label=="像素尺寸")==1 &&
            !viewer.DetailsRows.Any(r=>r.Label is "原始尺寸" or "显示尺寸" or "文件DPI标记" or "色彩信息"),"Details not simplified/grouped");
        viewer.OpenAsync(Path.Combine(Fixtures,"plain.png")).GetAwaiter().GetResult();viewer.CancelPrefetch();
        Check(!viewer.DetailsRows.Any(r=>r.Label=="相机型号")&&viewer.DetailsRows.Any(r=>r.Label=="拍摄信息"),"Old shot info leaked to next picture");
        viewer.OpenAsync(Path.Combine(root,"nonexistent.png")).GetAwaiter().GetResult();
        Check(viewer.DetailsRows.Single(r=>r.Label=="文件名").Value=="plain.png","Failed load replaced displayed details");
    }
    private sealed class FixedLoader(LoadedImage image) : IImageLoader
    { public LoadedImage Load(string path, CancellationToken cancellationToken) => image; }
    private static void FocalDetails()
    {
        var image=new WicImageLoader().Load(Path.Combine(Fixtures,"shooting.jpg"),CancellationToken.None);
        foreach(var fields in new ImageDetail[][]
        {
            [new("焦距","35 mm"),new("等效35mm焦距","52 mm")],
            [new("焦距","35 mm")], [new("等效35mm焦距","52 mm")], []
        })
        {
            var viewer=new ViewerViewModel(new FixedLoader(image with { Metadata=image.Metadata! with { Shooting=fields } }));
            viewer.OpenAsync(image.FilePath).GetAwaiter().GetResult();
            var focal=viewer.DetailsRows.Where(row=>row.Label.Contains("焦距")).ToArray();
            string? expected=fields.Length==2?"35mm（等效52mm）":fields.Length==0?null:fields[0].Value.Replace(" mm","mm");
            Check(expected is null?focal.Length==0:focal.Length==1&&focal[0].Value==expected,"Focal details duplicated or invented");
            if(fields.Length==1&&fields[0].Label=="等效35mm焦距")Check(focal[0].Label=="等效焦距","Equivalent was presented as actual focal length");
            Check(fields.All(row=>!row.Value.Contains('（')),"Presentation mutated EXIF fields");
        }
    }
}

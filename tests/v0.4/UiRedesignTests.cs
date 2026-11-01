using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Platform;
using LightView.App.ViewModels;
using LightView.App.Views;
using LightView.App.Views.Settings;
using LightView.Core.Settings;

namespace LightView.Tests;

internal static class UiRedesignTests
{
    private static void Check(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
    private static object? Call(object obj,string method,params object?[] args)=>obj.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(obj,args);
    private static T Field<T>(object obj,string field)=>(T)obj.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Wait(Task task){if(!task.IsCompleted){var d=System.Windows.Threading.Dispatcher.CurrentDispatcher;var frame=new System.Windows.Threading.DispatcherFrame();_=task.ContinueWith(_=>d.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.Default);System.Windows.Threading.Dispatcher.PushFrame(frame);}task.GetAwaiter().GetResult();}
    internal static int Run(string root,string? filter=null)
    {
        Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("LIGHTVIEW_DATA_DIR",Path.Combine(root,"UserData"));var app=new LightView.App.App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var cases=new (string Name,Action Test)[]{("retired fields never return and filter label keeps stable ID",Retired),("cards buttons and group-local drag share order with real preview and draft",()=>Editor(root)),("four corners and complete right alignment render actual WPF split",()=>Split(root))};if(filter is not null)cases=cases.Where(c=>c.Name.Contains(filter)).ToArray();int failures=0;
        foreach(var item in cases)try{item.Test();Console.WriteLine("PASS "+item.Name);}catch(Exception error){failures++;Console.WriteLine("FAIL "+item.Name+": "+error);}
        File.WriteAllText(Path.Combine(root,"result.txt"),$"{cases.Length-failures}/{cases.Length}; actual WPF offscreen editor/split images, not desktop interaction");Console.WriteLine($"RESULT {cases.Length-failures}/{cases.Length} UI redesign checks passed");return failures==0?0:1;
    }
    private static void Retired()
    {
        var layout=DetailsCatalog.Normalize(null);layout.Groups[0].Fields.Add(new(){Id="file.format"});layout.Groups[1].Fields.Add(new(){Id="camera.make"});layout.Groups[0].Fields.Add(new(){Id="future.unknown"});var current=DetailsCatalog.Normalize(layout);
        Check(!current.Groups.SelectMany(g=>g.Fields).Any(f=>DetailsCatalog.IsRetired(f.Id))&&current.Groups[0].Fields.Any(f=>f.Id=="future.unknown"),"Retired returned or unknown compatibility lost");
        var rows=new ImageDetail[]{new("格式","JPEG","file.format"),new("相机厂商","NIKON","camera.make")};Check(DetailsProjection.Apply([new("文件",rows,"file")],layout).Count==0,"Retired projected through old config");var style=new CameraStyle("Artificial_Film","FLEXIBLE COLOR");Check(style.Rows[0].Id=="style.name"&&style.Rows[0].Label=="滤镜名称","Filter label/identity incorrect");
    }
    private static void Editor(string root)
    {
        var page=new DetailsPage{FontSize=14,FontFamily=(FontFamily)Application.Current.FindResource("UiFont"),Foreground=(Brush)Application.Current.FindResource("TextBrush")};AppearanceSettings? changed=null;page.Changed+=p=>changed=p;
        page.Load(new AppearanceSettings());page.Measure(new Size(1080,960));page.Arrange(new Rect(0,0,1080,960));page.UpdateLayout();var up=Descendants(page).OfType<Button>().Single(b=>b.ToolTip?.ToString()=="上移像素尺寸");up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));string byButton=string.Join(",",changed!.DetailsLayout!.Groups[0].Fields.Select(f=>f.Id));
        page.Load(new AppearanceSettings());Check(page.ApplyDrop(new(false,"file","file.dimensions"),new(false,"file","file.directory"),false),"Field drag not accepted");Check(string.Join(",",changed!.DetailsLayout!.Groups[0].Fields.Select(f=>f.Id))==byButton,"Drag differs from local arrow");Check(!page.ApplyDrop(new(false,"file","file.name"),new(false,"camera","camera.model"),false),"Cross-group drag accepted");
        page.ApplyDrop(new(true,"style","style"),new(true,"file","file"),false);Check(page.PreviewSections[0].Id=="style","Group drag did not update projected preview");
        page.UpdateLayout();var filter=Descendants(page).OfType<CheckBox>().Single(c=>(c.Content as TextBlock)?.Text=="滤镜名称");filter.IsChecked=false;Check(!page.PreviewSections.SelectMany(g=>g.Rows).Any(r=>r.Id=="style.name"),"Checkbox did not update live preview");
        var settings=new SettingsWindow(new AppSettings(),_=>Task.FromResult<string?>("failed"),null,false);try
        {
            var details=(DetailsPage)settings.FindName("DetailsEditor");details.MoveGroup("style",0);var appearance=(AppearancePage)settings.FindName("AppearanceEditor");((Slider)appearance.FindName("FontSlider")).Value=18;
            var session=Field<SettingsSession>(settings,"session");Check(session.Draft.Appearance.DetailsLayout?.Groups[0].Id=="style"&&session.Draft.Appearance.DetailsFontSize==18,"Switching pages overwrote unapplied layout");
            settings.FocusSection(5);Check(((TabItem)settings.FindName("BackupTab")).IsSelected,"About tab moved");settings.FocusSection(6);Check(((TabItem)settings.FindName("DetailsContentTab")).IsSelected,"Details page unreachable");
            Wait(session.ApplyAsync(session.Draft,_=>Task.FromResult<string?>("failed")));Check(session.LastAppliedSnapshot.Appearance.DetailsLayout is null,"Failed save applied layout");session.TryClose();
        }finally{settings.Close();}
        page.Load(new AppearanceSettings());Render(page,1080,960,Path.Combine(root,"details-editor.png"));
        Check(!Descendants(page).OfType<CheckBox>().Any(c=>c.Content?.ToString()?.Contains("相机厂商")==true),"Retired editor item visible");
    }
    private static void Split(string root)
    {
        var window=new MainWindow();try
        {
            var right=Field<PaneContext>(window,"activePane");string fixtures=Path.Combine(Path.GetDirectoryName(root)!,"desktop-fixtures");Wait(right.Viewer.OpenAsync(Path.Combine(fixtures,"A.jpg")));Wait((Task)Call(window,"ToggleComparisonAsync",right)!);var split=Field<ComparisonSession>(window,"comparison");Wait(right.Viewer.OpenAsync(Path.Combine(fixtures,"B.jpg")));var left=split.Left!;
            Call(window,"ToggleDetails",left);Call(window,"ToggleDetails",right);Call(window,"ActivatePane",left);var visual=(FrameworkElement)window.Content;visual.DataContext=window.DataContext;window.Content=null;Render(visual,1280,800,Path.Combine(root,"split-view.png"));
            foreach(string name in new[]{"ReferenceName","CurrentComparisonName"})
            {
                var label=(FrameworkElement)window.FindName(name);var pane=(FrameworkElement)label.Parent;var bounds=label.TransformToAncestor(pane).TransformBounds(new Rect(label.RenderSize));
                Check(label.VerticalAlignment==VerticalAlignment.Bottom&&label.Margin==new Thickness(10,0,10,10),"Filename chrome lost its L-corner inset");
                Check(Math.Abs(bounds.Left-10)<.1&&Math.Abs(bounds.Right-(pane.ActualWidth-10))<.1&&Math.Abs(bounds.Bottom-(pane.ActualHeight-10))<.1,"Filename is not inset inside the L corner or overlaps the corner legs");
            }
            Check(window.FindName("SplitFocusStrip")is null&&((PaneFocusCorners)window.FindName("LeftFocusCorners")).IsActive&&!((PaneFocusCorners)window.FindName("RightFocusCorners")).IsActive,"Old marker or wrong active corners");
            var panel=(Border)window.FindName("DetailsPanel");Check(Descendants(panel).OfType<OutlinedTextBlock>().All(t=>t.TextAlignment==TextAlignment.Right),"Some right title/group/label/value is left aligned");Check(!right.Viewer.DetailsRows.Any(r=>DetailsCatalog.IsRetired(r.Id)),"Retired fields in real loaded image");
            var corner=new PaneFocusCorners{IsActive=true};var bitmap=Render(corner,200,120,null);byte[] data=Pixels(bitmap);foreach(var p in new[]{(5,5),(195,5),(5,115),(195,115)})Check(Enumerable.Range(-2,5).Any(dx=>Enumerable.Range(-2,5).Any(dy=>data[((p.Item2+dy)*200+p.Item1+dx)*4+3]>0)),"A focus corner was not actually drawn");
            var shared=new DetailsContentView{Width=424,Sections=[new("照片风格",[new("滤镜名称","Artificial_Film","style.name")],"style")]};foreach(var pair in new[]{("DetailsTitleSize",22d),("DetailsSectionSize",17d),("DetailsBodySize",16d),("DetailsOutlineSize",1.2d)})shared.Resources[pair.Item1]=pair.Item2;shared.Resources["DetailsRowMargin"]=new Thickness(0,3,0,3);DetailRowGrid.SetValueFirst(shared,true);var titleBitmap=Render(shared,424,160,null);var title=Pixels(titleBitmap);var xs=new List<int>();for(int y=0;y<30;y++)for(int x=0;x<424;x++){int at=(y*424+x)*4;if(title[at+3]>180&&title[at]>180&&title[at+1]>180&&title[at+2]>180)xs.Add(x);}Check(xs.Count>0&&xs.Min()>200&&xs.Max()>380,"Visible right title is cropped/negative or not at right edge");
        }finally{window.Close();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);yield return c;foreach(var child in Descendants(c))yield return child;}}
    private static byte[] Pixels(BitmapSource source){byte[] b=new byte[source.PixelWidth*source.PixelHeight*4];source.CopyPixels(b,source.PixelWidth*4,0);return b;}
    private static RenderTargetBitmap Render(FrameworkElement element,int width,int height,string? path)
    {element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(element);if(path is not null){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);}return image;}
}

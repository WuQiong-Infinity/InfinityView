using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LightView.App.Imaging;
using LightView.Core.Settings;

namespace LightView.App.Views.Settings;

public partial class DetailsPage : UserControl
{
    private AppearanceSettings value=new();
    private DetailsLayout layout=DetailsCatalog.Normalize(null);
    private bool readOnly, previewRight=true;
    internal sealed record LayoutDrag(bool Group,string GroupId,string Id);
    private const string DragFormat="LightView.DetailLayout";
    public event Action<AppearanceSettings>? Changed;
    internal IReadOnlyList<ImageDetailSection> PreviewSections=>PreviewDisplay.Sections??Array.Empty<ImageDetailSection>();
    public DetailsPage(){InitializeComponent();Load(value);}
    public void Load(AppearanceSettings settings)
    {value=settings.Clone();layout=DetailsCatalog.Normalize(settings.DetailsLayout);RenderCards();UpdatePreview();}
    internal void SetReadOnly(bool state){readOnly=state;CardsHost.IsEnabled=!state;ResetLayoutButton.IsEnabled=!state;}
    public void ResetLayout(){if(readOnly)return;value.DetailsLayout=null;layout=DetailsCatalog.Normalize(null);RenderCards();UpdatePreview();Changed?.Invoke(value.Clone());}
    private void OnReset(object sender,RoutedEventArgs e)=>ResetLayout();
    private void OnPreviewSide(object sender,RoutedEventArgs e)
    {previewRight=(sender as FrameworkElement)?.Tag as string=="right";UpdatePreview();}
    private void Commit(bool rebuild)
    {value.DetailsLayout=DetailsCatalog.Normalize(layout);if(rebuild)RenderCards();UpdatePreview();Changed?.Invoke(value.Clone());}
    internal void MoveGroup(string id,int to)
    {if(readOnly)return;int from=layout.Groups.FindIndex(g=>g.Id==id);if(!Move(layout.Groups,from,to))return;Commit(true);}
    internal void MoveField(string groupId,string id,int to)
    {if(readOnly)return;var group=layout.Groups.FirstOrDefault(g=>g.Id==groupId);if(group is null||!group.Enabled)return;int from=group.Fields.FindIndex(f=>f.Id==id);if(!Move(group.Fields,from,to))return;Commit(true);}
    private static bool Move<T>(List<T> list,int from,int to)
    {if(from<0||to<0||to>=list.Count||from==to)return false;var item=list[from];list.RemoveAt(from);list.Insert(to,item);return true;}
    internal bool ApplyDrop(LayoutDrag source,LayoutDrag target,bool after)
    {
        if(readOnly||source.Group!=target.Group||!source.Group&&source.GroupId!=target.GroupId)return false;
        if(source.Group){int from=layout.Groups.FindIndex(g=>g.Id==source.Id),to=layout.Groups.FindIndex(g=>g.Id==target.Id);if(from<0||to<0)return false;to+=after?1:0;if(from<to)to--;MoveGroup(source.Id,Math.Max(0,Math.Min(layout.Groups.Count-1,to)));return true;}
        var group=layout.Groups.FirstOrDefault(g=>g.Id==source.GroupId);if(group is null||!group.Enabled)return false;
        int at=group.Fields.FindIndex(f=>f.Id==source.Id),dest=group.Fields.FindIndex(f=>f.Id==target.Id);if(at<0||dest<0)return false;dest+=after?1:0;if(at<dest)dest--;MoveField(group.Id,source.Id,Math.Max(0,Math.Min(group.Fields.Count-1,dest)));return true;
    }
    private Button Arrow(string text,string hint,bool enabled,Action action)
    {
        var button=new Button{Content=text,ToolTip=hint,Width=26,Height=26,MinWidth=0,MinHeight=0,Padding=new Thickness(0),Margin=new Thickness(2,0,0,0),Background=Brushes.Transparent,BorderThickness=new Thickness(0),IsEnabled=enabled&&!readOnly};
        button.SetResourceReference(ForegroundProperty,"SecondaryBrush");button.Click+=(_,_)=>action();return button;
    }
    private FrameworkElement Handle(LayoutDrag item,string name)
    {
        var handle=new TextBlock{Text="⋮⋮",FontSize=15,Width=22,Padding=new Thickness(0,4,0,4),Cursor=Cursors.SizeAll,Background=Brushes.Transparent,VerticalAlignment=VerticalAlignment.Center};handle.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryBrush");System.Windows.Automation.AutomationProperties.SetName(handle,"拖动"+name);
        Point? start=null;handle.MouseLeftButtonDown+=(_,e)=>{if(readOnly)return;start=e.GetPosition(handle);handle.CaptureMouse();e.Handled=true;};handle.MouseLeftButtonUp+=(_,_)=>{start=null;handle.ReleaseMouseCapture();};
        handle.MouseMove+=(_,e)=>{if(start is not{} origin||e.LeftButton!=MouseButtonState.Pressed||readOnly)return;var p=e.GetPosition(handle);if(Math.Abs(p.X-origin.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(p.Y-origin.Y)<SystemParameters.MinimumVerticalDragDistance)return;start=null;handle.ReleaseMouseCapture();DragDrop.DoDragDrop(handle,new DataObject(DragFormat,item),DragDropEffects.Move);};return handle;
    }
    private void DropTarget(Border border,LayoutDrag target)
    {
        border.AllowDrop=true;Thickness ordinary=border.BorderThickness;Brush? brush=border.BorderBrush;
        void Restore(){border.BorderThickness=ordinary;border.BorderBrush=brush;}
        border.DragOver+=(_,e)=>{if(readOnly||e.Data.GetData(DragFormat)is not LayoutDrag item||item.Group!=target.Group||!item.Group&&item.GroupId!=target.GroupId){e.Effects=DragDropEffects.None;return;}e.Effects=DragDropEffects.Move;border.BorderThickness=new Thickness(0,2,0,ordinary.Bottom);border.SetResourceReference(Border.BorderBrushProperty,"AccentBrush");e.Handled=true;};
        border.DragLeave+=(_,_)=>Restore();border.Drop+=(_,e)=>{Restore();if(e.Data.GetData(DragFormat)is LayoutDrag item){ApplyDrop(item,target,e.GetPosition(border).Y>border.ActualHeight/2);e.Handled=true;}};
    }
    private void RenderCards()
    {
        double offset=CardsScroll.VerticalOffset;CardsHost.Children.Clear();
        for(int gi=0;gi<layout.Groups.Count;gi++)
        {
            var group=layout.Groups[gi];int index=gi;var definition=DetailsCatalog.Groups.FirstOrDefault(g=>g.Id==group.Id);string name=definition?.Name??"不可用："+group.Id;
            var body=new StackPanel();var card=new Border{Child=body,CornerRadius=new CornerRadius(6),Padding=new Thickness(12),Margin=new Thickness(0,0,0,12),BorderThickness=new Thickness(1)};card.SetResourceReference(Border.BackgroundProperty,"SurfaceBrush");card.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");
            var head=new DockPanel{LastChildFill=true};var groupDrag=new LayoutDrag(true,group.Id,group.Id);var handle=Handle(groupDrag,name);DockPanel.SetDock(handle,Dock.Left);head.Children.Add(handle);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(Arrow("↑","上移分组"+name,gi>0,()=>MoveGroup(group.Id,index-1)));buttons.Children.Add(Arrow("↓","下移分组"+name,gi<layout.Groups.Count-1,()=>MoveGroup(group.Id,index+1)));DockPanel.SetDock(buttons,Dock.Right);head.Children.Add(buttons);
            var check=new CheckBox{Content=name,IsChecked=group.Enabled,FontWeight=FontWeights.SemiBold,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,0,4)};check.Checked+=(_,_)=>{group.Enabled=true;Commit(true);};check.Unchecked+=(_,_)=>{group.Enabled=false;Commit(true);};head.Children.Add(check);
            var header=new Border{Child=head,BorderBrush=new SolidColorBrush(Color.FromRgb(66,73,84)),BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(0,0,0,8),Margin=new Thickness(0,0,0,5)};DropTarget(header,groupDrag);body.Children.Add(header);
            for(int fi=0;fi<group.Fields.Count;fi++)
            {
                var field=group.Fields[fi];int fieldIndex=fi;string fieldName=definition?.Fields.FirstOrDefault(f=>f.Id==field.Id)?.Name??"不可用："+field.Id;
                var row=new DockPanel{LastChildFill=true,MinHeight=34,Opacity=group.Enabled?1:.45,IsEnabled=group.Enabled};var drag=new LayoutDrag(false,group.Id,field.Id);var fieldHandle=Handle(drag,fieldName);DockPanel.SetDock(fieldHandle,Dock.Left);row.Children.Add(fieldHandle);
                var moves=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};moves.Children.Add(Arrow("↑","上移"+fieldName,fi>0,()=>MoveField(group.Id,field.Id,fieldIndex-1)));moves.Children.Add(Arrow("↓","下移"+fieldName,fi<group.Fields.Count-1,()=>MoveField(group.Id,field.Id,fieldIndex+1)));DockPanel.SetDock(moves,Dock.Right);row.Children.Add(moves);
                var toggle=new CheckBox{Content=new TextBlock{Text=fieldName,TextWrapping=TextWrapping.Wrap},IsChecked=field.Enabled,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,0,4)};toggle.Checked+=(_,_)=>{field.Enabled=true;Commit(false);};toggle.Unchecked+=(_,_)=>{field.Enabled=false;Commit(false);};row.Children.Add(toggle);
                var wrap=new Border{Child=row,BorderBrush=new SolidColorBrush(Color.FromRgb(48,55,65)),BorderThickness=new Thickness(0,0,0,1)};DropTarget(wrap,drag);body.Children.Add(wrap);
            }
            CardsHost.Children.Add(card);
        }
        CardsHost.IsEnabled=!readOnly;CardsScroll.ScrollToVerticalOffset(offset);
    }
    private void UpdatePreview()
    {
        LeftPreviewButton.IsChecked=!previewRight;RightPreviewButton.IsChecked=previewRight;DetailRowGrid.SetValueFirst(PreviewDisplay,previewRight);
        Resources["DetailsTitleSize"]=value.DetailsFontSize*22/16;Resources["DetailsSectionSize"]=value.DetailsFontSize*17/16;Resources["DetailsBodySize"]=value.DetailsFontSize;Resources["DetailsOutlineSize"]=value.DetailsOutlineThickness;Resources["DetailsRowMargin"]=new Thickness(0,value.DetailsLineGap/2,0,value.DetailsLineGap/2);
        PreviewDisplay.Width=value.DetailsWidth-16;PreviewScale.Stretch=value.DetailsFitSmallWindow?Stretch.Uniform:Stretch.None;
        var values=new Dictionary<string,string>{{"file.name","人工样本.jpg"},{"file.directory","人工样本目录"},{"file.dimensions","6,000 × 4,000"},{"file.size","8.2 MiB"},{"file.modified","2026-10-30 12:00:00"},{"camera.model","Nikon Z50 II"},{"lens.make","NIKKOR"},{"lens.model","DX 16–50mm f/3.5–6.3"},{"capture.time","2026-10-30 11:58:00"},{"exposure.shutter","1/100 s"},{"exposure.aperture","f/5.6"},{"exposure.iso","640"},{"exposure.focal","35mm（等效52mm）"},{"exposure.compensation","-0.33 EV"},{"exposure.program","光圈优先"},{"exposure.metering","多区/评价"},{"exposure.flash","未闪光"},{"exposure.whitebalance","自动"},{"style.name","Artificial_Film"},{"style.base","灵活颜色（FLEXIBLE COLOR）"}};
        var source=DetailsCatalog.Groups.Select(g=>new ImageDetailSection(g.Name,g.Fields.Select(f=>new ImageDetail(f.Name,values.GetValueOrDefault(f.Id)??"人工值",f.Id)).ToArray(),g.Id)).ToArray();PreviewDisplay.Sections=DetailsProjection.Apply(source,value.DetailsLayout);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LightView.App.Imaging;

namespace LightView.App.Views;

// Shared by the actual pane and the settings preview. No separate mock renderer.
public sealed class DetailsContentView : UserControl
{
    public static readonly DependencyProperty SectionsProperty=DependencyProperty.Register(nameof(Sections),typeof(IReadOnlyList<ImageDetailSection>),typeof(DetailsContentView),new PropertyMetadata(null,(d,_)=>((DetailsContentView)d).Rebuild()));
    public IReadOnlyList<ImageDetailSection>? Sections{get=>(IReadOnlyList<ImageDetailSection>?)GetValue(SectionsProperty);set=>SetValue(SectionsProperty,value);}
    private readonly StackPanel stack=new();
    public DetailsContentView(){Content=stack;IsHitTestVisible=false;Focusable=false;}
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {base.OnPropertyChanged(e);if(e.Property==DetailRowGrid.ValueFirstProperty&&stack is not null)Align();}
    private OutlinedTextBlock Text(string text,string size,Brush brush)
    {
        var block=new OutlinedTextBlock{Text=text,Foreground=brush,TextWrapping=TextWrapping.Wrap};
        block.SetResourceReference(FontSizeProperty,size);block.SetResourceReference(OutlinedTextBlock.OutlineThicknessProperty,"DetailsOutlineSize");return block;
    }
    private void Rebuild()
    {
        stack.Children.Clear();if(Sections is not {Count:>0})return;
        stack.Children.Add(Text("图片详情","DetailsTitleSize",Brushes.White));
        foreach(var section in Sections)
        {
            var group=new StackPanel{Margin=new Thickness(0,10,0,0)};
            group.Children.Add(new Border{BorderBrush=new SolidColorBrush(Color.FromRgb(85,85,85)),BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(0,0,0,6),Margin=new Thickness(0,0,0,6),Child=Text(section.Title,"DetailsSectionSize",new SolidColorBrush(Color.FromRgb(198,219,237)))});
            foreach(var row in section.Rows)
            {
                var grid=new DetailRowGrid{Tag=row};grid.SetResourceReference(MarginProperty,"DetailsRowMargin");
                grid.Children.Add(Text(row.Label,"DetailsBodySize",new SolidColorBrush(Color.FromRgb(221,221,221))));grid.Children.Add(Text(row.Value,"DetailsBodySize",Brushes.White));group.Children.Add(grid);
            }
            stack.Children.Add(group);
        }
        Align();
    }
    private void Align()
    {
        var alignment=DetailRowGrid.GetValueFirst(this)?TextAlignment.Right:TextAlignment.Left;
        foreach(var text in Descendants(stack).OfType<OutlinedTextBlock>())text.TextAlignment=alignment;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
}

using System.Windows;
using System.Windows.Controls;

namespace LightView.App.Views;

// Changes column placement only; text and its reading direction are untouched.
public sealed class DetailRowGrid : Grid
{
    public static readonly DependencyProperty ValueFirstProperty = DependencyProperty.RegisterAttached("ValueFirst",typeof(bool),typeof(DetailRowGrid),
        new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits,(d,_)=> { if(d is DetailRowGrid row) row.UpdateColumns(); }));
    public static bool GetValueFirst(DependencyObject d)=>(bool)d.GetValue(ValueFirstProperty);
    public static void SetValueFirst(DependencyObject d,bool value)=>d.SetValue(ValueFirstProperty,value);
    public DetailRowGrid(){ColumnDefinitions.Add(new());ColumnDefinitions.Add(new());UpdateColumns();}
    protected override void OnVisualChildrenChanged(DependencyObject added,DependencyObject removed){base.OnVisualChildrenChanged(added,removed);UpdateColumns();}
    private void UpdateColumns()
    {
        if(ColumnDefinitions.Count<2)return;bool first=GetValueFirst(this);
        ColumnDefinitions[0].Width=first?new GridLength(1,GridUnitType.Star):new GridLength(126);
        ColumnDefinitions[1].Width=first?new GridLength(126):new GridLength(1,GridUnitType.Star);
        if(Children.Count>0){SetColumn(Children[0],first?1:0);if(Children[0] is FrameworkElement label)label.Margin=first?new Thickness(8,0,0,0):new Thickness(0,0,8,0);}
        if(Children.Count>1){SetColumn(Children[1],first?0:1);if(Children[1] is OutlinedTextBlock value)value.TextAlignment=first?TextAlignment.Right:TextAlignment.Left;}
    }
}

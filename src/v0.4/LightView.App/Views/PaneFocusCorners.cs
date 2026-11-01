using System.Windows;
using System.Windows.Media;

namespace LightView.App.Views;

public sealed class PaneFocusCorners : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty=DependencyProperty.Register(nameof(IsActive),typeof(bool),typeof(PaneFocusCorners),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsActive{get=>(bool)GetValue(IsActiveProperty);set=>SetValue(IsActiveProperty,value);}
    public PaneFocusCorners(){IsHitTestVisible=false;Focusable=false;}
    protected override void OnRender(DrawingContext dc)
    {
        if(!IsActive||ActualWidth<20||ActualHeight<20)return;
        var pen=new Pen(TryFindResource("AccentBrush") as Brush??Brushes.DeepSkyBlue,2.5);
        double l=5,t=5,r=ActualWidth-5,b=ActualHeight-5,n=20;
        foreach(var line in new[]{(new Point(l,t+n),new Point(l,t)),(new Point(l,t),new Point(l+n,t)),
            (new Point(r-n,t),new Point(r,t)),(new Point(r,t),new Point(r,t+n)),
            (new Point(l,b-n),new Point(l,b)),(new Point(l,b),new Point(l+n,b)),
            (new Point(r-n,b),new Point(r,b)),(new Point(r,b),new Point(r,b-n))})dc.DrawLine(pen,line.Item1,line.Item2);
    }
}

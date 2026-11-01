using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Views;

namespace LightView.Tests;

internal static class OutlinedTextTests
{
    public static (string Name, Action Test)[] Cases =>
    [
        ("white text gains actual black outline without painting its background", PixelOutline),
        ("outlined details wrap update and tolerate empty text", WrapAndUpdate)
    ];
    private static void Check(bool ok,string why) { if(!ok)throw new InvalidOperationException(why); }
    private static (byte[] Pixels,int Width,int Height) Render(OutlinedTextBlock text,double width=280,Brush? background=null)
    {
        text.Measure(new Size(width,600));double height=Math.Ceiling(text.DesiredSize.Height);
        text.Arrange(new Rect(0,0,width,Math.Max(1,height)));text.UpdateLayout();
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {dc.DrawRectangle(background??Brushes.White,null,new Rect(0,0,width+12,height+12));dc.PushTransform(new TranslateTransform(6,6));dc.DrawRectangle(new VisualBrush(text),null,new Rect(text.RenderSize));dc.Pop();}
        var bitmap=new RenderTargetBitmap((int)width+12,(int)height+12,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        byte[] pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
        return(pixels,bitmap.PixelWidth,bitmap.PixelHeight);
    }
    private static int Black(byte[] p) { int n=0;for(int i=0;i<p.Length;i+=4)if(p[i]<8&&p[i+1]<8&&p[i+2]<8)n++;return n; }
    private static void PixelOutline()
    {
        var text=new OutlinedTextBlock{Text="焦距 35mm（等效52mm）",FontSize=24,Foreground=Brushes.White};
        var thick=Render(text);Check(Black(thick.Pixels)>100,"No solid black stroke on white background");
        Check(thick.Pixels[0]==255&&thick.Pixels[1]==255&&thick.Pixels[2]==255,"Outline filled the panel background");
        text.OutlineThickness=0;var plain=Render(text);Check(Black(plain.Pixels)==0,"Zero outline still draws black");
        text.OutlineThickness=1.2;Check(Black(Render(text).Pixels)>100,"Runtime outline update failed");
        var red=Render(text,280,Brushes.Red);int corner=(10*red.Width+284)*4;
        Check(red.Pixels[corner]==0&&red.Pixels[corner+1]==0&&red.Pixels[corner+2]==255,"Details text painted an opaque background");
        var dark=Render(text,280,Brushes.Black);int white=0;
        for(int i=0;i<dark.Pixels.Length;i+=4)if(dark.Pixels[i]>245&&dark.Pixels[i+1]>245&&dark.Pixels[i+2]>245)white++;
        Check(white>20,"Stroke ate the white glyph face");
    }
    private static void WrapAndUpdate()
    {
        var text=new OutlinedTextBlock{Text="NIKKOR Z DX 16-50mm f/3.5-6.3 VR 路径信息",FontSize=16,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap};
        var wide=Render(text,400);var narrow=Render(text,100);Check(narrow.Height>wide.Height,"Outline lost wrapping");
        text.Text="更新";var updated=Render(text,100);Check(updated.Height<narrow.Height&&Black(updated.Pixels)>0,"Text update not rendered");
        text.Text="";Check(Black(Render(text,100).Pixels)==0,"Empty text kept old outline");
    }
}

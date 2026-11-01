using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LightView.App.Imaging;

namespace LightView.App.Views;

public sealed class HistogramView : FrameworkElement
{
    private DisplayHistogram? data;
    private bool preview;
    internal DisplayHistogram? Data => data;
    internal bool IsPreview => preview;
    public HistogramView()
    { Width = 220; Height = 180; IsHitTestVisible = false; Focusable = false; ToolTip = "基于当前显示图像；明度为加权RGB亮度，不代表RAW曝光"; }
    internal void SetData(DisplayHistogram? value, bool isPreview)
    { data = value; preview = isPreview; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double width = ActualWidth, height = ActualHeight; if (width < 30 || height < 40) return;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(185, 28, 30, 34)), null, new Rect(0, 0, width, height), 4, 4);
        var face = new Typeface("Microsoft YaHei UI"); double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string text, double x, double y, double size = 11)
        { dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.LightGray, dpi), new Point(x, y)); }
        Text("直方图", 9, 6); if (preview) Text("预览", Math.Max(9, width - 34), 7, 10);
        Text("明度", 9, 24, 10); Text("RGB", 9, 101, 10);
        const double left = 9; double right = width - 9, bottom = 159, top = 118;
        Text("0", left, height - 16, 10); Text("255", width - 27, height - 16, 10);
        if (data is null || data.Pixels == 0) return;
        long maximum = Math.Max(data.Red.Max(), Math.Max(data.Green.Max(), data.Blue.Max())); if (maximum == 0) return;
        foreach (var series in new[] { (data.Red, Color.FromArgb(210, 245, 104, 101)), (data.Green, Color.FromArgb(210, 104, 207, 128)), (data.Blue, Color.FromArgb(210, 103, 158, 246)) })
        {
            var path = new StreamGeometry();
            using (var points = path.Open())
            {
                for (int i = 0; i < 256; i++)
                {
                    var point = new Point(left + (right - left) * i / 255, bottom - (bottom - top) * series.Item1[i] / maximum);
                    if (i == 0) points.BeginFigure(point, false, false); else points.LineTo(point, true, false);
                }
            }
            path.Freeze(); dc.DrawGeometry(null, new Pen(new SolidColorBrush(series.Item2), 1), path);
        }
        long lightMaximum = data.Lightness.Max();
        if (lightMaximum > 0)
        {
            var path = new StreamGeometry();
            using (var points = path.Open())
                for (int i = 0; i < 256; i++)
                {
                    var point = new Point(left + (right - left) * i / 255, 92 - 52d * data.Lightness[i] / lightMaximum);
                    if (i == 0) points.BeginFigure(point, false, false); else points.LineTo(point, true, false);
                }
            path.Freeze(); dc.DrawGeometry(null, new Pen(Brushes.LightGray, 1.1), path);
        }
    }
}

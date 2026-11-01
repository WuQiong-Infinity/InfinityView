using LightView.Core.Viewing;

namespace LightView.Tests;

internal static class ViewportTests
{
    public static (string Name, Action Test)[] Cases =>
    [
        ("100 percent means one physical pixel at 150 percent DPI", PhysicalPixels),
        ("pointer actual pixels preserves the image coordinate at all zooms and DPI", PointerActualPixels),
        ("zoom preserves cursor image coordinate", CursorAnchor),
        ("small image freely pans and leaves automatic fit mode", FreeSmallImage),
        ("large image freely pans past viewport edges", FreeLargeImage),
        ("resize in fit mode recomputes scale", FitResize),
        ("manual zoom survives a DPI change", DpiChange),
        ("portrait landscape and tiny zoom retain pointer before filling viewport", AllSizesAnchor),
        ("zoom out and blank margin anchors are never recentered or clamped", MarginAnchor),
        ("zoom limits preserve the actual pointer coordinate", LimitAnchor),
        ("fit and actual pixels explicitly recenter after pointer zoom", ResetCenter),
        ("tiny drag after off-center zoom never recenters or snaps", ZoomThenDrag),
        ("manual canvas resize and DPI preserve the center image coordinate", FreeResize),
        ("zero invalid or empty canvas pan does not change the view", IgnoreInvalidPan)
    ];

    private static ViewportTransform Create()
    {
        var state = new ViewportTransform();
        state.SetImage(2000, 1000);
        state.Resize(1000, 500, 1.5);
        return state;
    }
    private static void Near(double a, double b)
    {
        if (Math.Abs(a - b) > 0.000001) throw new InvalidOperationException($"Expected {b}, got {a}.");
    }
    private static void PhysicalPixels()
    {
        var state = Create(); state.ActualPixels();
        Near(state.DipPerPixel * 1.5, 1);
    }
    private static void CursorAnchor()
    {
        var state = Create();
        double imageX = (450 - state.OffsetX) / state.DipPerPixel;
        double imageY = (220 - state.OffsetY) / state.DipPerPixel;
        state.ZoomAt(450, 220, 2);
        Near((450 - state.OffsetX) / state.DipPerPixel, imageX);
        Near((220 - state.OffsetY) / state.DipPerPixel, imageY);
    }
    private static void PointerActualPixels()
    {
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        foreach (var size in new[] { (600, 1800), (2400, 1200), (100, 80) })
        foreach (double factor in new[] { .1, .8, 4.0 })
        {
            var state = new ViewportTransform();state.SetImage(size.Item1,size.Item2);state.Resize(1600,900,dpi);
            state.ZoomAt(700,300,factor);state.Pan(55,-80);
            foreach(var point in new[] { (15.0,20.0),(700.0,300.0) })
            {
                double ix=(point.Item1-state.OffsetX)/state.DipPerPixel,iy=(point.Item2-state.OffsetY)/state.DipPerPixel;
                state.ActualPixelsAt(point.Item1,point.Item2);
                Near(state.Zoom,1);Near(state.DipPerPixel*dpi,1);
                Near((point.Item1-state.OffsetX)/state.DipPerPixel,ix);Near((point.Item2-state.OffsetY)/state.DipPerPixel,iy);
                if(state.IsFit)throw new InvalidOperationException("Pointer 100% retained automatic fit");
                double x=state.OffsetX,y=state.OffsetY;state.ActualPixelsAt(point.Item1,point.Item2);Near(state.OffsetX,x);Near(state.OffsetY,y);
            }
        }
        var empty=new ViewportTransform();empty.Resize(1000,600,1);empty.ActualPixelsAt(30,40);
        if(!empty.IsFit)throw new InvalidOperationException("Empty 100% changed state");
        var valid=Create();double ox=valid.OffsetX,oy=valid.OffsetY;valid.ActualPixelsAt(double.NaN,30);valid.ActualPixelsAt(30,double.PositiveInfinity);
        Near(valid.OffsetX,ox);Near(valid.OffsetY,oy);
    }
    private static void FreeSmallImage()
    {
        var state = Create(); state.SetImage(100, 100); double x = state.OffsetX, y = state.OffsetY;
        state.Pan(100000, -100000);
        Near(state.OffsetX, x + 100000); Near(state.OffsetY, y - 100000);
        if (state.IsFit) throw new InvalidOperationException("Dragging left automatic fit active.");
        state.Resize(state.ViewWidth, state.ViewHeight, state.DpiScale);
        Near(state.OffsetX, x + 100000); Near(state.OffsetY, y - 100000);
    }
    private static void FreeLargeImage()
    {
        var state = Create(); state.ZoomAt(500, 250, 4);
        double x = state.OffsetX, y = state.OffsetY; state.Pan(100000, -100000);
        Near(state.OffsetX, x + 100000); Near(state.OffsetY, y - 100000);
    }
    private static void FitResize()
    {
        var state = Create(); state.Resize(500, 250, 1);
        Near(state.Zoom, 0.25);
    }
    private static void DpiChange()
    {
        var state = Create(); state.ActualPixels(); state.Resize(1000, 500, 2);
        Near(state.Zoom, 1); Near(state.DipPerPixel, 0.5);
    }
    private static void Anchor(ViewportTransform state, double x, double y, double factor)
    {
        double imageX = (x - state.OffsetX) / state.DipPerPixel;
        double imageY = (y - state.OffsetY) / state.DipPerPixel;
        state.ZoomAt(x, y, factor);
        Near((x - state.OffsetX) / state.DipPerPixel, imageX);
        Near((y - state.OffsetY) / state.DipPerPixel, imageY);
    }
    private static void AllSizesAnchor()
    {
        foreach (var (width, height) in new[] { (600, 1800), (2400, 1200), (100, 80) })
        foreach (double dpi in new[] { 1.0, 1.5 })
        {
            var state = new ViewportTransform(); state.SetImage(width, height); state.Resize(1600, 900, dpi);
            foreach (double factor in new[] { 1.2, 1.2, .8, .5, 1.1 }) Anchor(state, 830, 340, factor);
        }
    }
    private static void MarginAnchor()
    {
        var state = new ViewportTransform(); state.SetImage(600, 1800); state.Resize(1600, 900, 1);
        foreach (double factor in new[] { .6, 1.2, .8, 4.0, .1 }) Anchor(state, 15, 20, factor);
    }
    private static void LimitAnchor()
    {
        var state = Create(); Anchor(state, 25, 35, 1000); Near(state.Zoom, 32);
        Anchor(state, 25, 35, 2); Near(state.Zoom, 32);
        Anchor(state, 25, 35, 0.000001); Near(state.Zoom, .01);
        Anchor(state, 25, 35, .1); Near(state.Zoom, .01);
    }
    private static void ResetCenter()
    {
        var state = Create(); Anchor(state, 20, 30, .2); state.Pan(100000, -100000); state.Fit();
        Near(state.OffsetX, (state.ViewWidth - state.ImageWidth * state.DipPerPixel) / 2);
        Near(state.OffsetY, (state.ViewHeight - state.ImageHeight * state.DipPerPixel) / 2);
        Anchor(state, 20, 30, 1.2); state.Pan(-100000, 100000); state.ActualPixels();
        Near(state.Zoom, 1);
        Near(state.OffsetX, (state.ViewWidth - state.ImageWidth * state.DipPerPixel) / 2);
        Near(state.OffsetY, (state.ViewHeight - state.ImageHeight * state.DipPerPixel) / 2);
    }
    private static void ZoomThenDrag()
    {
        foreach (var (width, height) in new[] { (600, 1800), (2400, 1200), (100, 80) })
        {
            var state = new ViewportTransform(); state.SetImage(width, height); state.Resize(1600, 900, 1.5);
            state.ZoomAt(900, 250, 1.2);
            double x = state.OffsetX, y = state.OffsetY;
            state.Pan(0, 0); Near(state.OffsetX, x); Near(state.OffsetY, y);
            state.Pan(1, -1); Near(state.OffsetX, x + 1); Near(state.OffsetY, y - 1);
            state.Pan(-1, 1); Near(state.OffsetX, x); Near(state.OffsetY, y);
            Anchor(state, 900, 250, .5);
        }
    }
    private static void FreeResize()
    {
        var state = Create(); state.SetImage(100, 100); state.Pan(5000, -7000);
        double centerX = (state.ViewWidth / 2 - state.OffsetX) / state.DipPerPixel;
        double centerY = (state.ViewHeight / 2 - state.OffsetY) / state.DipPerPixel;
        double zoom = state.Zoom; state.Resize(1400, 800, 2);
        Near(state.Zoom, zoom);
        Near((state.ViewWidth / 2 - state.OffsetX) / state.DipPerPixel, centerX);
        Near((state.ViewHeight / 2 - state.OffsetY) / state.DipPerPixel, centerY);
    }
    private static void IgnoreInvalidPan()
    {
        var state = Create(); double x = state.OffsetX, y = state.OffsetY;
        state.Pan(0, 0); state.Pan(double.NaN, 3); state.Pan(3, double.PositiveInfinity);
        Near(state.OffsetX, x); Near(state.OffsetY, y);
        if (!state.IsFit) throw new InvalidOperationException("Ignored pan exited fit mode.");
        state.SetImage(0, 0); x = state.OffsetX; y = state.OffsetY; state.Pan(20, 30);
        Near(state.OffsetX, x); Near(state.OffsetY, y);
    }
}

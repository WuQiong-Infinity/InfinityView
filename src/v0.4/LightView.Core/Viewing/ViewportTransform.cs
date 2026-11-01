namespace LightView.Core.Viewing;

public sealed class ViewportTransform
{
    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }
    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }
    public double DpiScale { get; private set; } = 1;
    public double Zoom { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public bool IsFit { get; private set; } = true;
    public double DipPerPixel => Zoom / DpiScale;
    public bool HasImage => ImageWidth > 0 && ImageHeight > 0;

    public void SetImage(int width, int height, bool keepView = false)
    {
        bool restore = keepView && HasImage && !IsFit && ViewWidth > 0 && ViewHeight > 0 &&
            FrameworkApi.IsFinite(Zoom) && Zoom > 0 && FrameworkApi.IsFinite(OffsetX) && FrameworkApi.IsFinite(OffsetY);
        double u = restore ? FrameworkApi.Clamp((ViewWidth / 2 - OffsetX) / (DipPerPixel * ImageWidth), 0, 1) : .5;
        double v = restore ? FrameworkApi.Clamp((ViewHeight / 2 - OffsetY) / (DipPerPixel * ImageHeight), 0, 1) : .5;
        double previousZoom = Zoom;
        ImageWidth = Math.Max(0, width);
        ImageHeight = Math.Max(0, height);
        if (!restore || !HasImage || !FrameworkApi.IsFinite(u) || !FrameworkApi.IsFinite(v)) { Fit(); return; }
        double minimum = Math.Min(0.01, Math.Min(ViewWidth * DpiScale / ImageWidth, ViewHeight * DpiScale / ImageHeight));
        Zoom = FrameworkApi.Clamp(previousZoom, Math.Max(0.000001, minimum), 32);
        IsFit = false;
        double renderedWidth = ImageWidth * DipPerPixel, renderedHeight = ImageHeight * DipPerPixel;
        // Clamp only a cross-image restore. Ordinary pan/zoom/resize stay free.
        OffsetX = renderedWidth <= ViewWidth ? (ViewWidth - renderedWidth) / 2
            : FrameworkApi.Clamp(ViewWidth / 2 - u * renderedWidth, ViewWidth - renderedWidth, 0);
        OffsetY = renderedHeight <= ViewHeight ? (ViewHeight - renderedHeight) / 2
            : FrameworkApi.Clamp(ViewHeight / 2 - v * renderedHeight, ViewHeight - renderedHeight, 0);
    }

    public void Resize(double width, double height, double dpiScale)
    {
        if (!FrameworkApi.IsFinite(width) || !FrameworkApi.IsFinite(height) ||
            !FrameworkApi.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        double centerX = (ViewWidth / 2 - OffsetX) / DipPerPixel;
        double centerY = (ViewHeight / 2 - OffsetY) / DipPerPixel;
        ViewWidth = Math.Max(0, width);
        ViewHeight = Math.Max(0, height);
        DpiScale = dpiScale;
        if (IsFit) Fit();
        else
        {
            OffsetX = ViewWidth / 2 - centerX * DipPerPixel;
            OffsetY = ViewHeight / 2 - centerY * DipPerPixel;
        }
    }

    public void Fit()
    {
        IsFit = true;
        Zoom = HasImage && ViewWidth > 0 && ViewHeight > 0
            ? Math.Min(1, Math.Min(ViewWidth * DpiScale / ImageWidth, ViewHeight * DpiScale / ImageHeight)) : 1;
        Center();
    }

    public void ActualPixels()
    {
        IsFit = false;
        Zoom = 1;
        Center();
    }

    public void ActualPixelsAt(double x, double y)
    {
        if (!HasImage || !FrameworkApi.IsFinite(x) || !FrameworkApi.IsFinite(y)) return;
        double imageX = (x - OffsetX) / DipPerPixel;
        double imageY = (y - OffsetY) / DipPerPixel;
        Zoom = 1;
        IsFit = false;
        OffsetX = x - imageX * DipPerPixel;
        OffsetY = y - imageY * DipPerPixel;
    }

    public void ZoomAt(double x, double y, double factor)
    {
        if (!HasImage || !FrameworkApi.IsFinite(factor) || factor <= 0) return;
        double imageX = (x - OffsetX) / DipPerPixel;
        double imageY = (y - OffsetY) / DipPerPixel;
        double minimum = Math.Min(0.01, Math.Min(ViewWidth * DpiScale / ImageWidth, ViewHeight * DpiScale / ImageHeight));
        Zoom = FrameworkApi.Clamp(Zoom * factor, Math.Max(0.000001, minimum), 32);
        IsFit = false;
        OffsetX = x - imageX * DipPerPixel;
        OffsetY = y - imageY * DipPerPixel;
        // Re-centering a short axis or clamping a long one here would move
        // the image coordinate under the pointer. Fit/100% remain explicit resets.
    }

    public void Pan(double dx, double dy)
    {
        if (!HasImage || !FrameworkApi.IsFinite(dx) || !FrameworkApi.IsFinite(dy) || dx == 0 && dy == 0) return;
        IsFit = false;
        OffsetX += dx;
        OffsetY += dy;
    }

    public void FreezeFit() { if (HasImage) IsFit = false; }

    public void Follow(ViewportTransform master)
    {
        if (!HasImage || !master.HasImage || ViewWidth <= 0 || ViewHeight <= 0 || master.ViewWidth <= 0 || master.ViewHeight <= 0) return;
        if (master.IsFit) { Fit(); return; }
        SetNormalized(master.Zoom,
            (master.ViewWidth / 2 - master.OffsetX) / (master.DipPerPixel * master.ImageWidth),
            (master.ViewHeight / 2 - master.OffsetY) / (master.DipPerPixel * master.ImageHeight));
    }
    public void SetNormalized(double zoom, double u, double v)
    {
        if (!HasImage || !FrameworkApi.IsFinite(zoom) || zoom <= 0 || !FrameworkApi.IsFinite(u) || !FrameworkApi.IsFinite(v)) return;
        double minimum = Math.Min(0.01, Math.Min(ViewWidth * DpiScale / ImageWidth, ViewHeight * DpiScale / ImageHeight));
        Zoom = FrameworkApi.Clamp(zoom, Math.Max(0.000001, minimum), 32); IsFit = false;
        OffsetX = ViewWidth / 2 - u * ImageWidth * DipPerPixel;
        OffsetY = ViewHeight / 2 - v * ImageHeight * DipPerPixel;
    }

    private void Center()
    {
        OffsetX = (ViewWidth - ImageWidth * DipPerPixel) / 2;
        OffsetY = (ViewHeight - ImageHeight * DipPerPixel) / 2;
    }

}

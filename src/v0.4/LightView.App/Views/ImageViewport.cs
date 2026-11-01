using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.Core.Viewing;
using LightView.App.Imaging;

namespace LightView.App.Views;

public sealed class ImageViewport : FrameworkElement
{
    private static readonly Brush CheckerBrush = CreateCheckerBrush();
    public static readonly DependencyProperty TransparencyCheckerboardProperty = DependencyProperty.Register(
        nameof(TransparencyCheckerboard), typeof(bool), typeof(ImageViewport), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool TransparencyCheckerboard { get => (bool)GetValue(TransparencyCheckerboardProperty); set => SetValue(TransparencyCheckerboardProperty, value); }
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(LoadedImage), typeof(ImageViewport), new PropertyMetadata(null, OnImageChanged));
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(ImageViewport),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ImageViewport),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(16, 16, 16)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly CanvasPointerGesture pointer = new();
    private MouseButton? pressedButton;
    private bool pressedForPan;
    public bool EdgeNavigationEnabled { get; set; } = true;
    public string MouseNavigationRegion { get; set; } = "halves";
    public string MousePanButton { get; set; } = "right";
    public bool KeepViewOnImageChange { get; set; }
    public bool IsReadOnly { get; set; }
    public event Action<int>? EdgeClicked;
    private bool preserveSourceTransform;
    public LoadedImage? Image
    {
        get => (LoadedImage?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }
    public ViewportTransform Transform { get; } = new();
    public event EventHandler? ViewChanged;
    public event EventHandler? UserViewChanged;
    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public ImageViewport()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public void Fit() { Transform.Fit(); GestureChanged(); }
    public void ActualPixels() { Transform.ActualPixels(); GestureChanged(); }
    public void ActualPixelsAt(Point point) { Transform.ActualPixelsAt(point.X, point.Y); GestureChanged(); }
    public void ZoomAt(Point point, double factor)
    { Transform.ZoomAt(point.X, point.Y, factor); GestureChanged(); }
    internal void RefreshGeometry() => UpdateGeometry();
    internal void Follow(ImageViewport master) { Transform.Follow(master.Transform); Changed(); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Background, null,
            new Rect(RenderSize));
        if (Source is null) return;
        double x = Transform.OffsetX, y = Transform.OffsetY;
        if (Math.Abs(Transform.Zoom - 1) < 0.000001)
        {
            x = Math.Round(x * Transform.DpiScale) / Transform.DpiScale;
            y = Math.Round(y * Transform.DpiScale) / Transform.DpiScale;
        }
        var imageBounds = new Rect(x, y, Transform.ImageWidth * Transform.DipPerPixel, Transform.ImageHeight * Transform.DipPerPixel);
        if (TransparencyCheckerboard) drawingContext.DrawRectangle(CheckerBrush, null, imageBounds);
        drawingContext.DrawImage(Source, imageBounds);
    }

    private static Brush CreateCheckerBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(56, 56, 56)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var squares = new GeometryGroup(); squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8))); squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(80, 80, 80)), null, squares));
        var brush = new DrawingBrush(group) { Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateGeometry();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateGeometry();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => BeginPointer(e, MousePanButton == "left");
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (MousePanButton == "right") BeginPointer(e, true);
    }
    private void BeginPointer(MouseButtonEventArgs e, bool pan)
    {
        if (Source is null || IsReadOnly) return;
        if (pressedButton is not null) { ReleaseMouseCapture(); EndDrag(); e.Handled = true; return; }
        Focus();
        var point = e.GetPosition(this);
        pointer.Begin(point.X, point.Y);
        pressedButton = e.ChangedButton; pressedForPan = pan;
        if (CaptureMouse()) { if (pan) Cursor = Cursors.Hand; } else EndDrag();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!pointer.IsPressed || !IsMouseCaptured) return;
        Point point = e.GetPosition(this);
        var delta = pointer.Move(point.X, point.Y, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
        if (pressedForPan && (delta.X != 0 || delta.Y != 0)) { Transform.Pan(delta.X, delta.Y); GestureChanged(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndPointer(e);
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => EndPointer(e);
    private void EndPointer(MouseButtonEventArgs e)
    {
        if (!pointer.IsPressed || pressedButton != e.ChangedButton) return;
        Point point = e.GetPosition(this);
        int direction = pointer.Release(point.X, point.Y, ActualWidth, ActualHeight,
            SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance, MouseNavigationRegion == "halves");
        ReleaseMouseCapture();
        EndDrag();
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Left && EdgeNavigationEnabled && Keyboard.Modifiers == ModifierKeys.None && direction != 0) EdgeClicked?.Invoke(direction);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        EndDrag();
        base.OnLostMouseCapture(e);
    }

    protected override void OnContextMenuOpening(System.Windows.Controls.ContextMenuEventArgs e)
    { if (MousePanButton == "right") e.Handled = true; else base.OnContextMenuOpening(e); }
    private void EndDrag() { pointer.Cancel(); pressedButton = null; pressedForPan = false; Cursor = Cursors.Arrow; }
    private void UpdateGeometry()
    {
        Transform.Resize(RenderSize.Width, RenderSize.Height, VisualTreeHelper.GetDpi(this).DpiScaleX);
        Changed();
    }
    private void GestureChanged() { Changed(); UserViewChanged?.Invoke(this, EventArgs.Empty); }
    private void Changed() { InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty); }
    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var viewport = (ImageViewport)dependencyObject;
        var source = (BitmapSource?)e.NewValue;
        if (!viewport.preserveSourceTransform)
            viewport.Transform.SetImage(viewport.Image?.PixelWidth ?? source?.PixelWidth ?? 0, viewport.Image?.PixelHeight ?? source?.PixelHeight ?? 0);
        viewport.UpdateGeometry();
    }
    private static void OnImageChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var viewport = (ImageViewport)dependencyObject;
        var previous = (LoadedImage?)e.OldValue;
        var current = (LoadedImage?)e.NewValue;
        RenderOptions.SetBitmapScalingMode(viewport, current?.IsPreview == true ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality);
        bool sameImage = previous is not null && current is not null &&
            (StringComparer.OrdinalIgnoreCase.Equals(previous.FilePath, current.FilePath) || ReferenceEquals(previous.Source, current.Source)) &&
            previous.PixelWidth == current.PixelWidth && previous.PixelHeight == current.PixelHeight;
        if (!sameImage)
        {
            // Layout can have completed before its size-change notification.
            // Capture/restore against the current viewport, not stale zero geometry.
            viewport.Transform.Resize(viewport.RenderSize.Width, viewport.RenderSize.Height,
                VisualTreeHelper.GetDpi(viewport).DpiScaleX);
            viewport.Transform.SetImage(current?.PixelWidth ?? 0, current?.PixelHeight ?? 0,
                viewport.KeepViewOnImageChange && current?.ViewIntent == ImageViewIntent.Adjacent && previous is not null);
        }
        viewport.preserveSourceTransform = true;
        bool sameSource = ReferenceEquals(viewport.Source, current?.Source);
        viewport.Source = current?.Source;
        if (sameSource) viewport.UpdateGeometry();
        viewport.preserveSourceTransform = false;
    }
}

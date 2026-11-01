using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace LightView.App.Views;

// Plain, noninteractive details text. One shared glyph geometry supplies both
// border and face, without softening the outline through a bitmap effect.
public sealed class OutlinedTextBlock : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextWrapping.NoWrap, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutlineThicknessProperty = DependencyProperty.Register(
        nameof(OutlineThickness), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(1.2, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double size && FrameworkApi.IsFinite(size) && size >= 0 && size <= 8);

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(nameof(TextTrimming), typeof(TextTrimming), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(TextTrimming.None, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public TextTrimming TextTrimming { get => (TextTrimming)GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }
    public OutlinedTextBlock() { Focusable = false; IsTabStop = false; }
    public string Text { get => (string?)GetValue(TextProperty) ?? ""; set => SetValue(TextProperty, value); }
    public TextWrapping TextWrapping { get => (TextWrapping)GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    public Brush OutlineBrush
    {
        get => (Brush)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }
    // Visible expansion outside the glyph; the pen is twice this width.
    public double OutlineThickness
    {
        get => (double)GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    private FormattedText Format(double width)
    {
        var formatted = new FormattedText(Text, Language.GetSpecificCulture(), FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment
        };
        if (TextWrapping != TextWrapping.NoWrap && FrameworkApi.IsFinite(width))
            formatted.MaxTextWidth = Math.Max(.001, width - Padding.Left - Padding.Right);
        if (TextTrimming != TextTrimming.None && FrameworkApi.IsFinite(width))
        { formatted.MaxTextWidth = Math.Max(.001, width - Padding.Left - Padding.Right); formatted.MaxLineCount = 1; formatted.Trimming = TextTrimming; }
        return formatted;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var formatted = Format(constraint.Width);
        return new Size(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + Padding.Left + Padding.Right),
            Math.Ceiling(formatted.Height + Padding.Top + Padding.Bottom));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Background is not null) drawingContext.DrawRectangle(Background, null, new Rect(RenderSize));
        if (FrameworkApi.IsNullOrEmpty(Text) || ActualWidth <= 0 || ActualHeight <= 0) return;
        var formatted = Format(ActualWidth);
        Geometry glyphs = formatted.BuildGeometry(new Point(Padding.Left, Padding.Top));
        if (OutlineThickness > 0)
        {
            var pen = new Pen(OutlineBrush, OutlineThickness * 2) { LineJoin = PenLineJoin.Round };
            drawingContext.DrawGeometry(null, pen, glyphs);
        }
        // Fill after stroking so the inward half of the pen never eats the face.
        drawingContext.DrawGeometry(Foreground, null, glyphs);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DetailsTextPeer(this);
    private sealed class DetailsTextPeer(OutlinedTextBlock owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Text;
        protected override string GetClassNameCore() => nameof(OutlinedTextBlock);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}

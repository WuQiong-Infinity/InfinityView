using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LightView.Core.Settings;

namespace LightView.App.Views.Settings;

public partial class AppearancePage : UserControl
{
    private bool loading = true;
    private AppearanceSettings value = new();
    public event Action<AppearanceSettings>? Changed;
    public AppearancePage() { InitializeComponent(); Load(value); SetPreviewBackground("sample"); }
    public void Load(AppearanceSettings settings)
    {
        loading = true; value = settings.Clone();
        CanvasChoice.SelectedIndex = settings.CanvasBackground == "midGray" ? 1 : settings.CanvasBackground == "black" ? 2 : 0;
        CheckerChoice.IsChecked = settings.TransparencyCheckerboard;
        FontSlider.Value = settings.DetailsFontSize; OutlineSlider.Value = settings.DetailsOutlineThickness;
        GapSlider.Value = settings.DetailsLineGap; WidthSlider.Value = settings.DetailsWidth;
        FitChoice.IsChecked = settings.DetailsFitSmallWindow; MotionChoice.IsChecked = settings.ReduceMotion;
        loading = false; UpdatePreview();
    }
    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        value.CanvasBackground = (CanvasChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "darkGray";
        value.TransparencyCheckerboard = CheckerChoice.IsChecked == true;
        value.DetailsFontSize = FontSlider.Value; value.DetailsOutlineThickness = Math.Round(OutlineSlider.Value, 1);
        value.DetailsLineGap = GapSlider.Value; value.DetailsWidth = WidthSlider.Value;
        value.DetailsFitSmallWindow = FitChoice.IsChecked == true; value.ReduceMotion = MotionChoice.IsChecked == true;
        UpdatePreview(); Changed?.Invoke(value.Clone());
    }
    private void UpdatePreview()
    {
        FontReadout.Text = value.DetailsFontSize.ToString("0"); OutlineReadout.Text = value.DetailsOutlineThickness.ToString("0.0");
        GapReadout.Text = value.DetailsLineGap.ToString("0"); WidthReadout.Text = value.DetailsWidth.ToString("0");
        PreviewTitle.FontSize = value.DetailsFontSize * 22 / 16;
        PreviewSection.FontSize = value.DetailsFontSize * 17 / 16;
        foreach (var text in new[] { PreviewTitle, PreviewSection, PreviewName, PreviewPixels, PreviewFocal }) text.OutlineThickness = value.DetailsOutlineThickness;
        foreach (var text in new[] { PreviewName, PreviewPixels, PreviewFocal })
        { text.FontSize = value.DetailsFontSize; text.Margin = new Thickness(0, value.DetailsLineGap / 2, 0, value.DetailsLineGap / 2); }
        PreviewText.Width = value.DetailsWidth - 16;
        PreviewFit.Stretch = value.DetailsFitSmallWindow ? Stretch.Uniform : Stretch.None;
    }
    private void OnPreviewBackground(object sender, RoutedEventArgs e) => SetPreviewBackground((sender as Button)?.Tag as string ?? "sample");
    private void SetPreviewBackground(string kind)
    {
        if (kind != "sample") { PreviewBackground.Background = kind == "white" ? Brushes.White : Brushes.Black; return; }
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.DimGray, null, new RectangleGeometry(new Rect(0, 0, 320, 272))));
        drawing.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, Geometry.Parse("M0,180 L120,60 250,220 320,120 320,272 0,272 Z")));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, new EllipseGeometry(new Point(245, 60), 30, 30)));
        PreviewBackground.Background = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
    }
}

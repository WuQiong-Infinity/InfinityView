using LightView.App.Views;

namespace LightView.App.ViewModels;

internal sealed class PaneContext(ViewerViewModel viewer, ImageViewport viewport)
{
    public ViewerViewModel Viewer { get; } = viewer;
    public ImageViewport Viewport { get; } = viewport;
    public bool Attached { get; internal set; } = true;
    public bool DetailsVisible { get; set; }
    public bool HistogramVisible { get; set; }
    public bool ColorPending { get; set; }
    public int ColorGeneration { get; set; }
    public string Feedback { get; set; } = "";
    public bool FeedbackPersistent { get; set; }
    public bool CanOrganize => Attached && !ColorPending && Viewer.OrganizingImage is not null;
}

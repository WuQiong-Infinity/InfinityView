using System.Windows;
using System.Windows.Threading;

namespace LightView.App.Views;

public partial class MainWindow
{
    private readonly DispatcherTimer linkToastTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private void InitializeLinkToast()
    {
        linkToastTimer.Tick += (_, _) => ClearLinkToast();
        comparison.Changed += () => { if (!comparison.IsOn) ClearLinkToast(); };
        Closed += (_, _) => ClearLinkToast();
    }
    private void ShowLinkState(bool linked)
    {
        linkToastTimer.Stop();
        LinkToastText.Text = linked ? "联动已开启" : "联动已关闭";
        LinkToast.Visibility = Visibility.Visible;
        linkToastTimer.Start();
    }
    private void ClearLinkToast()
    { linkToastTimer.Stop(); LinkToast.Visibility = Visibility.Collapsed; LinkToastText.Text = ""; }
}

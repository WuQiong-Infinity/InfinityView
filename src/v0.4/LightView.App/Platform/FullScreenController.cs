using System.Windows;
using System.Windows.Media;

namespace LightView.App.Platform;

internal sealed class FullScreenController(Window window)
{
    private Rect previousBounds;
    private WindowState previousState;
    private WindowStyle previousStyle;
    private ResizeMode previousResizeMode;
    private bool previousTopmost;
    private double previousDpi;
    public bool IsFullScreen { get; private set; }
    public Rect NormalBounds => IsFullScreen ? previousBounds : window.WindowState == WindowState.Normal
        ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
    public bool NormalMaximized => (IsFullScreen ? previousState : window.WindowState) == WindowState.Maximized;
    public double NormalDpi => IsFullScreen ? previousDpi : VisualTreeHelper.GetDpi(window).DpiScaleX;

    public void Toggle()
    {
        if (IsFullScreen) { Exit(); return; }
        previousState = window.WindowState;
        previousBounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
        previousStyle = window.WindowStyle;
        previousResizeMode = window.ResizeMode;
        previousTopmost = window.Topmost;
        previousDpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowState = WindowState.Maximized;
        IsFullScreen = true;
    }

    public void Exit()
    {
        if (!IsFullScreen) return;
        window.WindowState = WindowState.Normal;
        window.WindowStyle = previousStyle;
        window.ResizeMode = previousResizeMode;
        window.Topmost = previousTopmost;
        window.Left = previousBounds.Left;
        window.Top = previousBounds.Top;
        window.Width = previousBounds.Width;
        window.Height = previousBounds.Height;
        window.WindowState = previousState;
        IsFullScreen = false;
    }
}

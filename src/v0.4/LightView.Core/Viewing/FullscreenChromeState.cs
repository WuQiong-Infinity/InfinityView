namespace LightView.Core.Viewing;

public sealed class FullscreenChromeState
{
    private DateTimeOffset? enteredEdge, leftChrome;
    public bool IsVisible { get; private set; } = true;
    public void Reset(bool fullscreen, bool autoHide)
    { IsVisible = !fullscreen || !autoHide; enteredEdge = leftChrome = null; }
    public bool Update(bool fullscreen, bool autoHide, bool atTopEdge, bool overChrome, bool hold, DateTimeOffset now, bool imageDrag = false)
    {
        if (fullscreen && autoHide && imageDrag)
        { IsVisible = false; enteredEdge = leftChrome = null; return false; }
        if (!fullscreen || !autoHide || overChrome || hold)
        { IsVisible = true; enteredEdge = leftChrome = null; return IsVisible; }
        if (atTopEdge)
        {
            enteredEdge ??= now; leftChrome = null;
            if (now - enteredEdge.Value >= TimeSpan.FromMilliseconds(120)) IsVisible = true;
        }
        else
        {
            enteredEdge = null;
            if (IsVisible)
            {
                leftChrome ??= now;
                if (now - leftChrome.Value >= TimeSpan.FromMilliseconds(800)) IsVisible = false;
            }
        }
        return IsVisible;
    }
}

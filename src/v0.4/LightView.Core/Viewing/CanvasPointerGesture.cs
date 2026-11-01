namespace LightView.Core.Viewing;

public sealed class CanvasPointerGesture
{
    private double startX, startY, lastX, lastY;
    public bool IsPressed { get; private set; }
    public bool IsDragging { get; private set; }
    public void Begin(double x, double y)
    { startX = lastX = x; startY = lastY = y; IsPressed = true; IsDragging = false; }
    public (double X, double Y) Move(double x, double y, double thresholdX, double thresholdY)
    {
        if (!IsPressed) return (0, 0);
        if (!IsDragging)
        {
            if (Math.Abs(x - startX) <= thresholdX && Math.Abs(y - startY) <= thresholdY) return (0, 0);
            IsDragging = true;
        }
        var delta = (x - lastX, y - lastY); lastX = x; lastY = y; return delta;
    }
    public int Release(double x, double y, double width, double height, double thresholdX, double thresholdY, bool halves = false)
    {
        bool click = IsPressed && !IsDragging && Math.Abs(x - startX) <= thresholdX && Math.Abs(y - startY) <= thresholdY;
        Cancel();
        if (!click || !FrameworkApi.IsFinite(x) || !FrameworkApi.IsFinite(y) || width <= 0 || height <= 0 || x < 0 || x > width || y < 0 || y > height) return 0;
        if (halves) return x < width / 2 ? -1 : 1;
        double band = Math.Min(96, width * .12);
        return x < band ? -1 : x > width - band ? 1 : 0;
    }
    public void Cancel() { IsPressed = false; IsDragging = false; }
}

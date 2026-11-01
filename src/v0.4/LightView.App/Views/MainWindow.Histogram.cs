using System.Windows;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.ViewModels;

namespace LightView.App.Views;

public partial class MainWindow
{
    private bool histogramPumping, histogramClosed;
    private long histogramOrder;
    private readonly HistogramCache histogramCache = new();
    private readonly Dictionary<PaneContext, HistogramSlot> histogramSlots = new();
    private HistogramRequest? histogramRunning;
    private CancellationTokenSource? histogramCancellation;
    private Task histogramWorker = Task.CompletedTask;
    internal int HistogramScanCount { get; private set; }
    internal Func<BitmapSource, CancellationToken, DisplayHistogram> HistogramCompute { get; set; } = HistogramCalculator.Calculate;
    private sealed class HistogramSlot(PaneContext pane)
    {
        internal readonly PaneContext Pane = pane;
        internal long Generation;
        internal HistogramCache.Key? Selection;
        internal HistogramRequest? Wanted;
    }
    private sealed class HistogramRequest(HistogramSlot slot, LoadedImage image, HistogramCache.Key key, long generation, long order, IDisposable lease)
    {
        internal readonly HistogramSlot Slot = slot;
        internal BitmapSource? Source = image.Source;
        internal readonly HistogramCache.Key Key = key;
        internal readonly long Generation = generation;
        internal readonly long Order = order;
        internal void Release() { lease.Dispose(); Source = null; }
    }
    private HistogramView HistogramFor(PaneContext pane) => ReferenceEquals(pane.Viewport, ReferenceViewport) ? LeftHistogram : RightHistogram;
    private HistogramSlot SlotFor(PaneContext pane)
    {
        if (!histogramSlots.TryGetValue(pane, out var slot)) histogramSlots[pane] = slot = new(pane);
        return slot;
    }
    private void InitializeHistogram()
    {
        comparison.Changed += () => RefreshHistogram();
        Closed += (_, _) => { histogramClosed = true; CancelHistogramRequest(); };
    }
    private void ToggleHistogram()
    { activePane.HistogramVisible = !activePane.HistogramVisible; RefreshHistogram(); }
    private void CancelHistogramSlot(HistogramSlot slot)
    {
        ++slot.Generation;
        if (ReferenceEquals(histogramRunning?.Slot, slot)) histogramCancellation?.Cancel();
        slot.Wanted?.Release(); slot.Wanted = null; slot.Selection = null;
        var chart = HistogramFor(slot.Pane); chart.SetData(null, false); chart.Visibility = Visibility.Collapsed;
    }
    private void CancelHistogramRequest()
    {
        foreach (var slot in histogramSlots.Values) CancelHistogramSlot(slot);
        LeftHistogram.SetData(null, false); RightHistogram.SetData(null, false);
        LeftHistogram.Visibility = RightHistogram.Visibility = Visibility.Collapsed;
        LayoutHistogram();
    }
    private async Task SuspendHistogramAsync()
    { CancelHistogramRequest(); await histogramWorker; }
    private void RefreshHistogram(bool force = false)
    {
        HistogramMenuItem.IsChecked = activePane.HistogramVisible;
        foreach (var pane in histogramSlots.Keys.Where(p => !p.Attached).ToArray())
        { CancelHistogramSlot(histogramSlots[pane]); histogramSlots.Remove(pane); }
        foreach (var pane in AllPanes)
        {
            var slot = SlotFor(pane); var image = pane.Viewer.DisplayedImage;
            if (histogramClosed || !pane.HistogramVisible || pane.ColorPending || image is null || image.DisplayTargetKey != colorTarget.Key)
            { CancelHistogramSlot(slot); continue; }
            var key = new HistogramCache.Key(ComparisonLoadPolicy.Identity(image), image.DisplayTargetKey, image.IsPreview);
            if (!(force && ReferenceEquals(pane, activePane)) && slot.Selection == key) continue;
            CancelHistogramSlot(slot); slot.Selection = key;
            var chart = HistogramFor(pane); chart.Visibility = Visibility.Visible; chart.SetData(null, image.IsPreview);
            if (histogramCache.TryGet(key, out var cached)) { chart.SetData(cached, image.IsPreview); continue; }
            slot.Wanted = new(slot, image, key, slot.Generation, ++histogramOrder, imageLoader.ComparisonPolicy.RetainSource(image));
        }
        LayoutHistogram();
        if (!histogramPumping && histogramSlots.Values.Any(s => s.Wanted is not null)) histogramWorker = PumpHistogramAsync();
    }
    private bool HistogramCurrent(HistogramRequest request)
    {
        var pane = request.Slot.Pane;
        return !histogramClosed && pane.HistogramVisible && request.Generation == request.Slot.Generation &&
            pane.Attached && !pane.ColorPending && pane.Viewer.DisplayedImage is { } image &&
            ReferenceEquals(image.Source, request.Source) && image.DisplayTargetKey == request.Key.Target && image.IsPreview == request.Key.Preview;
    }
    private async Task PumpHistogramAsync()
    {
        histogramPumping = true;
        try
        {
            while (true)
            {
                var slot = histogramSlots.Values.Where(s => s.Wanted is not null).OrderBy(s => s.Wanted!.Order).FirstOrDefault();
                if (slot?.Wanted is not { } request) break;
                slot.Wanted = null; histogramRunning = request;
                using var cancellation = new CancellationTokenSource(); histogramCancellation = cancellation;
                try
                {
                    var compute = HistogramCompute; ++HistogramScanCount;
                    var result = await Task.Run(() => compute(request.Source!, cancellation.Token));
                    histogramCache.Add(request.Key, result);
                    if (HistogramCurrent(request)) HistogramFor(slot.Pane).SetData(result, request.Key.Preview);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException or System.IO.IOException)
                { /* Optional statistics must not disrupt viewing or organizing. */ }
                finally
                {
                    if (ReferenceEquals(histogramCancellation, cancellation)) histogramCancellation = null;
                    histogramRunning = null; request.Release();
                }
            }
        }
        finally { histogramPumping = false; }
    }
    private void LayoutHistogram()
    {
        foreach (var pane in AllPanes)
        {
            var chart = HistogramFor(pane); var ui = Ui(pane);
            ui.Details.Margin = new Thickness(10, 10, 10, comparison.IsOn ? 36 : 10);
            bool detailsRight = comparison.IsOn && ReferenceEquals(comparison.Right, pane);
            chart.HorizontalAlignment = detailsRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            double available = Math.Max(0, pane.Viewport.ActualWidth - 20);
            chart.MaxWidth = ui.Details.Visibility == Visibility.Visible ? Math.Max(0, (available - 12) * .45) : available;
            double chartSpace = chart.Visibility == Visibility.Visible ? Math.Min(chart.Width, chart.MaxWidth) + 12 : 0;
            ui.Details.MaxWidth = Math.Max(0, available - chartSpace);
        }
    }
}

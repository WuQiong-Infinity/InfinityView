using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LightView.App.Imaging;
using LightView.App.ViewModels;

namespace LightView.App.Views;

public partial class MainWindow
{
    private ComparisonSession comparison = null!;
    private PaneContext activePane = null!;
    private bool syncingComparison, splitterDragging, splitLayout;
    private long focusGeneration;
    private readonly Dictionary<PaneContext, PaneUi> paneUi = new();
    private sealed record PaneUi(Grid Root, OutlinedTextBlock Name, Border Details, Viewbox DetailsBox, TextBlock Error, Border Toast, TextBlock ToastText, Border ColorMask);
    private IEnumerable<PaneContext> AllPanes => paneUi.Keys.Where(p => p.Attached);
    private PaneUi Ui(PaneContext p) => paneUi[p];

    private void InitializeComparison(ViewerViewModel initial)
    {
        comparison = new(imageLoader.ComparisonPolicy, imageLoader.SetComparisonMode);
        activePane = CreatePane(initial, Viewport);
        comparison.Changed += UpdateComparisonUi;
        foreach (var viewport in new[] { Viewport, ReferenceViewport })
        {
            viewport.IsReadOnly = false; viewport.Focusable = true;
            viewport.PreviewMouseDown += async (_, e) =>
            {
                var pane = PaneForViewport(viewport); if (pane is null) return;
                bool focusOnly = comparison.State == ComparisonState.Active && !ReferenceEquals(pane, activePane) &&
                    pane.Viewer.DisplayedImage is not null && e.ChangedButton == MouseButton.Left;
                ActivatePane(pane);
                // Consume the whole first click: no left-down reaches the viewport,
                // so its later left-up cannot navigate the newly selected pane.
                if (focusOnly) { e.Handled = true; return; }
                if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && pane.Viewer.DisplayedImage is null && !fileDialogOpen)
                { e.Handled = true; await OpenPictureAsync(pane); }
            };
            viewport.UserViewChanged += (_, _) => { var pane = PaneForViewport(viewport); if (pane is not null) SyncGesture(pane); };
            viewport.ViewChanged += (_, _) =>
            {
                var pane = PaneForViewport(viewport); if (pane is null) return;
                double physicalEdge = Math.Max(viewport.ActualWidth, viewport.ActualHeight) * VisualTreeHelper.GetDpi(viewport).DpiScaleX;
                LayoutHistogram();
                pane.Viewer.PreviewMaximumEdge = FrameworkApi.Clamp((int)Math.Ceiling(physicalEdge / 256) * 256, 768, 2560);
                if (ReferenceEquals(pane, activePane)) RefreshActiveReadout();
            };
            viewport.EdgeClicked += async direction => { var pane = PaneForViewport(viewport); if (pane is not null) await ExecuteCommandAsync(direction < 0 ? LightView.Core.Commands.CommandCatalog.Previous : LightView.Core.Commands.CommandCatalog.Next, owner: pane); };
        }
        PaneSplitter.DragStarted += (_, _) =>
        {
            splitterDragging = true;
            foreach (var pane in AllPanes) pane.Viewport.Transform.FreezeFit();
        };
        PaneSplitter.DragCompleted += (_, _) => splitterDragging = false;
    }
    private PaneContext CreatePane(ViewerViewModel model, ImageViewport viewport)
    {
        foreach (var old in paneUi.Keys.Where(p => ReferenceEquals(p.Viewport, viewport) && !p.Attached).ToArray()) paneUi.Remove(old);
        var pane = new PaneContext(model, viewport);
        bool physicalLeft = ReferenceEquals(viewport, ReferenceViewport);
        var ui = physicalLeft ? new PaneUi(ReferencePane, ReferenceName, LeftDetailsPanel, LeftDetailsViewbox, LeftComparisonError, LeftOperationToast, LeftOperationToastText, ReferencePlaceholder)
            : new PaneUi(CanvasContainer, CurrentComparisonName, DetailsPanel, DetailsViewbox, ComparisonError, OperationToast, OperationToastText, CurrentColorPlaceholder);
        paneUi[pane] = ui; ui.Root.DataContext = model;
        model.SetDetailsLayout(currentAppearance.DetailsLayout);
        ui.Details.Width = currentAppearance.DetailsWidth;
        ui.DetailsBox.Stretch = currentAppearance.DetailsFitSmallWindow ? Stretch.Uniform : Stretch.None;
        viewport.KeepViewOnImageChange = settings.KeepViewOnImageChange; viewport.EdgeNavigationEnabled = settings.EdgeNavigationEnabled;
        viewport.MouseNavigationRegion = settings.MouseNavigationRegion; viewport.MousePanButton = settings.MousePanButton;
        model.PropertyChanged += (_, e) =>
        {
            if (!pane.Attached) return;
            if (e.PropertyName is nameof(ViewerViewModel.DisplayedImage) or nameof(ViewerViewModel.IsLoading) or nameof(ViewerViewModel.ErrorMessage) or nameof(ViewerViewModel.OrganizingImage))
            { if (pane.Viewer.DisplayedImage?.DisplayTargetKey == colorTarget.Key) pane.ColorPending = false; comparison.UpdateResidency(); UpdatePaneUi(pane); if (ReferenceEquals(pane, activePane)) UpdateNoticeVisibility(); if (e.PropertyName == nameof(ViewerViewModel.DisplayedImage)) RefreshHistogram(); }
        };
        model.DiscardDecoded = imageLoader.ComparisonPolicy.ReleaseDecoded;
        model.BudgetRecoveryFactory = () =>
        {
            long generation = comparison.Generation, focus = focusGeneration; var activeAtStart = activePane;
            return () =>
            {
                if (!pane.Attached || !comparison.IsOn || comparison.Generation != generation || focus != focusGeneration || !ReferenceEquals(activePane, pane) || !ReferenceEquals(activeAtStart, pane)) return false;
                ExitComparison(pane); ShowOperationFeedback("这张图片超出双图预算，已返回单图查看。", state: "NoOp", owner: pane); return true;
            };
        };
        return pane;
    }
    private PaneContext? PaneForViewport(ImageViewport viewport) => AllPanes.FirstOrDefault(p => ReferenceEquals(p.Viewport, viewport));
    private PaneContext? PointerPane() => AllPanes.FirstOrDefault(p => Ui(p).Root.IsMouseOver);
    private void ActivatePane(PaneContext pane)
    {
        if (!pane.Attached) return;
        if (!ReferenceEquals(activePane, pane)) ++focusGeneration;
        activePane = pane; DataContext = pane.Viewer;
        foreach (var p in AllPanes)
        { p.Viewer.SetPrefetchEligibility(!comparison.IsOn || ReferenceEquals(p, pane)); UpdatePaneUi(p); }
        RefreshActiveReadout(); UpdateFocusCorners(); UpdateNoticeVisibility();
        RefreshHistogram();
    }
    private void UpdateFocusCorners()
    {
        bool on = comparison.State == ComparisonState.Active;
        LeftFocusCorners.IsActive = on && ReferenceEquals(activePane.Viewport, ReferenceViewport);
        RightFocusCorners.IsActive = on && ReferenceEquals(activePane.Viewport, Viewport);
    }
    private void RefreshActiveReadout()
    {
        ZoomReadout.Text = $"{activePane.Viewport.Transform.Zoom:P0}{(activePane.Viewport.Transform.IsFit ? " · 适配" : "")}";
        UpdateViewSelection();
    }
    private void SyncGesture(PaneContext owner)
    {
        if (syncingComparison || splitterDragging || comparison.State != ComparisonState.Active || !comparison.Linked || !owner.Attached) return;
        var other = ReferenceEquals(owner, comparison.Left) ? comparison.Right : comparison.Left;
        if (other is null || !other.Attached) return;
        syncingComparison = true;
        try { other.Viewport.Follow(owner.Viewport); }
        finally { syncingComparison = false; }
    }
    private void UpdatePaneUi(PaneContext pane)
    {
        if (!pane.Attached) return;
        var ui = Ui(pane); bool left = comparison.IsOn && ReferenceEquals(comparison.Left, pane);
        ui.Details.Visibility = pane.DetailsVisible ? Visibility.Visible : Visibility.Collapsed;
        ui.Details.HorizontalAlignment = comparison.IsOn && !left ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        ui.DetailsBox.HorizontalAlignment = ui.Details.HorizontalAlignment;
        DetailRowGrid.SetValueFirst(ui.Details, comparison.IsOn && !left);
        ui.Name.Visibility = comparison.IsOn ? Visibility.Visible : Visibility.Collapsed;
        ui.Name.TextAlignment = left ? TextAlignment.Right : TextAlignment.Left;
        ui.Name.Foreground = (Brush)FindResource(ReferenceEquals(pane, activePane) ? "TextBrush" : "SecondaryBrush");
        ui.Error.Visibility = comparison.IsOn && !FrameworkApi.IsNullOrWhiteSpace(pane.Viewer.ErrorMessage) ? Visibility.Visible : Visibility.Collapsed;
        ui.ColorMask.Visibility = pane.ColorPending ? Visibility.Visible : Visibility.Collapsed;
        ui.ToastText.Text = pane.Feedback;
        LayoutHistogram();
    }
    private void UpdateComparisonUi()
    {
        bool on = comparison.State == ComparisonState.Active;
        ReferenceColumn.MinWidth = CurrentColumn.MinWidth = on ? 200 : 0;
        if (on)
        {
            if (!splitLayout) ReferenceColumn.Width = CurrentColumn.Width = new GridLength(1, GridUnitType.Star);
            ComparisonSeparatorColumn.Width = new GridLength(5);
            Grid.SetColumn(Ui(comparison.Left!).Root, 0); Grid.SetColumn(Ui(comparison.Right!).Root, 2);
        }
        else
        {
            ReferenceColumn.Width = new GridLength(0); ComparisonSeparatorColumn.Width = new GridLength(0); CurrentColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(Ui(activePane).Root, 2);
        }
        foreach (var item in paneUi) { item.Value.Root.Visibility = item.Key.Attached && (on || ReferenceEquals(item.Key, activePane)) ? Visibility.Visible : Visibility.Collapsed; if (item.Key.Attached) UpdatePaneUi(item.Key); }
        splitLayout = on; UpdateFocusCorners(); ApplyComparisonChrome(); RefreshComparisonMenu(); UpdateNoticeVisibility();
    }
    private void ApplyComparisonChrome()
    {
        foreach (var p in AllPanes) Ui(p).Name.Margin = new Thickness(10, 0, 10, 10);
    }
    private void RefreshComparisonMenu()
    {
        ComparisonMenuItem.Header = comparison.IsOn ? "退出独立分屏" : "进入独立分屏";
        ComparisonMenuItem.IsEnabled = comparison.IsOn || activePane.Viewer.DisplayedImage is not null;
        LinkComparisonMenuItem.IsEnabled = comparison.IsOn; LinkComparisonMenuItem.IsChecked = comparison.IsOn && comparison.Linked;
    }
    private async Task ToggleComparisonAsync(PaneContext owner)
    {
        if (comparison.IsOn) { ExitComparison(owner); return; }
        await SuspendHistogramAsync();
        bool entered = await comparison.EnterAsync(owner, _ =>
        {
            var peer = CreatePane(new ViewerViewModel(imageLoader), ReferenceEquals(owner.Viewport, Viewport) ? ReferenceViewport : Viewport);
            peer.HistogramVisible = owner.HistogramVisible; return peer;
        }, colorTarget.Key);
        if (entered)
        {
            ActivatePane(owner); syncingComparison = true;
            try { comparison.Left!.Viewport.Fit(); owner.Viewport.Fit(); }
            finally { syncingComparison = false; }
        }
        RefreshHistogram();
    }
    private void ExitComparison(PaneContext keep)
    {
        if (!keep.Attached) return;
        CancelHistogramRequest(); activePane = keep; comparison.Exit(keep); ActivatePane(keep);
    }
    private void MarkPeerInvalid(PaneContext owner, LightView.App.Organizing.FileSnapshot version)
    { foreach (var pane in AllPanes.Where(p => !ReferenceEquals(p, owner))) pane.Viewer.InvalidateVersion(version); }
    private async Task ReloadPaneTargetAsync(PaneContext owner, string target)
    {
        string? path = owner.Viewer.RequestedOrDisplayedPath; if (path is null) return;
        int generation = ++owner.ColorGeneration; owner.ColorPending = true; UpdatePaneUi(owner); RefreshHistogram();
        var intent = owner.Viewer.CurrentRequestIntent;
        await owner.Viewer.OpenAsync(path, intent);
        if (!owner.Attached || generation != owner.ColorGeneration || colorTarget.Key != target) return;
        owner.ColorPending = owner.Viewer.DisplayedImage?.DisplayTargetKey != target;
        if (owner.ColorPending) ShowOperationFeedback("此窗格色彩目标未就绪，请重新打开图片。", owner: owner);
        UpdatePaneUi(owner);
        RefreshHistogram();
    }
    private void DisposeComparison()
    {
        comparison.Changed -= UpdateComparisonUi; comparison.Dispose();
        foreach (var pane in AllPanes.ToArray()) { pane.Attached = false; pane.Viewer.Detach(); pane.Viewport.Image = null; }
        paneUi.Clear();
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LightView.App.Imaging;
using LightView.App.Input;
using LightView.App.Persistence;
using LightView.App.Platform;
using LightView.App.Organizing;
using LightView.App.ViewModels;
using LightView.Core.Viewing;
using LightView.Core.Commands;
using LightView.Core.Settings;
using Microsoft.Win32;

namespace LightView.App.Views;

public partial class MainWindow : Window
{
    private readonly WicImageLoader imageLoader;
    private DisplayColorTarget colorTarget = DisplayColorTarget.Standard;
    private readonly DispatcherTimer displayRefresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly FullScreenController fullScreen;
    private bool fileDialogOpen;
    private string? lastOpenDirectory;
    private readonly SettingsStore settingsStore;
    private AppSettings settings = new();
    private AppearanceSettings currentAppearance = new();
    private ShortcutMap shortcuts = new(CommandCatalog.DefaultBindings());
    private SettingsWindow? settingsWindow;
    private RecoveryWindow? recoveryWindow;
    private SafeCopyService? copyService;
    private FileStream? operationLease;
    private bool copyBusy;
    private readonly TaskCompletionSource startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer operationToastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer chromeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly FullscreenChromeState chromeState = new();
    private bool? chromeTargetVisible;
    private bool persistentOperationFeedback;

    public MainWindow()
    {
        imageLoader = new WicImageLoader(() => Volatile.Read(ref colorTarget));
        var initialViewer = new ViewerViewModel(imageLoader);
        InitializeComponent();
        _ = new ImeShortcutBridge(this, () => !MoreMenu.IsOpen && !fileDialogOpen,
            (key, repeat) => InputRouter.KeyboardGesture(key) is string gesture && shortcuts.Resolve(gesture, repeat) is not null,
            OnPreviewKeyDown);
        WindowAppearance.Attach(this);
        DataContext = initialViewer;
        fullScreen = new FullScreenController(this);
        InitializeComparison(initialViewer);
        InitializeLinkToast();
        InitializeHistogram();
        settingsStore = new SettingsStore(AppPaths.UserData);
        var loadedSettings = ReadSettings();
        settings = loadedSettings.IsReadOnly ? new AppSettings() : loadedSettings.Settings;
        SourceInitialized += (_, _) =>
        {
            try { WindowPlacement.Restore(this, settings.Window); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            { activePane.Viewer.Notify("未能恢复上次窗口位置，将使用可用窗口：" + error.Message); }
        };
        ApplyAppearance(settings.Appearance);
        shortcuts = new ShortcutMap(settings.Bindings, settings.Presets);
        Viewport.KeepViewOnImageChange = settings.KeepViewOnImageChange;
        Viewport.EdgeNavigationEnabled = settings.EdgeNavigationEnabled;
        if (loadedSettings.Warning is not null) activePane.Viewer.Notify("设置加载有提示，请打开设置查看。");
        RefreshTooltips();
        SizeChanged += (_, _) => UpdateResponsiveCommands();
        UpdateResponsiveCommands();
        UpdateChromeVisibility();
        operationToastTimer.Tick += (_, _) =>
        {
            operationToastTimer.Stop();
            foreach (var pane in AllPanes) Ui(pane).Toast.Visibility = Visibility.Collapsed;
        };
        chromeTimer.Tick += (_, _) => RefreshFullscreenChrome();
        displayRefresh.Tick += async (_, _) =>
        {
            displayRefresh.Stop();
            await RefreshDisplayTargetAsync();
        };
        LocationChanged += OnDisplayEnvironmentChanged;
        DpiChanged += OnDisplayEnvironmentChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayEnvironmentChanged;
        SystemEvents.UserPreferenceChanged += OnDisplayEnvironmentChanged;
        Closed += (_, _) =>
        {
            displayRefresh.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayEnvironmentChanged;
            SystemEvents.UserPreferenceChanged -= OnDisplayEnvironmentChanged;
            operationLease?.Dispose();
            operationToastTimer.Stop();
            chromeTimer.Stop();
            activePane.Viewer.CancelPrefetch();
            DisposeComparison();
        };
        Closing += (_, e) =>
        {
            if (copyBusy || settingsWindow?.IsSaving == true)
            {
                e.Cancel = true;
                activePane.Viewer.Notify(copyBusy ? "正在完成文件操作，请结束后再关闭窗口。" : "正在保存设置，请结束后再关闭窗口。");
                return;
            }
            SaveWindowPlacement();
        };
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
        => await OpenPictureAsync(activePane);

    private async Task OpenPictureAsync(PaneContext owner)
    {
        if (fileDialogOpen) return;
        var dialog = new OpenFileDialog
        {
            Title = "打开图片",
            Filter = ImageFormats.DialogFilter,
            Multiselect = false,
            CheckFileExists = true,
            RestoreDirectory = true
        };
        string? directory = owner.Viewer.DisplayedImage is null
            ? null : Path.GetDirectoryName(owner.Viewer.DisplayedImage.FilePath);
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        directory = directory is not null && Directory.Exists(directory) ? directory
            : lastOpenDirectory is not null && Directory.Exists(lastOpenDirectory) ? lastOpenDirectory
            : Directory.Exists(pictures) ? pictures : null;
        if (directory is not null) dialog.InitialDirectory = directory;
        if (owner.Viewer.DisplayedImage is not null && File.Exists(owner.Viewer.DisplayedImage.FilePath))
            dialog.FileName = owner.Viewer.DisplayedImage.FilePath;
        fileDialogOpen = true;
        try
        {
            if (dialog.ShowDialog(this) == true && owner.Attached)
            {
                await owner.Viewer.OpenAsync(dialog.FileName);
                if (owner.Viewer.DisplayedImage?.FilePath == dialog.FileName)
                    lastOpenDirectory = Path.GetDirectoryName(dialog.FileName);
            }
        }
        finally { fileDialogOpen = false; }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var owner = activePane;
        try
        {
        await RefreshDisplayTargetAsync(false);
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Length > 1)
            await owner.Viewer.OpenAsync(arguments[1]);
        }
        finally { startupReady.TrySetResult(); }
        await ShowRecoveryAsync(false);
    }

    internal async Task OpenExternalAsync(string? path)
    {
        var owner = activePane;
        await startupReady.Task;
        ForegroundActivation.BringToFront(this, fullScreen.IsFullScreen);
        if (path is not null) await owner.Viewer.OpenAsync(path);
    }

    private void OnDisplayEnvironmentChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        displayRefresh.Stop(); displayRefresh.Start();
    }));

    private async Task RefreshDisplayTargetAsync(bool reload = true)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var target = DisplayColorProbe.TargetForWindow(handle);
        if (target.Key == colorTarget.Key) return;
        Volatile.Write(ref colorTarget, target);
        if (reload)
        {
            var owners = AllPanes.ToArray();
            await Task.WhenAll(owners.Select(owner => ReloadPaneTargetAsync(owner, target.Key)));
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var owner = PointerPane() ?? activePane;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
            await owner.Viewer.OpenAsync(paths[0]);
    }

    private async void OnFitClick(object sender, RoutedEventArgs e) => await ExecuteCommandAsync(CommandCatalog.Fit);
    private async void OnActualPixelsClick(object sender, RoutedEventArgs e) => await ExecuteCommandAsync(CommandCatalog.ActualPixels);
    private async void OnFullScreenClick(object sender, RoutedEventArgs e) => await ExecuteCommandAsync(CommandCatalog.FullScreen);
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void ToggleFullScreen()
    {
        fullScreen.Toggle();
        WindowAppearance.Apply(this);
        UpdateChromeVisibility();
        activePane.Viewport.Focus();
    }

    private async void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (MoreMenu.IsOpen) return;
        var owner = PointerPane() ?? activePane;
        ActivatePane(owner);
        string? gesture = InputRouter.MouseGesture(e.ChangedButton);
        var command = gesture is null ? null : shortcuts.Resolve(gesture, textEntry: InputRouter.IsTextEntry);
        if (command is null || fileDialogOpen) return;
        e.Handled = true;
        await ExecuteCommandAsync(command.Id, owner: owner);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MoreMenu.IsOpen) return;
        string? gesture = InputRouter.KeyboardGesture(e);
        var command = gesture is null ? null : shortcuts.Resolve(gesture, e.IsRepeat, InputRouter.IsTextEntry);
        if (command is null || fileDialogOpen) return;
        e.Handled = true;
        await ExecuteCommandAsync(command.Id);
    }

    private async void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MoreMenu.IsOpen) return;
        var owner = PointerPane();
        if (owner is null || !owner.Viewport.IsMouseOver || fileDialogOpen) return;
        string? gesture = InputRouter.MakeGesture(e.Delta > 0 ? "WHEELUP" : "WHEELDOWN");
        var command = gesture is null ? null : shortcuts.Resolve(gesture, textEntry: InputRouter.IsTextEntry);
        if (command is null) return;
        e.Handled = true;
        await ExecuteCommandAsync(command.Id, e.GetPosition(owner.Viewport), Math.Abs(e.Delta / 120.0), owner);
    }

    private async Task ExecuteCommandAsync(string command, Point? anchor = null, double zoomSteps = 1, PaneContext? owner = null)
    {
        owner ??= activePane;
        if (!owner.Attached) return;
        // Capture the pointer before awaiting quality. EnsureFullAsync rejects a
        // navigation that overtook this request, so a late command cannot zoom
        // a different photo or present enlarged preview pixels as 100%.
        if (command == CommandCatalog.ActualPixelsAtPointer)
        {
            if (!owner.Viewport.IsMouseOver) return;
            anchor ??= Mouse.GetPosition(owner.Viewport);
        }
        bool requiresFull = command is CommandCatalog.ActualPixels or CommandCatalog.ActualPixelsAtPointer
            or CommandCatalog.ZoomIn or CommandCatalog.ZoomOut || command == CommandCatalog.ToggleFit && owner.Viewport.Transform.IsFit;
        if (requiresFull && (owner.ColorPending || !await owner.Viewer.EnsureFullAsync() || !owner.Attached)) return;
        switch (command)
        {
            case CommandCatalog.Open: await OpenPictureAsync(owner); break;
            case CommandCatalog.Previous: await owner.Viewer.NavigateAsync(-1); break;
            case CommandCatalog.Next: await owner.Viewer.NavigateAsync(1); break;
            case CommandCatalog.Fit: owner.Viewport.Fit(); break;
            case CommandCatalog.ActualPixels: owner.Viewport.ActualPixels(); break;
            case CommandCatalog.ActualPixelsAtPointer:
                if (owner.Viewport.IsMouseOver) owner.Viewport.ActualPixelsAt(anchor ?? Mouse.GetPosition(owner.Viewport));
                break;
            case CommandCatalog.ToggleFit:
                if (owner.Viewport.Transform.IsFit) owner.Viewport.ActualPixels(); else owner.Viewport.Fit();
                break;
            case CommandCatalog.FullScreen: ToggleFullScreen(); break;
            case CommandCatalog.ExitFullScreen:
                if (comparison.IsOn) { ExitComparison(owner); break; }
                fullScreen.Exit(); WindowAppearance.Apply(this); UpdateChromeVisibility(); break;
            case CommandCatalog.Comparison: await ToggleComparisonAsync(owner); break;
            case CommandCatalog.LinkComparison:
                if (comparison.State != ComparisonState.Active) break;
                comparison.SetLinked(!comparison.Linked); if (comparison.Linked) SyncGesture(owner);
                ShowLinkState(comparison.Linked); break;
            case CommandCatalog.FocusLeft:
                if (comparison.Left is { } left) { ActivatePane(left); left.Viewport.Focus(); } break;
            case CommandCatalog.FocusRight:
                if (comparison.Right is { } right) { ActivatePane(right); right.Viewport.Focus(); } break;
            case CommandCatalog.Settings: OpenSettings(); break;
            case CommandCatalog.Details: ToggleDetails(owner); break;
            case CommandCatalog.Histogram: ToggleHistogram(); break;
            case CommandCatalog.CopyShootingInfo:
                ShowOperationFeedback(ShootingInfoFormatter.Copy(owner.Viewer.DisplayedImage, text => Clipboard.SetText(text)), owner: owner);
                break;
            case CommandCatalog.Undo: await UndoFileAsync(owner); break;
            case CommandCatalog.Delete: await RecycleImageAsync(owner); break;
            case CommandCatalog.Recovery: await ShowRecoveryAsync(true); break;
            case CommandCatalog.Reveal: RevealInExplorer(owner); break;
            case CommandCatalog.About:
                OpenSettings(); settingsWindow?.FocusAbout();
                break;
            case CommandCatalog.ZoomIn:
            case CommandCatalog.ZoomOut:
                owner.Viewport.ZoomAt(anchor ?? new Point(owner.Viewport.ActualWidth / 2, owner.Viewport.ActualHeight / 2),
                    Math.Pow(settings.ZoomStep, command == CommandCatalog.ZoomIn ? zoomSteps : -zoomSteps));
                break;
            default:
                if (CommandCatalog.TryPresetAction(command, out string presetId, out bool move))
                {
                    if (!owner.CanOrganize)
                    { ShowOperationFeedback("请等待有效原图读取完成后再整理。", owner: owner); break; }
                    var preset = settings.Presets.FirstOrDefault(item => item.Id == presetId);
                    if (preset is null || !preset.Enabled) ShowOperationFeedback("此目标预设不存在或已停用。", owner: owner);
                    else if (FrameworkApi.IsNullOrWhiteSpace(preset.Path)) OpenSettings(presetId);
                    else await CopyToPresetAsync(owner, preset, move);
                }
                break;
        }
    }

    private void ToggleDetails(PaneContext owner)
    {
        owner.DetailsVisible = !owner.DetailsVisible; UpdatePaneUi(owner); UpdateViewSelection(); owner.Viewport.Focus();
    }
    private void OnDetailsClick(object sender, RoutedEventArgs e) => ToggleDetails(activePane);

    private async Task CopyToPresetAsync(PaneContext owner, FolderPreset preset, bool move)
    {
        if (copyBusy) { ShowOperationFeedback("文件操作正在进行，本次按键未排队。", owner: owner); return; }
        var image = owner.Viewer.OrganizingImage;
        if (!owner.CanOrganize || image?.FileVersion is null) { ShowOperationFeedback("请等待图片读取完成后再整理。", owner: owner); return; }
        // Capture both the displayed version and settings before any await.
        var request = new CopyRequest(image.FileVersion, preset.Path, preset.Name, ConflictPolicy: settings.ConflictPolicy);
        bool advance = move ? preset.MoveAdvance : preset.CopyAdvance;
        int navigationVersion = owner.Viewer.NavigationVersion;
        copyBusy = true;
        UpdateUndoButton();
        owner.Viewer.Notify($"正在{(move ? "移动" : "复制")}到 " + request.PresetName + "…");
        try
        {
            await EnsureFileSessionAsync(owner);
            var result = await (move ? copyService!.MoveAsync(request) : copyService!.CopyAsync(request));
            if (result.State == "Conflict" && request.ConflictPolicy == FileConflictPolicy.Ask)
            {
                var choice = MessageBox.Show(this, "目标存在同名项目。是否自动改名并保留双方？\n是：改名继续；否：跳过；取消：取消本次操作。",
                    "同名文件", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                result = choice == MessageBoxResult.Yes
                    ? await (move ? copyService!.MoveAsync(request with { ConflictPolicy = FileConflictPolicy.Rename })
                        : copyService!.CopyAsync(request with { ConflictPolicy = FileConflictPolicy.Rename }))
                    : new CopyResult(choice == MessageBoxResult.No ? "Skipped" : "Cancelled",
                        choice == MessageBoxResult.No ? "已跳过同名文件，源文件已保留。" : "本次操作已取消，源文件已保留。");
            }
            if (move && result.State == "Completed" && result.Record?.DestinationVersion is { } destination)
            {
                MarkPeerInvalid(owner, image.FileVersion);
                await owner.Viewer.ApplyMoveAsync(image, destination, advance, navigationVersion);
            }
            else if (result.State == "Completed" && advance && owner.Viewer.NavigationVersion == navigationVersion && ReferenceEquals(owner.Viewer.OrganizingImage, image))
                await owner.Viewer.NavigateAsync(1);
            ShowOperationFeedback(result.State == "NeedsRecovery"
                ? result.Message + " 日志：" + Path.Combine(AppPaths.UserData, "Operations") : result.Message,
                result.State == "Completed", result.State, owner);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowOperationFeedback("无法开始文件操作：" + error.Message, owner: owner); }
        finally { copyBusy = false; UpdateUndoButton(); }
    }

    private void ShowOperationFeedback(string message, bool succeeded = false, string? state = null, PaneContext? owner = null)
    {
        owner ??= activePane; if (!owner.Attached) return;
        owner.Viewer.Notify(message); owner.Feedback = message;
        operationToastTimer.Stop(); var ui = Ui(owner); ui.ToastText.Text = message;
        bool neutral = state is "NoOp" or "Skipped" or "Cancelled";
        owner.FeedbackPersistent = !succeeded && !neutral;
        persistentOperationFeedback = AllPanes.Any(p => p.FeedbackPersistent);
        ui.Toast.SetResourceReference(Border.BorderBrushProperty, succeeded ? "SuccessBrush" : neutral ? "SecondaryBrush" : "ErrorBrush");
        ui.Toast.Visibility = Visibility.Visible; if (!owner.FeedbackPersistent) operationToastTimer.Start(); RefreshFullscreenChrome();
    }
    private void OnDismissFeedback(object sender, RoutedEventArgs e)
    {
        var owner = PointerPane() ?? activePane; Ui(owner).Toast.Visibility = Visibility.Collapsed; owner.FeedbackPersistent = false;
        persistentOperationFeedback = AllPanes.Any(p => p.FeedbackPersistent); RefreshFullscreenChrome();
    }

    private async Task EnsureFileSessionAsync(PaneContext owner)
    {
        if (copyService is not null) return;
        string path = Path.Combine(AppPaths.UserData, "Operations");
        operationLease = OperationJournal.AcquireSession(path);
        var archive = await Task.Run(() => JournalArchive.Rotate(path, DateTimeOffset.UtcNow));
        if (archive.Warning is not null) owner.Viewer.Notify(archive.Warning);
        copyService = new(new OperationJournal(path));
    }
    private void UpdateUndoButton() => UndoButton.IsEnabled = !copyBusy && copyService?.UndoCount > 0;
    private async void OnUndoClick(object sender, RoutedEventArgs e) => await UndoFileAsync(activePane);
    private async Task UndoFileAsync(PaneContext owner)
    {
        if (copyBusy) { owner.Viewer.Notify("文件操作正在进行，撤销未排队。"); return; }
        if (copyService is null) { owner.Viewer.Notify("本次会话没有可撤销操作。"); return; }
        var anchor = owner.Viewer.OrganizingImage;
        int navigationVersion = owner.Viewer.NavigationVersion;
        string? directory = owner.Viewer.ActiveDirectory;
        copyBusy = true; UpdateUndoButton(); owner.Viewer.Notify("正在撤销本次会话操作…");
        try
        {
            var result = await copyService.UndoAsync();
            if (result.State == "Conflict" && MessageBox.Show(this,
                    "原位置已被占用。是否自动改名恢复并保留双方？选择“否”将取消撤销。", "撤销冲突",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                result = await copyService.UndoAsync(true);
            if (result.State == "Completed" && result.Record is { DestinationVersion: { } restored } record)
            {
                if (ReferenceEquals(owner.Viewer.OrganizingImage, anchor) && anchor?.FileVersion is { } current && current.SameVersion(record.Source))
                    await owner.Viewer.ApplyMoveAsync(anchor, restored, false);
                else if (settings.UndoJumpBack && record.Kind == "UndoMove" && !owner.Viewer.IsLoading && owner.Viewer.NavigationVersion == navigationVersion &&
                    ReferenceEquals(owner.Viewer.OrganizingImage, anchor) && StringComparer.OrdinalIgnoreCase.Equals(directory, Path.GetDirectoryName(restored.Path)))
                    await owner.Viewer.OpenAsync(restored.Path);
                MarkPeerInvalid(owner, record.Source);
                await owner.Viewer.RefreshFolderAsync();
            }
            ShowOperationFeedback(result.Message, result.State == "Completed", result.State, owner);
        }
        finally { copyBusy = false; UpdateUndoButton(); }
    }

    private async void OnRecoveryClick(object sender, RoutedEventArgs e) => await ShowRecoveryAsync(true);
    private async Task ShowRecoveryAsync(bool showEmpty)
    {
        var owner = activePane;
        if (recoveryWindow is not null) { recoveryWindow.Activate(); return; }
        string path = Path.Combine(AppPaths.UserData, "Operations");
        var scan = await Task.Run(() => JournalRecovery.Read(path, showEmpty));
        if (scan.Items.Count == 0 && scan.Warning is null && !showEmpty) return;
        recoveryWindow = new(path, scan, async item =>
        {
            if (copyBusy) return "文件操作正在进行，请结束后再确认。";
            copyBusy = true; UpdateUndoButton();
            try { await EnsureFileSessionAsync(owner); await Task.Run(() => JournalRecovery.KeepAll(path, item)); return null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return error.Message; }
            finally { copyBusy = false; UpdateUndoButton(); }
        }) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        recoveryWindow.Closed += (_, _) => recoveryWindow = null;
        recoveryWindow.Show();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (fullScreen.IsFullScreen) RefreshFullscreenChrome();
    }

    private void UpdateChromeVisibility()
    {
        ApplyChromeLayout(fullScreen.IsFullScreen);
        chromeState.Reset(fullScreen.IsFullScreen, settings.Window.FullScreenAutoHide);
        SetChromeVisible(chromeState.IsVisible, false);
        if (fullScreen.IsFullScreen && settings.Window.FullScreenAutoHide) chromeTimer.Start(); else chromeTimer.Stop();
        UpdateNoticeVisibility();
        UpdateViewSelection();
    }

    private void UpdateNoticeVisibility() => NoticePanel.Visibility =
        !comparison.IsOn && !FrameworkApi.IsNullOrEmpty(activePane.Viewer.ErrorMessage)
            ? Visibility.Visible : Visibility.Collapsed;

    private async void OnPreviousClick(object sender, RoutedEventArgs e) => await ExecuteCommandAsync(CommandCatalog.Previous);
    private async void OnNextClick(object sender, RoutedEventArgs e) => await ExecuteCommandAsync(CommandCatalog.Next);

    private SettingsLoadResult ReadSettings()
    {
        try { return settingsStore.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(new AppSettings(), "暂时无法读取设置，本次使用默认值且不覆盖原文件。", true); }
    }

    private void OpenSettings(string? presetToEdit = null, int? section = null)
    {
        if (settingsWindow is not null)
        {
            if (presetToEdit is not null) settingsWindow.FocusPreset(presetToEdit);
            if (section is int existingSection) settingsWindow.FocusSection(existingSection);
            ForegroundActivation.BringToFront(settingsWindow); return;
        }
        var current = ReadSettings();
        settingsWindow = new SettingsWindow(current.IsReadOnly ? new AppSettings() : current.Settings,
            ApplySettingsAsync, current.Warning, current.IsReadOnly, presetToEdit,
            ApplyAppearance, settings.Appearance, () => ShowRecoveryAsync(true), ApplyImportedSettingsAsync) { Owner = this };
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        if (section is int selectedSection) settingsWindow.FocusSection(selectedSection);
        settingsWindow.Show();
        ForegroundActivation.BringToFront(settingsWindow);
    }

    private Task<string?> ApplySettingsAsync(AppSettings updated) => ApplySettingsCoreAsync(updated, false);
    private Task<string?> ApplyImportedSettingsAsync(AppSettings updated) => ApplySettingsCoreAsync(updated, true);
    private async Task<string?> ApplySettingsCoreAsync(AppSettings updated, bool imported)
    {
        try
        {
            var map = new ShortcutMap(updated.Bindings, updated.Presets);
            await Task.Run(() => { if (imported) settingsStore.SaveImported(updated); else settingsStore.Save(updated); });
            settings = updated;
            ApplyAppearance(settings.Appearance);
            foreach (var pane in AllPanes) { pane.Viewport.KeepViewOnImageChange = settings.KeepViewOnImageChange; pane.Viewport.EdgeNavigationEnabled = settings.EdgeNavigationEnabled; pane.Viewport.MouseNavigationRegion = settings.MouseNavigationRegion; pane.Viewport.MousePanButton = settings.MousePanButton; }
            shortcuts = map;
            UpdateChromeVisibility();
            RefreshTooltips();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        { return "未保存设置：" + error.Message; }
    }

    private void RefreshTooltips()
    {
        string Hint(string id)
        {
            string? gesture = settings.Bindings.FirstOrDefault(binding => binding.CommandId == id && binding.Enabled && !FrameworkApi.IsNullOrWhiteSpace(binding.Gesture))?.Gesture;
            string name = CommandCatalog.Find(id)?.Name ?? "";
            return gesture is null ? name : $"{name}（{ShortcutGesture.Display(gesture)}）";
        }
        OpenButton.ToolTip = Hint(CommandCatalog.Open);
        PreviousButton.ToolTip = Hint(CommandCatalog.Previous);
        NextButton.ToolTip = Hint(CommandCatalog.Next);
        FullScreenButton.ToolTip = Hint(CommandCatalog.FullScreen);
        SettingsButton.ToolTip = Hint(CommandCatalog.Settings);
        DetailsButton.ToolTip = Hint(CommandCatalog.Details);
        UndoButton.ToolTip = Hint(CommandCatalog.Undo) + "；最近100项，重启后不提供一键撤销";
        FitButton.ToolTip = Hint(CommandCatalog.Fit);
        ActualPixelsButton.ToolTip = Hint(CommandCatalog.ActualPixels);
        foreach (var item in MoreMenu.Items.OfType<MenuItem>())
            if (item.Tag is string id)
            {
                string? gesture = settings.Bindings.FirstOrDefault(binding => binding.CommandId == id && binding.Enabled && !FrameworkApi.IsNullOrWhiteSpace(binding.Gesture))?.Gesture;
                item.InputGestureText = gesture is null ? "" : ShortcutGesture.Display(gesture);
            }
    }

    internal void UpdateResponsiveCommands(double? width = null)
    {
        bool narrow = (width ?? ActualWidth) < 960;
        PositionReadout.Visibility = SecondaryCommands.Visibility = ActualPixelsButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateViewSelection()
    {
        FitButton.IsChecked = activePane.Viewport.Transform.IsFit;
        ActualPixelsButton.IsChecked = !activePane.Viewport.Transform.IsFit && Math.Abs(activePane.Viewport.Transform.Zoom - 1) < 0.000001;
        DetailsButton.IsChecked = activePane.DetailsVisible;
        FullScreenButton.IsChecked = fullScreen.IsFullScreen;
    }
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton; MoreMenu.Placement = PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }
    private void OnMoreOpened(object sender, RoutedEventArgs e)
    {
        DetailsMenuItem.IsChecked = activePane.DetailsVisible;
        FullScreenMenuItem.IsChecked = fullScreen.IsFullScreen;
        UndoMenuItem.IsEnabled = UndoButton.IsEnabled;
        RevealMenuItem.IsEnabled = activePane.Viewer.DisplayedImage is not null;
        CopyShootingMenuItem.IsEnabled = ShootingInfoFormatter.Format(activePane.Viewer.DisplayedImage) is not null;
        RefreshComparisonMenu();
        RefreshTooltips();
        RefreshFullscreenChrome();
    }
    private void OnMoreClosed(object sender, RoutedEventArgs e) { activePane.Viewport.Focus(); RefreshFullscreenChrome(); }
    private async void OnMenuCommand(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is MenuItem { Tag: string command }) await ExecuteCommandAsync(command);
    }
    private void RevealInExplorer(PaneContext owner)
    {
        if (owner.Viewer.DisplayedImage is not { } image) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + image.FilePath + "\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ShowOperationFeedback("无法打开资源管理器：" + error.Message, owner: owner); }
    }

    internal void ApplyAppearance(AppearanceSettings appearance)
    {
        currentAppearance = appearance.Clone();
        foreach (var pane in AllPanes) pane.Viewer.SetDetailsLayout(appearance.DetailsLayout);
        var brush = new System.Windows.Media.SolidColorBrush(appearance.CanvasBackground switch
        {
            "midGray" => System.Windows.Media.Colors.Gray,
            "black" => System.Windows.Media.Colors.Black,
            _ => System.Windows.Media.Color.FromRgb(16, 16, 16)
        });
        brush.Freeze();
        CanvasContainer.Background = Viewport.Background = ReferenceViewport.Background = ReferencePane.Background = brush;
        Viewport.TransparencyCheckerboard = ReferenceViewport.TransparencyCheckerboard = appearance.TransparencyCheckerboard;
        foreach (var pane in AllPanes)
        {
            Ui(pane).Details.Width = appearance.DetailsWidth;
            Ui(pane).DetailsBox.Stretch = appearance.DetailsFitSmallWindow ? System.Windows.Media.Stretch.Uniform : System.Windows.Media.Stretch.None;
        }
        Resources["DetailsBodySize"] = appearance.DetailsFontSize;
        Resources["DetailsSectionSize"] = appearance.DetailsFontSize * 17 / 16;
        Resources["DetailsTitleSize"] = appearance.DetailsFontSize * 22 / 16;
        Resources["DetailsOutlineSize"] = appearance.DetailsOutlineThickness;
        Resources["DetailsRowMargin"] = new Thickness(0, appearance.DetailsLineGap / 2, 0, appearance.DetailsLineGap / 2);
        Resources["DetailsContentWidth"] = appearance.DetailsWidth - 16;
    }

    internal void ApplyChromeLayout(bool fullscreen)
    {
        bool overlay = fullscreen && settings.Window.FullScreenAutoHide;
        TopChromeRow.Height = new GridLength(overlay ? 0 : 44);
        ApplyComparisonChrome();
        Footer.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(NoticePanel, fullscreen ? 1 : 2);
        Panel.SetZIndex(NoticePanel, fullscreen ? 25 : 0);
        NoticePanel.VerticalAlignment = fullscreen ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        NoticePanel.Margin = overlay ? new Thickness(18, 52, 18, 8) : new Thickness(18, 8, 18, 8);
    }
    private void RefreshFullscreenChrome()
    {
        if (!fullScreen.IsFullScreen) return;
        Point pointer = Mouse.GetPosition(this);
        bool imageDrag = AllPanes.Any(p => p.Viewport.IsMouseCaptured);
        bool hold = MoreMenu.IsOpen || TopBar.IsKeyboardFocusWithin || Mouse.Captured is not null && !imageDrag || copyBusy ||
            persistentOperationFeedback || !FrameworkApi.IsNullOrEmpty(activePane.Viewer.ErrorMessage);
        SetChromeVisible(chromeState.Update(true, settings.Window.FullScreenAutoHide, pointer.Y is >= 0 and <= 8,
            TopBar.IsMouseOver, hold, DateTimeOffset.UtcNow, imageDrag), animate: !imageDrag);
    }
    private void SetChromeVisible(bool visible, bool animate = true)
    {
        if (chromeTargetVisible == visible && animate) return;
        chromeTargetVisible = visible;
        double from = TopBar.Opacity;
        TopBar.BeginAnimation(OpacityProperty, null);
        if (!animate || settings.Appearance.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        { TopBar.Opacity = visible ? 1 : 0; TopBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; return; }
        TopBar.Visibility = Visibility.Visible;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(from, visible ? 1 : 0, TimeSpan.FromMilliseconds(120));
        animation.Completed += (_, _) => { if (chromeTargetVisible == false) TopBar.Visibility = Visibility.Collapsed; };
        TopBar.BeginAnimation(OpacityProperty, animation);
    }
    private void SaveWindowPlacement()
    {
        if (!settings.Window.RememberPlacement) return;
        try
        {
            // Read a fresh fingerprint and update only placement, preserving external settings.
            WindowPlacement.Save(AppPaths.UserData, fullScreen.NormalBounds, fullScreen.NormalDpi, fullScreen.NormalMaximized);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.UserData, "window-placement.log"), DateTimeOffset.UtcNow.ToString("o") + " " + error.Message + Environment.NewLine); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        }
    }
}

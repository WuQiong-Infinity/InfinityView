using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using LightView.App.Input;
using LightView.App.ViewModels;
using LightView.App.Persistence;
using LightView.Core.Commands;
using LightView.Core.Settings;
using Microsoft.Win32;

namespace LightView.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsSession session;
    private readonly Func<AppSettings, Task<string?>> apply;
    private readonly Func<Task>? openRecovery;
    private readonly Func<AppSettings, Task<string?>>? applyImported;
    private System.ComponentModel.ICollectionView? commandView;
    private readonly ObservableCollection<ShortcutRow> rows = [];
    private readonly ObservableCollection<FolderPresetRow> presets = [];
    private ShortcutRow? recording;
    private bool applying;
    private bool closed;
    internal bool IsSaving => applying;
    internal AppSettings AppliedSnapshot => session.LastAppliedSnapshot;

    public SettingsWindow(AppSettings settings, Func<AppSettings, Task<string?>> apply,
                          string? warning, bool readOnly, string? presetToEdit = null,
                          Action<AppearanceSettings>? appearancePreview = null, AppearanceSettings? appliedAppearance = null, Func<Task>? openRecovery = null,
                          Func<AppSettings, Task<string?>>? applyImported = null)
    {
        InitializeComponent();
        _ = new ImeShortcutBridge(this, () => recording is not null && !applying,
            (key, repeat) => !repeat && InputRouter.KeyboardGesture(key) is not null, OnPreviewKeyDown);
        LightView.App.Platform.WindowAppearance.Attach(this);
        session = new(settings, appearancePreview ?? (_ => { }), readOnly, appliedAppearance);
        this.apply = apply;
        this.openRecovery = openRecovery;
        this.applyImported = applyImported;
        AppearanceEditor.Load(settings.Appearance);
        AppearanceEditor.SetReadOnly(readOnly);
        DetailsEditor.Load(settings.Appearance); DetailsEditor.SetReadOnly(readOnly);
        AppearanceEditor.DetailsRequested += () => DetailsContentTab.IsSelected = true;
        AppearanceEditor.Changed += appearance => { session.Draft.Appearance = appearance; DetailsEditor.Load(appearance); session.PreviewAppearance(); };
        DetailsEditor.Changed += appearance => { session.Draft.Appearance = appearance; AppearanceEditor.Load(appearance); session.PreviewAppearance(); };
        Closing += (_, e) => e.Cancel = applying || !session.TryClose();
        Closed += (_, _) => closed = true;
        Activated += async (_, _) => { if (DefaultAppsTab.IsSelected) await RefreshRegistrationAsync(); };
        commandView = CollectionViewSource.GetDefaultView(rows);
        commandView.Filter = item => item is ShortcutRow row && (row.Name.Contains(CommandSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase) || row.Gesture.Contains(CommandSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        BindingsGrid.ItemsSource = commandView;
        CommandSearch.GotKeyboardFocus += (_, _) => CancelRecording();
        BindingsGrid.BeginningEdit += (_, _) => CancelRecording();
        PresetsGrid.ItemsSource = presets;
        foreach (var preset in settings.Presets) AddPresetRow(preset);
        Fill(settings.Bindings);
        ZoomStepBox.Text = settings.ZoomStep.ToString(CultureInfo.InvariantCulture);
        UndoJumpBackCheck.IsChecked = settings.UndoJumpBack;
        KeepViewCheck.IsChecked = settings.KeepViewOnImageChange;
        EdgeNavigationCheck.IsChecked = settings.EdgeNavigationEnabled;
        MouseNavigationRegionBox.SelectedIndex = settings.MouseNavigationRegion == "edges" ? 1 : 0;
        MousePanButtonBox.SelectedIndex = settings.MousePanButton == "left" ? 1 : 0;
        FullscreenAutoHideCheck.IsChecked = settings.Window.FullScreenAutoHide;
        RememberPlacementCheck.IsChecked = settings.Window.RememberPlacement;
        ConflictPolicyBox.SelectedIndex = (int)settings.ConflictPolicy;
        Message.Text = warning ?? "更改会在应用成功后保存；关闭可放弃尚未应用的编辑。";
        ApplyButton.IsEnabled = !readOnly;
        OpenRecoveryButton.IsEnabled = openRecovery is not null;
        ExportSettingsButton.IsEnabled = !readOnly;
        ImportSettingsButton.IsEnabled = !readOnly && applyImported is not null;
        VersionText.Text = typeof(SettingsWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        ConfigDirectoryText.Text = LightView.App.Persistence.AppPaths.UserData;
        LogDirectoryText.Text = Path.Combine(LightView.App.Persistence.AppPaths.UserData, "Operations");
        if (presetToEdit is not null)
        {
            TargetsTab.IsSelected = true;
            PresetsGrid.SelectedItem = presets.FirstOrDefault(preset => preset.Id == presetToEdit);
        }
    }

    private void Fill(IEnumerable<ShortcutBinding> bindings)
    {
        rows.Clear();
        var current = PresetSnapshot();
        foreach (var binding in bindings) rows.Add(new ShortcutRow(binding, CommandCatalog.Find(binding.CommandId, current)) { IsAlternate = rows.Any(row => row.CommandId == binding.CommandId) });
        foreach (var command in CommandCatalog.Definitions(current))
            if (!rows.Any(row => row.CommandId == command.Id)) rows.Add(new(new(command.Id, ""), command));
        BindingsGrid.SelectedIndex = 0;
        commandView?.Refresh();
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        BindingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        recording = BindingsGrid.SelectedItem as ShortcutRow;
        Message.Text = recording is null ? "请先选择一行。" : "正在等待输入：「" + recording.Name + "」。按快捷键、点击中键/侧键或滚动滚轮；Esc取消。";
    }
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        recording = null;
        if (BindingsGrid.SelectedItem is ShortcutRow row) row.Gesture = "";
    }
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (BindingsGrid.SelectedItem is not ShortcutRow row) return;
        var addition = new ShortcutRow(new(row.CommandId, ""), CommandCatalog.Find(row.CommandId, PresetSnapshot())) { IsAlternate = true };
        rows.Add(addition);
        BindingsGrid.SelectedItem = addition;
        BindingsGrid.ScrollIntoView(addition);
    }
    private void OnDefaultsClick(object sender, RoutedEventArgs e)
    {
        recording = null;
        BindingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); BindingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var unknown = rows.Where(row => !row.IsKnown).Select(row => row.ToBinding()).ToArray();
        CommandSearch.Text = "";
        Fill(CommandCatalog.DefaultBindings(PresetSnapshot()).Concat(unknown));
        Message.Text = "已在编辑副本中恢复默认，应用后才会保存。";
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (applying || !ApplyButton.IsEnabled) return;
        recording = null;
        BindingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        BindingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PresetsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PresetsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!double.TryParse(ZoomStepBox.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double step))
        { Message.Text = "请填写有效缩放倍数，例如 1.2。"; return; }
        AppSettings settings = session.Draft.Clone();
        settings.ZoomStep = step;
        settings.UndoJumpBack = UndoJumpBackCheck.IsChecked == true;
        settings.KeepViewOnImageChange = KeepViewCheck.IsChecked == true;
        settings.EdgeNavigationEnabled = EdgeNavigationCheck.IsChecked == true;
        settings.MouseNavigationRegion = MouseNavigationRegionBox.SelectedIndex == 1 ? "edges" : "halves";
        settings.MousePanButton = MousePanButtonBox.SelectedIndex == 1 ? "left" : "right";
        settings.Window.FullScreenAutoHide = FullscreenAutoHideCheck.IsChecked == true;
        settings.Window.RememberPlacement = RememberPlacementCheck.IsChecked == true;
        settings.ConflictPolicy = (FileConflictPolicy)ConflictPolicyBox.SelectedIndex;
        settings.Presets = PresetSnapshot();
        try { settings.Bindings = rows.Select(row => row.ToBinding()).ToList(); }
        catch (FormatException error) { Message.Text = error.Message; return; }
        if (SettingsValidation.Error(settings) is string errorMessage)
        { Message.Text = errorMessage; return; }
        if (settings.Bindings.Any(binding => binding.Enabled && CommandCatalog.Find(binding.CommandId, settings.Presets) is not null && !InputRouter.IsSupported(binding.Gesture)))
        { Message.Text = "存在无法识别或已为系统/输入法保留的按键，请重新设置。"; return; }
        applying = true;
        EditorRoot.IsEnabled = false;
        Message.Text = "正在保存，请稍候…";
        try { Message.Text = await session.ApplyAsync(settings, apply) ?? "已应用并保存。"; }
        finally { applying = false; EditorRoot.IsEnabled = true; ApplyButton.IsEnabled = !session.IsReadOnly; }
    }

    private void OnResetPageClick(object sender, RoutedEventArgs e)
    {
        if (applying) return;
        if (session.IsReadOnly) { Message.Text = "此配置只读，不能修改。"; return; }
        if (AppearanceTab.IsSelected) { session.ResetAppearance(); AppearanceEditor.Load(session.Draft.Appearance); DetailsEditor.Load(session.Draft.Appearance); }
        else if (DetailsContentTab.IsSelected) { DetailsEditor.ResetLayout(); }
        else if (ViewingTab.IsSelected)
        { KeepViewCheck.IsChecked = false; ZoomStepBox.Text = "1.2"; EdgeNavigationCheck.IsChecked = true; MouseNavigationRegionBox.SelectedIndex = MousePanButtonBox.SelectedIndex = 0; FullscreenAutoHideCheck.IsChecked = true; RememberPlacementCheck.IsChecked = true; }
        else if (ShortcutsTab.IsSelected) { OnDefaultsClick(sender, e); return; }
        else if (TargetsTab.IsSelected) { ConflictPolicyBox.SelectedIndex = 0; UndoJumpBackCheck.IsChecked = true; }
        Message.Text = "仅本页选项草稿恢复默认，文件夹预设和路径保持不变；应用后才保存。";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (recording is null) return;
        if (e.Key == Key.Escape) { recording = null; Message.Text = "录制已取消。"; e.Handled = true; return; }
        string? gesture = InputRouter.KeyboardGesture(e);
        if (gesture is null) return;
        e.Handled = true;
        if (!e.IsRepeat) SetRecorded(gesture);
    }
    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (recording is null) return;
        string? gesture = InputRouter.MouseGesture(e.ChangedButton);
        if (gesture is null) return;
        e.Handled = true;
        SetRecorded(gesture);
    }
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (recording is null) return;
        string? gesture = InputRouter.MakeGesture(e.Delta > 0 ? "WHEELUP" : "WHEELDOWN");
        if (gesture is null) return;
        e.Handled = true;
        SetRecorded(gesture);
    }
    private void SetRecorded(string gesture)
    {
        if (recording is null) return;
        var conflicts = rows.Where(row => row != recording && row.Enabled &&
            row.IsKnown && SameGesture(row.Gesture, gesture)).ToArray();
        if (conflicts.Length > 0)
        {
            string names = string.Join("、", conflicts.Select(row => row.Name).Distinct());
            if (MessageBox.Show(this, $"此输入已用于「{names}」。是否替换原绑定？", "快捷键冲突",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            { recording = null; Message.Text = "已取消替换，原绑定保持不变。"; return; }
            foreach (var conflict in conflicts) conflict.Gesture = "";
        }
        recording.Gesture = ShortcutGesture.Display(gesture);
        recording = null;
        Message.Text = "已录制。应用并保存后生效。";
    }
    private static bool SameGesture(string first, string second)
    {
        try { return ShortcutGesture.Normalize(first) == second; } catch (FormatException) { return false; }
    }

    private List<FolderPreset> PresetSnapshot() => presets.Select(row => row.ToPreset()).ToList();
    public void FocusPreset(string id)
    {
        TargetsTab.IsSelected = true;
        PresetsGrid.SelectedItem = presets.FirstOrDefault(preset => preset.Id == id);
        if (PresetsGrid.SelectedItem is not null) PresetsGrid.ScrollIntoView(PresetsGrid.SelectedItem);
    }
    public void FocusSection(int index) => SettingsTabs.SelectedIndex = FrameworkApi.Clamp(index, 0, SettingsTabs.Items.Count-1);
    public void FocusAbout() => BackupTab.IsSelected = true;
    private void AddPresetRow(FolderPreset preset)
    {
        var row = new FolderPresetRow(preset);
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FolderPresetRow.Name)) SyncCommands(); };
        presets.Add(row);
    }
    private void SyncCommands()
    {
        var current = PresetSnapshot();
        foreach (var row in rows) row.SetDefinition(CommandCatalog.Find(row.CommandId, current));
        foreach (var command in CommandCatalog.Definitions(current))
            if (!rows.Any(row => row.CommandId == command.Id)) rows.Add(new(new(command.Id, ""), command));
    }
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == SettingsTabs && BindingsGrid is not null && PresetsGrid is not null) SyncCommands();
        if (ResetPageButton is not null && AppearanceTab is not null) ResetPageButton.IsEnabled = AppearanceTab.IsSelected || DetailsContentTab.IsSelected || ViewingTab.IsSelected || ShortcutsTab.IsSelected || TargetsTab.IsSelected;
        if (e.Source == SettingsTabs) CancelRecording();
        if (e.Source == SettingsTabs && DefaultAppsTab is not null && DefaultAppsTab.IsSelected) _ = RefreshRegistrationAsync();
    }
    private void OnAddPresetClick(object sender, RoutedEventArgs e)
    {
        AddPresetRow(new FolderPreset { Name = $"文件夹{presets.Count + 1}" });
        PresetsGrid.SelectedItem = presets[presets.Count - 1];
        SyncCommands();
    }
    private void OnRemovePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetsGrid.SelectedItem is not FolderPresetRow selected) return;
        var bindings = rows.Where(row => CommandCatalog.TryPresetAction(row.CommandId, out string id, out _) && id == selected.Id).ToArray();
        if (MessageBox.Show(this, $"移除预设「{selected.Name}」及关联快捷键？仅修改设置，不删除目录或照片。", "移除预设",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var binding in bindings) rows.Remove(binding);
        presets.Remove(selected);
        SyncCommands();
    }
    private void OnMovePresetUpClick(object sender, RoutedEventArgs e) => MovePreset(-1);
    private void OnMovePresetDownClick(object sender, RoutedEventArgs e) => MovePreset(1);
    private void MovePreset(int direction)
    {
        if (PresetsGrid.SelectedItem is not FolderPresetRow selected) return;
        int index = presets.IndexOf(selected), next = index + direction;
        if (next >= 0 && next < presets.Count) presets.Move(index, next);
    }
    private void OnChoosePresetFolderClick(object sender, RoutedEventArgs e)
    {
        if (PresetsGrid.SelectedItem is not FolderPresetRow selected) { Message.Text = "请先选择目标预设。"; return; }
        var dialog = new Platform.FolderDialog { Title = $"选择「{selected.Name}」的目标文件夹" };
        if (!FrameworkApi.IsNullOrWhiteSpace(selected.Path) && Directory.Exists(selected.Path)) dialog.InitialDirectory = selected.Path;
        if (dialog.ShowDialog(this) == true) selected.Path = dialog.FolderName;
    }
    private async void OnCreatePresetFolderClick(object sender, RoutedEventArgs e)
    {
        if (PresetsGrid.SelectedItem is not FolderPresetRow selected) return;
        PresetsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        string path = selected.Path;
        if (FrameworkApi.IsNullOrWhiteSpace(path) || !FrameworkApi.IsPathFullyQualified(path))
        { Message.Text = "请先填写完整绝对路径。"; return; }
        try { await Task.Run(() => Directory.CreateDirectory(path)); Message.Text = "目录已存在或已创建。请应用保存预设。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Message.Text = "未创建目录：" + error.Message; }
    }

    private void CancelRecording()
    {
        if (recording is null) return;
        recording = null; if (Message is not null) Message.Text = "录制已取消。";
    }
    private void OnCommandSearch(object sender, TextChangedEventArgs e)
    {
        if (commandView is null) return;
        BindingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); BindingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        commandView.Refresh();
    }
    private async void OnCheckPresetPathClick(object sender, RoutedEventArgs e)
    {
        if (PresetsGrid.SelectedItem is not FolderPresetRow row) { Message.Text = "请先选择预设。"; return; }
        await row.RefreshPathStatusAsync(); Message.Text = "路径状态已检查，没有创建或修改文件夹。";
    }
    private void OnPresetCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is FolderPresetRow row) Dispatcher.InvokeAsync(async () => await row.RefreshPathStatusAsync());
    }
    private void OnEditPresetBindings(object sender, RoutedEventArgs e)
    {
        if (PresetsGrid.SelectedItem is not FolderPresetRow row) { Message.Text = "请先选择预设。"; return; }
        ShortcutsTab.IsSelected = true; CommandSearch.Text = row.Name;
    }
    private async void OnOpenRecoveryClick(object sender, RoutedEventArgs e)
    { if (openRecovery is not null && !applying) await openRecovery(); }
    private void OnOpenConfigDirectory(object sender, RoutedEventArgs e)
    {
        string directory = LightView.App.Persistence.AppPaths.UserData;
        if (!Directory.Exists(directory)) { Message.Text = "尚未生成配置目录，应用设置后可打开。"; return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Message.Text = "未打开配置目录：" + error.Message; }
    }
    private async void OnExportSettings(object sender, RoutedEventArgs e)
    {
        if (applying || session.IsReadOnly) return;
        var dialog = new SaveFileDialog { Title = "导出已应用的配置", Filter = "InfinityView配置包|*.json", DefaultExt = ".json", AddExtension = true,
            FileName = "InfinityView-settings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = session.LastAppliedSnapshot; bool paths = IncludePathsCheck.IsChecked == true;
        applying = true; EditorRoot.IsEnabled = false;
        try
        {
            await Task.Run(() => SettingsTransfer.Export(dialog.FileName, snapshot, paths, Path.Combine(AppPaths.UserData, "settings.json")));
            Message.Text = "配置已导出。" + (paths ? "包含本机文件夹路径。" : "不含文件夹路径。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or JsonException)
        { Message.Text = "未导出配置：" + error.Message + " 已有文件不会覆盖，请选择新文件名。"; }
        finally { applying = false; EditorRoot.IsEnabled = true; }
    }
    private async void OnImportSettings(object sender, RoutedEventArgs e)
    {
        if (applying || session.IsReadOnly || applyImported is null) return;
        CancelRecording();
        BindingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); BindingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PresetsGrid.CommitEdit(DataGridEditingUnit.Cell, true); PresetsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var file = new OpenFileDialog { Title = "预览配置备份", Filter = "InfinityView配置包|*.json", Multiselect = false, CheckFileExists = true };
        if (file.ShowDialog(this) != true) return;
        SettingsImportPreview incoming;
        applying = true; EditorRoot.IsEnabled = false;
        try { incoming = await Task.Run(() => SettingsTransfer.Read(file.FileName)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or JsonException or InvalidOperationException)
        { Message.Text = "未导入配置：" + error.Message; return; }
        finally { applying = false; EditorRoot.IsEnabled = true; }
        var preview = new Settings.SettingsImportWindow(file.FileName, session.LastAppliedSnapshot, incoming) { Owner = this };
        if (preview.ShowDialog() != true || preview.SelectedSettings is not { } selected) { Message.Text = "已取消导入，配置与运行设置保持不变。"; return; }
        Message.Text = await CommitImportedAsync(selected) ?? "配置已导入并应用；如有原配置，已保留导入前备份。";
    }
    internal async Task<string?> CommitImportedAsync(AppSettings selected)
    {
        if (applying || session.IsReadOnly || applyImported is null) return "当前无法应用导入。";
        applying = true; EditorRoot.IsEnabled = false;
        try
        {
            string? error = await session.ApplyAsync(selected, applyImported, preserveDraftOnFailure: true);
            if (error is null) ReloadEditor(session.LastAppliedSnapshot);
            return error;
        }
        finally { applying = false; EditorRoot.IsEnabled = true; ApplyButton.IsEnabled = !session.IsReadOnly; }
    }
    private void ReloadEditor(AppSettings settings)
    {
        CommandSearch.Text = ""; presets.Clear(); foreach (var preset in settings.Presets) AddPresetRow(preset);
        Fill(settings.Bindings); AppearanceEditor.Load(settings.Appearance); DetailsEditor.Load(settings.Appearance);
        ZoomStepBox.Text = settings.ZoomStep.ToString(CultureInfo.InvariantCulture);
        UndoJumpBackCheck.IsChecked = settings.UndoJumpBack; KeepViewCheck.IsChecked = settings.KeepViewOnImageChange;
        EdgeNavigationCheck.IsChecked = settings.EdgeNavigationEnabled;
        MouseNavigationRegionBox.SelectedIndex = settings.MouseNavigationRegion == "edges" ? 1 : 0;
        MousePanButtonBox.SelectedIndex = settings.MousePanButton == "left" ? 1 : 0;
        FullscreenAutoHideCheck.IsChecked = settings.Window.FullScreenAutoHide; RememberPlacementCheck.IsChecked = settings.Window.RememberPlacement;
        ConflictPolicyBox.SelectedIndex = (int)settings.ConflictPolicy;
    }
    private async Task RefreshRegistrationAsync()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "InfinityView.exe");
        var states = await Task.Run(Platform.AppRegistration.QueryDefaults).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (closed) return;
            bool stable = Platform.AppRegistration.IsStableInstallation(executable);
            RegistrationStatusText.Text = "当前运行：InfinityView " + VersionText.Text + Environment.NewLine + executable + Environment.NewLine +
                (stable ? "已从日常安装位置运行。" : "当前从临时位置运行。请先安装日常版，再修复注册。") + Environment.NewLine +
                Platform.AppRegistration.DescribeDefaults(states) + Environment.NewLine + string.Join(Environment.NewLine, states.Select(Platform.AppRegistration.DescribeAssociation));
            RepairRegistrationButton.IsEnabled = stable && !session.IsReadOnly;
        });
    }
    private async void OnRefreshRegistration(object sender, RoutedEventArgs e) => await RefreshRegistrationAsync();
    private void OnOpenDefaults(object sender, RoutedEventArgs e)
    {
        try { Platform.AppRegistration.OpenDefaultSettings(); Message.Text = "请在 Windows 中选择 InfinityView；返回后会重新检查真实关联。"; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Message.Text = "未打开 Windows 设置：" + error.Message; }
    }
    private async void OnRepairRegistration(object sender, RoutedEventArgs e)
    {
        if (applying) return;
        applying = true; EditorRoot.IsEnabled = false;
        try
        {
            await Task.Run(() => new Platform.AppRegistration(new Platform.WindowsRegistrationStore(), Path.Combine(AppContext.BaseDirectory, "InfinityView.exe"), AppPaths.UserData).Register());
            Message.Text = "InfinityView注册已修复，默认应用仍需在Windows中选择。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.Security.SecurityException)
        { Message.Text = "未修复注册：" + error.Message; }
        finally { applying = false; EditorRoot.IsEnabled = true; await RefreshRegistrationAsync(); }
    }
}

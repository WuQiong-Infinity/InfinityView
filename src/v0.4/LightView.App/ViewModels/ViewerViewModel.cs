using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using LightView.App.Imaging;
using LightView.App.Platform;
using LightView.Core.Settings;

namespace LightView.App.ViewModels;

public sealed class ViewerViewModel(IImageLoader loader) : INotifyPropertyChanged
{
    private LoadedImage? displayedImage;
    private bool isLoading;
    private bool showLoadingIndicator;
    private string errorMessage = "";
    private int requestVersion;
    private int folderRequestVersion;
    private ImageFolder? folder;
    private string? requestedPath;
    private string? lastDisplayedDirectory;
    private string navigationMessage = "";
    private CancellationTokenSource? requestCancellation;
    private readonly object prefetchSync = new();
    private CancellationTokenSource? prefetchCancellation;
    private readonly HashSet<CancellationTokenSource> activePrefetch = new();
    private readonly HashSet<Task> activePrefetchTasks = new();
    private string? prefetchPath;
    private int preferredDirection = 1;
    private volatile bool prefetchSuspended;
    private volatile bool prefetchEligible = true;
    private long lastAdjacentTick;
    internal int LastUpgradeDelay { get; private set; }
    internal int PrefetchNeighborLimit { get; set; } = 16;
    private ImageViewIntent requestedIntent;
    internal Action? ComparisonBudgetFallback { get; set; }
    internal Action<LoadedImage>? DiscardDecoded { get; set; } = loader is WicImageLoader wic ? wic.ComparisonPolicy.ReleaseDecoded : null;
    internal Func<Func<bool>>? BudgetRecoveryFactory { get; set; }
    private readonly Dictionary<string, int> invalidVersions = new();
    private bool displayedInvalid;
    private static string VersionKey(LightView.App.Organizing.FileSnapshot v) => v.Identity + ":" + v.Length + ":" + v.LastWrite + ":" + v.Sha256;
    internal bool IsPrefetchSuspended => prefetchSuspended;
    internal ImageViewIntent CurrentRequestIntent => isLoading ? requestedIntent : ImageViewIntent.Refresh;
    private TaskCompletionSource fullDemand = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FullReady { get; private set; } = Task.CompletedTask;
    public int PreviewMaximumEdge { get; set; } = 1536;
    public bool IsPreview => displayedImage?.IsPreview == true;
    public Task FolderReady { get; private set; } = Task.CompletedTask;
    public Task PrefetchReady { get; private set; } = Task.CompletedTask;
    public string? RequestedOrDisplayedPath => isLoading ? requestedPath : displayedImage?.FilePath;
    public int NavigationVersion => requestVersion;
    public string? ActiveDirectory => folder?.DirectoryPath ?? lastDisplayedDirectory;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LoadedImage? DisplayedImage => displayedImage;
    public LoadedImage? OrganizingImage => !isLoading && !displayedInvalid && displayedImage is { IsPreview: false, FileVersion: not null } ? displayedImage : null;
    public bool IsLoading => isLoading;
    public bool ShowLoadingIndicator => showLoadingIndicator;
    public bool IsEmpty => displayedImage is null;
    public bool IsDisplayedInvalid => displayedInvalid;
    public bool CanNavigate => folder is { Count: > 0 };
    public string ErrorMessage => errorMessage;
    public string FileName => displayedImage is null
        ? "InfinityView" : Path.GetFileName(displayedImage.FilePath);
    public string WindowTitle => displayedImage is null ? "InfinityView" : FileName + " — InfinityView";
    public string ImageSummary => displayedImage?.FileVersion is { Length: var length }
        ? ImageInfo + " · " + (length >= 1048576 ? $"{length / 1048576d:0.#} MiB" : length >= 1024 ? $"{length / 1024d:0.#} KiB" : $"{length} B")
        : ImageInfo;
    public string ImageInfo => displayedImage is null
        ? "JPG · JPEG · PNG · BMP · WebP"
        : $"{displayedImage.PixelWidth:N0} × {displayedImage.PixelHeight:N0}";
    private DetailsLayout? detailsLayout;
    internal void SetDetailsLayout(DetailsLayout? layout)
    { detailsLayout = DetailsCatalog.Normalize(layout); Raise(nameof(DetailsSections), nameof(DetailsRows)); }
    public IReadOnlyList<ImageDetail> DetailsRows => DetailsSections.SelectMany(section => section.Rows).ToArray();
    public IReadOnlyList<ImageDetailSection> DetailsSections
    {
        get
        {
            if (displayedImage is not { } image) return [new("文件", [new("图片详情", "尚未打开图片")])];
            var rows = new List<ImageDetail>
            {
                new("文件名", Path.GetFileName(image.FilePath), "file.name"), new("位置", Path.GetDirectoryName(image.FilePath) ?? image.FilePath, "file.directory"),
                new("像素尺寸", $"{image.PixelWidth:N0} × {image.PixelHeight:N0}", "file.dimensions")
            };
            if (image.FileVersion is { } version)
            {
                rows.Add(new("文件大小", version.Length < 1024 ? $"{version.Length:N0} 字节"
                    : version.Length < 1048576 ? $"{version.Length / 1024.0:0.##} KiB" : $"{version.Length / 1048576.0:0.##} MiB", "file.size"));
                rows.Add(new("修改时间", DateTime.FromFileTimeUtc(version.LastWrite).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "file.modified"));
            }
            var sections = new List<ImageDetailSection> { new("文件", rows.AsReadOnly(), "file") };
            if (image.Metadata is { } metadata)
            {
                static bool Camera(ImageDetail row) => row.Label is "相机厂商" or "相机型号" or "镜头厂商" or "镜头型号" or "拍摄时间";
                var camera = metadata.Shooting.Where(row => Camera(row) && row.Id != "camera.make" && !(row.Id is null && row.Label == "相机厂商")).ToArray();
                string? focal = metadata.Shooting.FirstOrDefault(row => row.Label == "焦距")?.Value;
                string? equivalent = metadata.Shooting.FirstOrDefault(row => row.Label == "等效35mm焦距")?.Value;
                static string CompactMillimeters(string value) => value.Replace(" mm", "mm", StringComparison.Ordinal);
                var exposure = metadata.Shooting.Where(row => !Camera(row) && (row.Label != "等效35mm焦距" || focal is null))
                    .Select(row => row.Label switch
                    {
                        "焦距" => new ImageDetail("焦距", CompactMillimeters(row.Value) +
                            (equivalent is null ? "" : $"（等效{CompactMillimeters(equivalent)}）"), row.Id),
                        "等效35mm焦距" => new ImageDetail("等效焦距", CompactMillimeters(row.Value), row.Id is null ? null : "exposure.focal"),
                        _ => row
                    }).ToArray();
                if (camera.Length > 0) sections.Add(new("拍摄设备与时间", camera, "camera"));
                if (exposure.Length > 0) sections.Add(new("曝光与拍摄参数", exposure, "exposure"));
                if (metadata.Style?.Rows is { Count: > 0 } style) sections.Add(new("照片风格", style, "style"));
                if (metadata.Shooting.Count == 0) sections.Add(new("拍摄", [new("拍摄信息", "无可读取的 EXIF 拍摄信息")]));
                if (metadata.Warning is not null) sections.Add(new("元数据提示", [new("提示", metadata.Warning)]));
            }
            else sections.Add(new("拍摄", [new("拍摄信息", "无可读取的拍摄信息")]));
            return DetailsProjection.Apply(sections.AsReadOnly(), detailsLayout);
        }
    }
    public string ImagePosition => folder is not null && displayedImage is not null
        ? $"{folder.IndexOf(displayedImage.FilePath) + 1} / {folder.Count}" : "";
    public string LoadStatus => displayedInvalid ? "文件已变化，请重新打开后再整理" : IsPreview ? "快速预览 · 原图就绪前无法整理" : isLoading ? "正在读取图片…"
        : !FrameworkApi.IsNullOrEmpty(navigationMessage) ? navigationMessage : ImagePosition;
    public string ColorStatus => displayedImage is null
        ? "色彩验证尚未执行"
        : displayedImage.ColorDescription ?? (displayedImage.EmbeddedProfileCount > 0
            ? "色彩：嵌入 ICC → sRGB（SDR）；设备显示验证未完成"
            : "色彩：无嵌入 ICC，按 sRGB 假设；设备显示验证未完成");

    public Task OpenAsync(string path, ImageViewIntent intent = ImageViewIntent.Open) => FullReady = OpenCoreAsync(path, intent);

    public async Task<bool> EnsureFullAsync()
    {
        int version = requestVersion;
        fullDemand.TrySetResult();
        await FullReady;
        return version == requestVersion && !isLoading && displayedImage is { IsPreview: false };
    }

    private async Task OpenCoreAsync(string path, ImageViewIntent intent, bool budgetRetried = false)
    {
        int version = ++requestVersion;
        var recoverBudget = BudgetRecoveryFactory?.Invoke();
        var demand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fullDemand = demand;
        lock (prefetchSync)
        {
            // If this exact image is already decoding, let it populate the cache
            // rather than cancelling it and decoding the same file again.
            if (!StringComparer.OrdinalIgnoreCase.Equals(prefetchPath, path))
                prefetchCancellation?.Cancel();
        }
        requestCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        requestCancellation = cancellation;
        long now = Stopwatch.GetTimestamp();
        LastUpgradeDelay = intent == ImageViewIntent.Adjacent && lastAdjacentTick != 0 &&
            (now - lastAdjacentTick) * 1000d / Stopwatch.Frequency <= 180 ? 90 : 0;
        lastAdjacentTick = intent == ImageViewIntent.Adjacent ? now : 0;
        var upgradeBuffer = Task.Delay(LastUpgradeDelay, cancellation.Token);
        requestedPath = path;
        requestedIntent = intent;
        isLoading = true;
        showLoadingIndicator = false;
        errorMessage = "";
        navigationMessage = "";
        Raise(nameof(IsLoading), nameof(ShowLoadingIndicator), nameof(LoadStatus), nameof(OrganizingImage), nameof(ErrorMessage));
        _ = ShowLoadingIfSlowAsync(version, cancellation.Token);
        try
        {
            int edge = PreviewMaximumEdge;
            var image = await Task.Run(() => loader is IProgressiveImageLoader progressive
                ? progressive.LoadPreview(path, edge, cancellation.Token) : loader.Load(path, cancellation.Token), cancellation.Token);
            if (version != requestVersion) { DiscardDecoded?.Invoke(image); return; }
            image = image with { ViewIntent = intent };
            Publish(image);
            FolderReady = LoadFolderAsync(image);
            if (image.IsPreview)
            {
                await Task.WhenAny(upgradeBuffer, demand.Task);
                cancellation.Token.ThrowIfCancellationRequested();
                var full = await Task.Run(() => loader.Load(path, cancellation.Token), cancellation.Token);
                if (version != requestVersion) { DiscardDecoded?.Invoke(full); return; }
                if (full.IsPreview || image.FileVersion is null || full.FileVersion is null || !image.FileVersion.SameVersion(full.FileVersion))
                { DiscardDecoded?.Invoke(full); throw new IOException("图片在预览后发生变化，请重新打开。整理仍不可用。"); }
                Publish(full with { ViewIntent = intent });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ComparisonBudgetExceededException) when (!budgetRetried && (ComparisonBudgetFallback is not null || recoverBudget is not null))
        {
            if (version != requestVersion) return;
            bool retry = recoverBudget is null;
            if (recoverBudget is not null) retry = recoverBudget(); else ComparisonBudgetFallback?.Invoke();
            if (retry && version == requestVersion) await OpenCoreAsync(path, intent, true);
            else if (version == requestVersion) { errorMessage = "图片超出双图预算；活动窗格已改变，本次未自动退出或重试。"; Raise(nameof(ErrorMessage)); }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or FormatException)
        {
            if (version != requestVersion) return;
            errorMessage = error switch
            {
                UnauthorizedAccessException => "无法读取此文件，请检查访问权限。",
                FileNotFoundException or DirectoryNotFoundException => "图片文件不存在。",
                FileFormatException => "无法识别图片内容，文件可能已损坏。",
                _ => error.Message
            };
            Raise(nameof(ErrorMessage));
        }
        finally
        {
            if (version == requestVersion)
            {
                isLoading = false;
                showLoadingIndicator = false;
                Raise(nameof(IsLoading), nameof(ShowLoadingIndicator), nameof(LoadStatus), nameof(OrganizingImage));
                TryStartPrefetch();
            }
            if (ReferenceEquals(requestCancellation, cancellation)) requestCancellation = null;
            cancellation.Dispose();
        }
    }

    private void Publish(LoadedImage image)
    {
        displayedImage = image;
        displayedInvalid = image.FileVersion is { } file && invalidVersions.TryGetValue(VersionKey(file), out int invalidAt) && requestVersion <= invalidAt;
        if (!displayedInvalid && image.FileVersion is { } fresh) invalidVersions.Remove(VersionKey(fresh));
        lastDisplayedDirectory = Path.GetDirectoryName(image.FilePath);
        if (image.IsPreview) showLoadingIndicator = false;
        try
        {
            Raise(nameof(DisplayedImage), nameof(IsPreview), nameof(IsEmpty), nameof(IsDisplayedInvalid), nameof(FileName),
                  nameof(ImageInfo), nameof(ColorStatus), nameof(DetailsRows), nameof(DetailsSections),
                  nameof(LoadStatus), nameof(ShowLoadingIndicator), nameof(OrganizingImage));
        }
        finally { DiscardDecoded?.Invoke(image); }
    }

    public async Task NavigateAsync(int direction)
    {
        if (folder is null || displayedImage is null) return;
        preferredDirection = direction;
        string current = isLoading && requestedPath is not null ? Path.GetFullPath(requestedPath) : displayedImage.FilePath;
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(current), folder.DirectoryPath)) return;
        string? next = folder.Neighbor(current, direction);
        if (next is null)
        {
            navigationMessage = direction > 0 ? "已经是最后一张" : "已经是第一张";
            Raise(nameof(LoadStatus));
            return;
        }
        bool refresh = !File.Exists(current);
        await OpenAsync(next, ImageViewIntent.Adjacent);
        if (refresh && displayedImage is not null)
            FolderReady = LoadFolderAsync(displayedImage, true);
    }

    private async Task ShowLoadingIfSlowAsync(int version, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(150, cancellation);
            if (version != requestVersion || !isLoading || IsPreview) return;
            showLoadingIndicator = true;
            Raise(nameof(ShowLoadingIndicator));
        }
        catch (OperationCanceledException) { }
    }

    public void Notify(string message)
    {
        navigationMessage = message;
        Raise(nameof(LoadStatus));
    }

    public async Task ApplyMoveAsync(LoadedImage original, LightView.App.Organizing.FileSnapshot destination, bool advance, int? navigationVersion = null)
    {
        await FolderReady;
        if (!ReferenceEquals(OrganizingImage, original))
        {
            if (displayedImage is not null) FolderReady = LoadFolderAsync(displayedImage, true);
            return;
        }
        advance &= navigationVersion is null || navigationVersion == requestVersion;
        string? next = advance ? folder?.Neighbor(original.FilePath, 1) ?? folder?.Neighbor(original.FilePath, -1) : null;
        displayedImage = original with { FilePath = destination.Path, FileVersion = destination };
        Raise(nameof(DisplayedImage), nameof(FileName), nameof(ImagePosition), nameof(DetailsRows), nameof(DetailsSections));
        if (advance && next is not null)
        {
            await OpenAsync(next, ImageViewIntent.Adjacent);
            if (displayedImage is not null) FolderReady = LoadFolderAsync(displayedImage, true);
        }
        else if (advance)
        {
            displayedImage = null;
            folder = null;
            requestedPath = null;
            ++folderRequestVersion;
            Raise(nameof(DisplayedImage), nameof(IsEmpty), nameof(FileName), nameof(ImageInfo), nameof(ColorStatus), nameof(CanNavigate), nameof(ImagePosition), nameof(DetailsRows), nameof(DetailsSections));
        }
        else
        {
            lastDisplayedDirectory = Path.GetDirectoryName(destination.Path);
            FolderReady = LoadFolderAsync(displayedImage, true);
        }
    }

    internal async Task ApplyRecycleAsync(LoadedImage original, int navigation)
    {
        await FolderReady;
        InvalidateVersion(original.FileVersion!);
        if (!ReferenceEquals(displayedImage, original) || requestVersion != navigation)
        { await RefreshFolderAsync(); return; }
        string? next = folder?.Neighbor(original.FilePath, 1) ?? folder?.Neighbor(original.FilePath, -1);
        if (next is not null) { await OpenAsync(next, ImageViewIntent.Adjacent); return; }
        displayedImage = null; folder = null; requestedPath = null; ++folderRequestVersion;
        Raise(nameof(DisplayedImage), nameof(OrganizingImage), nameof(IsEmpty), nameof(FileName), nameof(WindowTitle), nameof(ImageInfo), nameof(CanNavigate), nameof(ImagePosition), nameof(DetailsRows), nameof(DetailsSections));
    }

    internal Task SeedAsync(LoadedImage snapshot)
    {
        ++requestVersion; displayedInvalid = false; Publish(snapshot with { ViewIntent = ImageViewIntent.Open });
        FullReady = Task.CompletedTask; return FolderReady = LoadFolderAsync(snapshot, true);
    }
    internal void InvalidateVersion(LightView.App.Organizing.FileSnapshot version)
    {
        invalidVersions[VersionKey(version)] = requestVersion;
        if (displayedImage?.FileVersion is { } shown && shown.SameVersion(version)) displayedInvalid = true;
        Raise(nameof(OrganizingImage), nameof(LoadStatus), nameof(IsDisplayedInvalid));
    }
    internal void Detach()
    {
        prefetchEligible = false;
        ++requestVersion; ++folderRequestVersion; requestCancellation?.Cancel(); CancelPrefetch();
        displayedImage = null; folder = null; requestedPath = null; isLoading = false;
        Raise(nameof(DisplayedImage), nameof(OrganizingImage));
    }
    internal Task RefreshFolderAsync() => displayedImage is null ? Task.CompletedTask : (FolderReady = LoadFolderAsync(displayedImage, true));

    internal async Task SuspendPrefetchAsync()
    {
        prefetchSuspended = true;
        Task[] pending;
        lock (prefetchSync)
        {
            pending = activePrefetchTasks.ToArray();
            foreach (var cancellation in activePrefetch.ToArray()) cancellation.Cancel();
        }
        await Task.WhenAll(pending);
    }
    internal void ResumePrefetch()
    {
        prefetchSuspended = false;
        TryStartPrefetch();
    }
    internal void SetPrefetchEligibility(bool value)
    {
        prefetchEligible = value;
        if (!value) CancelPrefetch(); else TryStartPrefetch();
    }
    private void TryStartPrefetch()
    {
        if (prefetchSuspended || !prefetchEligible || isLoading || errorMessage.Length != 0 ||
            displayedImage is not { IsPreview: false } image || loader is not IImagePrefetcher prefetcher) return;
        lock (prefetchSync) if (activePrefetch.Count > 0) return;
        PrefetchReady = StartPrefetch(prefetcher, image, requestVersion, PreviewMaximumEdge);
    }
    public void CancelPrefetch()
    {
        lock (prefetchSync) foreach (var cancellation in activePrefetch.ToArray()) cancellation.Cancel();
    }
    private Task StartPrefetch(IImagePrefetcher prefetcher, LoadedImage image, int version, int edge)
    {
        lock (prefetchSync)
        {
            if (prefetchSuspended || !prefetchEligible || image.IsPreview || isLoading || activePrefetch.Count > 0) return Task.CompletedTask;
            var cancellation = new CancellationTokenSource();
            activePrefetch.Add(cancellation); prefetchCancellation = cancellation;
            var task = PrefetchNeighborsAsync(prefetcher, image, version, cancellation, edge);
            activePrefetchTasks.Add(task);
            _ = task.ContinueWith(completed => { lock (prefetchSync) activePrefetchTasks.Remove(completed); }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task PrefetchNeighborsAsync(IImagePrefetcher prefetcher, LoadedImage image, int version, CancellationTokenSource cancellation, int edge)
    {
        try
        {
            await FolderReady;
            await Task.Delay(80, cancellation.Token);
            if (prefetchSuspended || !prefetchEligible || version != requestVersion || !ReferenceEquals(displayedImage, image) || folder is null) return;
            bool previews = loader is IProgressiveImageLoader && Path.GetExtension(image.FilePath).ToLowerInvariant() is ".jpg" or ".jpeg";
            var neighbors = new List<string?>();
            string? cursor = folder.Neighbor(image.FilePath, preferredDirection);
            neighbors.Add(cursor);
            neighbors.Add(folder.Neighbor(image.FilePath, -preferredDirection));
            for (int i = 2; previews && i < PrefetchNeighborLimit && cursor is not null; i++)
            { cursor = folder.Neighbor(cursor, preferredDirection); neighbors.Add(cursor); }
            await Task.Run(() =>
            {
                foreach (string? neighbor in neighbors)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (prefetchSuspended || !prefetchEligible || version != Volatile.Read(ref requestVersion)) return;
                    if (neighbor is null) continue;
                    if (previews && Path.GetExtension(neighbor).ToLowerInvariant() is not (".jpg" or ".jpeg")) continue;
                    lock (prefetchSync)
                        if (ReferenceEquals(prefetchCancellation, cancellation)) prefetchPath = neighbor;
                    try
                    {
                        bool added = previews && loader is IProgressiveImageLoader progressive
                            ? progressive.TryPrefetchPreview(neighbor, edge, cancellation.Token)
                            : prefetcher.TryPrefetch(neighbor, cancellation.Token);
                        if (!added) return;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException) { }
                    finally
                    {
                        lock (prefetchSync)
                            if (ReferenceEquals(prefetchCancellation, cancellation)) prefetchPath = null;
                    }
                }
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            bool restart = cancellation.IsCancellationRequested && version == requestVersion;
            lock (prefetchSync)
            {
                if (ReferenceEquals(prefetchCancellation, cancellation)) prefetchCancellation = null;
                activePrefetch.Remove(cancellation);
                cancellation.Dispose();
            }
            if (restart) TryStartPrefetch();
        }
    }

    private async Task LoadFolderAsync(LoadedImage image, bool force = false)
    {
        string? directory = Path.GetDirectoryName(image.FilePath);
        if (FrameworkApi.IsNullOrEmpty(directory)) return;
        if (!force && StringComparer.OrdinalIgnoreCase.Equals(folder?.DirectoryPath, directory))
        {
            Raise(nameof(ImagePosition), nameof(LoadStatus));
            return;
        }
        int version = ++folderRequestVersion;
        folder = null;
        Raise(nameof(CanNavigate), nameof(ImagePosition));
        try
        {
            var result = await Task.Run(() => ImageFolder.Read(directory));
            if (version != folderRequestVersion ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(displayedImage?.FilePath), directory)) return;
            folder = result;
            Raise(nameof(CanNavigate), nameof(ImagePosition), nameof(LoadStatus));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (version != folderRequestVersion) return;
            navigationMessage = "当前图片可查看，但目录列表暂不可用。";
            Raise(nameof(LoadStatus));
        }
    }

    public string ActivityStatus => IsPreview ? "" : isLoading ? "正在读取图片…" : navigationMessage ?? "";

    private void Raise(params string[] names)
    {
        foreach (string name in names) OnPropertyChanged(name);
        if (names.Contains(nameof(FileName))) OnPropertyChanged(nameof(WindowTitle));
        if (names.Contains(nameof(ImageInfo))) OnPropertyChanged(nameof(ImageSummary));
        if (names.Contains(nameof(LoadStatus))) OnPropertyChanged(nameof(ActivityStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using LightView.App.Imaging;

namespace LightView.App.ViewModels;

internal enum ComparisonState { Off, Preparing, Active }

// Two independent navigation owners; one shared decoder/cache policy and file service.
internal sealed class ComparisonSession : IDisposable
{
    private readonly ComparisonLoadPolicy policy;
    private readonly Action<bool> setMode;
    public ComparisonState State { get; private set; }
    public bool IsOn => State != ComparisonState.Off;
    public bool Linked { get; private set; } = true;
    public long Generation { get; private set; }
    public PaneContext? Left { get; private set; }
    public PaneContext? Right { get; private set; }
    public event Action? Changed;
    public ComparisonSession(ComparisonLoadPolicy policy, Action<bool> setMode) { this.policy = policy; this.setMode = setMode; }
    public async Task<bool> EnterAsync(PaneContext owner, Func<LoadedImage, PaneContext> createPeer, string target)
    {
        if (IsOn || !owner.Attached || owner.Viewer.DisplayedImage?.FileVersion is not { } version) return false;
        long own = ++Generation; int request = owner.Viewer.NavigationVersion;
        State = ComparisonState.Preparing; Right = owner; Changed?.Invoke();
        if (!await owner.Viewer.EnsureFullAsync() || own != Generation || !owner.Attached || request != owner.Viewer.NavigationVersion)
        { if (own == Generation) Exit(owner); return false; }
        await owner.Viewer.SuspendPrefetchAsync();
        if (own != Generation || !owner.Attached || request != owner.Viewer.NavigationVersion) return false;
        var image = owner.Viewer.DisplayedImage;
        if (!owner.CanOrganize || image is not { IsPreview: false, FileVersion: not null } || !version.SameVersion(image.FileVersion) || image.DisplayTargetKey != target || !FrameworkApi.IsNullOrEmpty(owner.Viewer.ErrorMessage))
        { Exit(owner); return false; }
        if (ComparisonBudget.Cost(image) > ComparisonBudget.PerImage) { Exit(owner); owner.Viewer.Notify("图片超出双图呈现预算，保留单图查看。"); return false; }
        var peer = createPeer(image);
        await peer.Viewer.SuspendPrefetchAsync();
        await peer.Viewer.SeedAsync(image);
        if (own != Generation || !owner.Attached) { peer.Attached = false; peer.Viewer.Detach(); return false; }
        Left = peer; Linked = true;
        policy.SetSources(owner.Viewer.DisplayedImage, peer.Viewer.DisplayedImage);
        setMode(true); State = ComparisonState.Active; UpdateResidency();
        peer.Viewer.PrefetchNeighborLimit = owner.Viewer.PrefetchNeighborLimit = 3;
        peer.Viewer.SetPrefetchEligibility(false); owner.Viewer.SetPrefetchEligibility(true);
        peer.Viewer.ResumePrefetch(); owner.Viewer.ResumePrefetch();
        Changed?.Invoke(); return true;
    }
    public void SetLinked(bool value) { Linked = value; Changed?.Invoke(); }
    public void UpdateResidency()
    { if (IsOn) policy.SetSources(Right?.Viewer.DisplayedImage, Left?.Viewer.DisplayedImage); }
    public void Exit(PaneContext keep)
    {
        ++Generation;
        var discard = ReferenceEquals(keep, Left) ? Right : Left;
        if (discard is not null) { discard.Attached = false; discard.Viewer.Detach(); }
        Left = Right = null; State = ComparisonState.Off; setMode(false);
        if (keep.Attached) { keep.Viewer.PrefetchNeighborLimit = 16; keep.Viewer.SetPrefetchEligibility(true); keep.Viewer.ResumePrefetch(); } Changed?.Invoke();
    }
    public void Dispose()
    {
        ++Generation;
        foreach (var pane in new[] { Left, Right }) if (pane is not null) { pane.Attached = false; pane.Viewer.Detach(); }
        Left = Right = null; State = ComparisonState.Off; setMode(false);
    }
}

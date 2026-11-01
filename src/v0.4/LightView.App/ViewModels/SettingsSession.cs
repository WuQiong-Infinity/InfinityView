using System.IO;
using LightView.Core.Settings;

namespace LightView.App.ViewModels;

internal sealed class SettingsSession
{
    private AppSettings applied;
    private readonly Action<AppearanceSettings> preview;
    public AppSettings Draft { get; private set; }
    public bool IsApplying { get; private set; }
    public bool IsReadOnly { get; }
    public AppSettings LastAppliedSnapshot => applied.Clone();

    public SettingsSession(AppSettings initial, Action<AppearanceSettings> preview, bool readOnly, AppearanceSettings? appliedAppearance = null)
    {
        applied = initial.Clone(); Draft = initial.Clone(); this.preview = preview; IsReadOnly = readOnly;
        if (appliedAppearance is not null) applied.Appearance = appliedAppearance.Clone();
    }
    public void PreviewAppearance() { if (!IsApplying && !IsReadOnly) preview(Draft.Appearance.Clone()); }
    public void ResetAppearance()
    {
        if (IsApplying) return;
        Draft.Appearance = new() { UnknownFields = Draft.Appearance.Clone().UnknownFields }; PreviewAppearance();
    }
    public bool TryClose()
    {
        if (IsApplying) return false;
        preview(applied.Appearance.Clone()); return true;
    }
    public async Task<string?> ApplyAsync(AppSettings candidate, Func<AppSettings, Task<string?>> saveAndApply, bool preserveDraftOnFailure = false)
    {
        if (IsApplying) return "正在保存，请稍候。";
        if (IsReadOnly) { preview(applied.Appearance.Clone()); return "此配置只读，无法覆盖。"; }
        if (SettingsValidation.Error(candidate) is string validation) { preview(applied.Appearance.Clone()); return validation; }
        var snapshot = candidate.Clone();
        var previousDraft = preserveDraftOnFailure ? Draft.Clone() : null;
        bool committed = false;
        Draft = snapshot.Clone(); IsApplying = true;
        try
        {
            string? error = await saveAndApply(snapshot);
            if (error is not null) return error;
            applied = snapshot.Clone(); Draft = snapshot.Clone();
            committed = true;
            preview(applied.Appearance.Clone());
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        { return "未保存设置：" + error.Message; }
        finally { if (!committed) { if (previousDraft is not null) Draft = previousDraft; preview(applied.Appearance.Clone()); } IsApplying = false; }
    }
}

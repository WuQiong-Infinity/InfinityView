using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using LightView.App.Organizing;
using LightView.App.Imaging;
using LightView.App.ViewModels;

namespace LightView.App.Views;

public partial class MainWindow
{
    private async Task RecycleImageAsync(PaneContext owner)
    {
        if (copyBusy) return;
        var image = owner.Viewer.OrganizingImage;
        if (!owner.CanOrganize || image?.FileVersion is null) { ShowOperationFeedback("请等待有效原图读取完成后再删除。", owner: owner); return; }
        int navigation = owner.Viewer.NavigationVersion; copyBusy = true; UpdateUndoButton();
        try
        {
            await RecycleService.RecycleAsync(image.FileVersion, new WindowInteropHelper(this).Handle);
            MarkPeerInvalid(owner, image.FileVersion);
            await owner.Viewer.ApplyRecycleAsync(image, navigation);
            ShowOperationFeedback("已移入回收站，可在回收站恢复。", true, "Completed", owner);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or COMException)
        { ShowOperationFeedback("无法移入回收站：" + error.Message, owner: owner); }
        finally { copyBusy = false; UpdateUndoButton(); }
    }
}

using System.Windows;

namespace LightView.App.Views.Settings;

public partial class AppearancePage
{
    public event Action? DetailsRequested;
    internal void SetReadOnly(bool readOnly) => DetailsLink.IsEnabled = !readOnly;
    private void OnOpenDetails(object sender,RoutedEventArgs e) => DetailsRequested?.Invoke();
}

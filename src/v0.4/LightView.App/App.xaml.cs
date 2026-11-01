using System.Windows;
using System.IO;
using LightView.App.Persistence;
using LightView.App.Platform;
using LightView.App.Views;

namespace LightView.App;

public partial class App : Application
{
    private SingleInstanceSession? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Styles.ThemeResources.Refresh(this);
        SystemParameters.StaticPropertyChanged += OnSystemAppearanceChanged;
        try
        {
            AppRegistration.SetProcessIdentity();
            if (e.Args.Length == 1 && e.Args[0] == "--check-running")
            { Shutdown(SingleInstanceSession.IsRunning(AppPaths.UserData) ? 2 : 0); return; }
            instance = new(AppPaths.UserData);
            if (e.Args.Length == 1 && e.Args[0] is "--register-install" or "--unregister-install")
            {
                if (!instance.IsPrimary) { Shutdown(2); return; }
                try
                {
                    var registration = new AppRegistration(new WindowsRegistrationStore(), Path.Combine(AppContext.BaseDirectory, "InfinityView.exe"), AppPaths.UserData);
                    if (e.Args[0] == "--register-install") registration.Register(); else registration.Unregister();
                    Shutdown(0);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or System.Security.SecurityException)
                {
                    try { Directory.CreateDirectory(AppPaths.UserData); File.AppendAllText(Path.Combine(AppPaths.UserData, "registration-errors.log"), DateTimeOffset.UtcNow.ToString("o") + " " + error.Message + Environment.NewLine); }
                    catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
                    Shutdown(1);
                }
                return;
            }
            if (!instance.IsPrimary)
            {
                string? path = e.Args.Length > 0 ? Path.GetFullPath(e.Args[0]) : null;
                bool accepted = await instance.ForwardAsync(path, Path.Combine(AppContext.BaseDirectory, "InfinityView.exe"));
                if (!accepted) MessageBox.Show(instance.LastForwardFailure ?? "已有InfinityView窗口尚未接收请求，请回到原窗口或稍后重试。", "InfinityView");
                Shutdown(accepted ? 0 : 1); return;
            }
            var window = new MainWindow(); MainWindow = window;
            AcceptanceProbe.Attach(window);
            window.Show();
            instance.Listen(path => Dispatcher.InvokeAsync(() => window.OpenExternalAsync(path)).Task.Unwrap());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show("无法启动InfinityView：" + error.Message, "InfinityView"); Shutdown(1); }
    }
    private void OnSystemAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(() => Styles.ThemeResources.Refresh(this));
    }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemAppearanceChanged;
        instance?.Dispose(); base.OnExit(e);
    }
}

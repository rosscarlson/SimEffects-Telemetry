using System.Windows;
using GtTelemetry.App.Theming;

namespace GtTelemetry.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // This app may sit next to azimuth.exe, beside the Render Aspect Ratio shim's dinput8.dll; load Windows' own
        // first so DirectInput uses that.
        try { System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(Environment.SystemDirectory, "dinput8.dll")); } catch { }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            MessageBox.Show(a.Exception.Message, "SimEffects Telemetry", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
        // --dev shows the address-finding tools; --page capture|scanner|watch|memory then opens on that page.
        bool dev = e.Args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(new AppState(settings), dev);
        MainWindow = window;
        int i = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < e.Args.Length) window.ShowPage(e.Args[i + 1]);
        window.Show();
    }
}

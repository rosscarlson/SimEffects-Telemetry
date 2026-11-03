using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using GtTelemetry.Core.Updates;

namespace GtTelemetry.App;

// The Updates card: compares this version with the GitHub releases a few seconds after the app opens and every few
// hours, and Update downloads the newest installer and runs it.
public partial class MainWindow
{
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private UpdateInfo? update;
    private bool updating;

    private static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    private void SetUpUpdates()
    {
        CurrentVersionText.Text = Show(CurrentVersion);
        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        updateTimer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += async (_, _) => { first.Stop(); await CheckForUpdatesAsync(); };
        first.Start();
    }

    private static string Show(Version v) => v.Build > 0 ? v.ToString(3) : v.ToString(2);

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (updating) return;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = "Checking for updates…";
        try
        {
            update = await UpdateChecker.CheckAsync(CurrentVersion);
            ShowUpdate();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UpdateStatus.Text = "Couldn't check for updates: " + (ex is TaskCanceledException ? "GitHub didn't answer." : ex.Message);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = !updating;
        }
    }

    private void ShowUpdate()
    {
        var u = update;
        if (u == null) return;
        LatestVersionText.Text = u.Latest == null ? "–" : Show(u.Latest.Version);
        BehindText.Text = u.Behind switch { 0 => "Up to date", 1 => "1 release", var n => $"{n} releases" };
        UpdateButton.Visibility = u.Available ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.ToolTip = u.Available && u.Latest!.Notes.Length > 0 ? u.Latest.Notes : null;
        UpdateStatus.Text = u.Latest == null ? "No releases published yet."
            : u.Available ? $"Version {Show(u.Latest.Version)} is available. Update installs it and restarts the app."
            : u.Latest.Version > u.Current ? $"Version {Show(u.Latest.Version)} is out but has no installer yet."
            : $"Up to date. Checked at {DateTime.Now:HH:mm}.";
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (update?.Latest is not { } release || updating) return;
        updating = true;
        UpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus.Text = $"Downloading {Show(release.Version)}… {p:P0}");
            UpdateStatus.Text = $"Downloading {Show(release.Version)}…";
            string path = await UpdateChecker.DownloadAsync(release, progress);
            UpdateStatus.Text = "Installing… the app restarts when it's done.";
            UpdateChecker.RunInstaller(path);
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Win32Exception: Windows' "allow this app" was answered No.
            UpdateStatus.Text = ex is System.ComponentModel.Win32Exception ? "The update was cancelled." : "The update didn't download: " + ex.Message;
            updating = false;
            UpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = true;
        }
    }
}

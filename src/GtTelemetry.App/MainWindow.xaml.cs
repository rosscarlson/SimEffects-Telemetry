using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GtTelemetry.App.Theming;
using GtTelemetry.App.Views;
using GtTelemetry.Core.Adapters;
using GtTelemetry.Core.Display;
using GtTelemetry.Core.Addresses;
using GtTelemetry.Core.Speech;

namespace GtTelemetry.App;

public sealed record FeatureRow(string Name, string SupportText, bool IsAvailable, string Note);

public partial class MainWindow : Window
{
    private readonly AppState state;
    private bool initializing = true;
    private string? pcsx2Data;
    private DisplayState? displayState;
    private readonly IAppView[] views;
    private readonly bool developer;

    /// <param name="developer">Shows the address-finding tools (guided capture, scanner, watch list, memory, voice and
    /// address map). End users only need telemetry and the game profile.</param>
    public MainWindow(AppState state, bool developer = false)
    {
        this.state = state;
        this.developer = developer;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/App.ico"));

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        SetUpUpdates();

        var s = state.Settings;
        ThemeBox.SelectedIndex = (int)s.Theme;
        AdapterBox.ItemsSource = AdapterCatalog.All;
        AdapterBox.SelectedItem = state.Adapter;
        SlotBox.Text = s.PineSlot.ToString();
        FastBox.IsChecked = s.FastSnapshots;
        SpeakBox.IsChecked = s.SpeakInstructions;
        var voices = SpeechAnnouncer.InstalledVoices();
        VoiceBox.ItemsSource = voices;
        VoiceBox.SelectedItem = voices.Contains(state.Voice.Voice ?? "") ? state.Voice.Voice : voices.FirstOrDefault();
        RateSlider.Value = s.VoiceRate;
        RateText.Text = FormatRate(s.VoiceRate);

        views = developer ? [TelemetryView, ControlsView, CaptureView, ScannerView, WatchView, MemoryView] : [TelemetryView, ControlsView];
        if (!developer)
        {
            VoiceCard.Visibility = MapCard.Visibility = FastBox.Visibility = FastNote.Visibility = Visibility.Collapsed;
            foreach (var tab in new UIElement[] { TabCapture, TabScanner, TabWatch, TabMemory }) tab.Visibility = Visibility.Collapsed;
            foreach (var dev in new UIElement[] { CaptureView, ScannerView, WatchView, MemoryView }) dev.Visibility = Visibility.Collapsed;
        }
        TelemetryView.AzimuthFolderChanged += ControlsView.FolderChanged;
        state.PropertyChanged += State_PropertyChanged;
        state.StatusChanged += ShowStatus;
        TelemetryView.RunningChanged += UpdateStartButton;
        foreach (var view in views) view.Attach(state);
        SetUpCamera();
        RefreshAdapter();
        RefreshConnection();
        RefreshMap();
        ShowHint();
        initializing = false;

        Loaded += async (_, _) =>
        {
            if (s.AutoConnect) await state.ConnectAsync();
        };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        ThemeManager.ApplyTitleBar(this);
        SetUpStartStop();
    }

    private void Window_Activated(object? sender, EventArgs e) => TelemetryView.RefreshScreens();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        foreach (var view in views) view.Shutdown();
        ShutdownCamera();
        ShutdownStartStop();
        state.SaveWatch();
        state.Settings.Save();
        state.Connection.Dispose();
        state.Rpcs3.Dispose();
        state.Voice.Dispose();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        state.Settings.Theme = (ThemeChoice)Math.Max(0, ThemeBox.SelectedIndex);
        ThemeManager.Apply(state.Settings.Theme);
        state.Settings.Save();
    }

    // ---- navigation ----

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (views == null) return;
        var map = new Dictionary<object, IAppView> { [TabTelemetry] = TelemetryView, [TabControls] = ControlsView, [TabCapture] = CaptureView, [TabScanner] = ScannerView, [TabWatch] = WatchView, [TabMemory] = MemoryView };
        foreach (var (tab, view) in map)
        {
            bool active = ReferenceEquals(tab, sender);
            ((UIElement)view).Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            view.SetActive(active);
        }
        ShowHint();
    }

    public void ShowPage(string name)
    {
        if (NavBar.Visibility != Visibility.Visible) return;
        var tab = name.ToLowerInvariant() switch { "controls" => TabControls, "capture" => TabCapture, "scanner" => TabScanner, "watch" => TabWatch, "memory" => TabMemory, _ => TabTelemetry };
        tab.IsChecked = true;
    }

    private IAppView ActiveView => views.First(v => ((UIElement)v).Visibility == Visibility.Visible);

    // ---- status line ----

    /// <summary>Status line: one message at a time; errors in red. Live/Hint messages persist until replaced.</summary>
    private void ShowStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(kind == StatusKind.Error ? "ErrorTextBrush" : "TextBrush");
    }

    private void ShowHint()
    {
        HintText.Text = ActiveView.Hint;
        if (string.IsNullOrEmpty(StatusText.Text))
            ShowStatus(state.IsConnected ? "Ready." : $"Waiting for {state.EmulatorName}…", StatusKind.Hint);
    }

    // ---- game / adapter ----

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || AdapterBox.SelectedItem is not GameAdapter a) return;
        state.Adapter = a;
    }

    private void RefreshAdapter()
    {
        var a = state.Adapter;
        if (!ReferenceEquals(AdapterBox.SelectedItem, a)) AdapterBox.SelectedItem = a;
        AdapterInfo.Text = $"{a.Platform} · {GameAdapter.SourceName(a.Source)}"
            + (a.GameIds.Count > 0 && a.Source != MemorySourceKind.Azimuth ? $" · {string.Join(", ", a.GameIds)}" : "");
        // Only what works, or what's planned; nothing that isn't coming.
        FeatureList.ItemsSource = a.Features.Where(f => f.Support != Support.Unavailable).Select(f => new FeatureRow(
            GameAdapter.FeatureName(f.Feature),
            f.Support switch { Support.Available => "Available", Support.Planned => "Planned", _ => "Unavailable" },
            f.Support == Support.Available,
            string.IsNullOrEmpty(f.Note) ? f.Support.ToString() : f.Note)).ToList();
        // The display card is hidden for games without display patches; the feature list above says what's planned.
        // Azimuth's render size is on the Telemetry tab instead (Render Aspect Ratio).
        ProfileCard.Visibility = a.SupportOf(Feature.DisplayPatches) == Support.Available && a.Source == MemorySourceKind.Pcsx2Pine ? Visibility.Visible : Visibility.Collapsed;
        // The camera bindings are shared by every game, so the card stays; it's greyed out for games without camera control.
        CameraCard.IsEnabled = a.SupportOf(Feature.CameraControl) == Support.Available;
        CameraCard.Opacity = CameraCard.IsEnabled ? 1 : 0.5;
        // The tab bar: Controls (Azimuth's wheel bindings) for the Azimuth games, the address tools in developer mode.
        TabControls.Visibility = state.UsesAzimuth ? Visibility.Visible : Visibility.Collapsed;
        NavBar.Visibility = developer || state.UsesAzimuth ? Visibility.Visible : Visibility.Collapsed;
        if (!state.UsesAzimuth && TabControls.IsChecked == true) TabTelemetry.IsChecked = true;
        if (camera != null) CameraAdapterChanged();
        foreach (var view in views) view.AdapterChanged();
        RefreshDisplay();
    }

    // ---- game profile: display ----

    /// <summary>Reads the display mode back from PCSX2's files. Only GT4 Spec II has display modes so far.</summary>
    private void RefreshDisplay()
    {
        var a = state.Adapter;
        bool supported = a.SupportOf(Feature.DisplayPatches) == Support.Available;
        bool was = initializing;
        initializing = true;
        try
        {
            DisplayBox.IsEnabled = Pcsx2FolderBox.IsEnabled = supported;
            DisplayApply.IsEnabled = false;
            ShowDisplayError(null);
            if (!supported)
            {
                DisplayBox.ItemsSource = null;
                return;
            }
            if (DisplayBox.ItemsSource == null) DisplayBox.ItemsSource = Gt4SpecIIDisplay.Choices;
            pcsx2Data = Pcsx2Folders.Find(state.Settings.Pcsx2Folder);
            Pcsx2FolderBox.Text = pcsx2Data ?? state.Settings.Pcsx2Folder ?? "";
            if (pcsx2Data == null)
            {
                displayState = null;
                ShowDisplayError("Couldn't find PCSX2's settings. Start PCSX2, or choose its folder below.");
                return;
            }
            if (state.Settings.Pcsx2Folder == null)
            {
                // Found through the running PCSX2: remember it for when PCSX2 is closed.
                state.Settings.Pcsx2Folder = pcsx2Data;
                state.Settings.Save();
            }
            displayState = Gt4SpecIIDisplay.Read(pcsx2Data);
            // A mode that's no longer offered (48:9, 64:9) shows as no selection; it keeps working until another is applied.
            DisplayBox.SelectedItem = displayState.Closest is { } c && Gt4SpecIIDisplay.Choices.Contains(c) ? c : null;
            DisplayBox.ToolTip = displayState.Description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowDisplayError($"Couldn't read PCSX2's settings: {ex.Message}");
        }
        finally
        {
            initializing = was;
            UpdateDisplayChoice();
        }
    }

    private void UpdateDisplayChoice()
    {
        if (DisplayBox.SelectedItem is not DisplayMode mode)
        {
            DisplayApply.IsEnabled = false;
            return;
        }
        DisplayApply.IsEnabled = pcsx2Data != null && displayState?.Mode != mode;
    }

    private void ShowDisplayError(string? text)
    {
        DisplayError.Text = text ?? "";
        DisplayError.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DisplayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing) UpdateDisplayChoice();
    }

    private void DisplayApply_Click(object sender, RoutedEventArgs e)
    {
        if (pcsx2Data == null || DisplayBox.SelectedItem is not DisplayMode mode) return;
        try
        {
            var result = Gt4SpecIIDisplay.Apply(pcsx2Data, mode);
            string when = Pcsx2Folders.IsRunning() ? "Restart the game in PCSX2 to see it." : "It takes effect when the game starts.";
            state.Status(result.Changed.Count == 0
                ? $"{mode.Name} was already set."
                : $"Display set to {mode.Name}. {when} Backed up {result.Backups.Count} file{(result.Backups.Count == 1 ? "" : "s")} first (.bak-date next to each).",
                StatusKind.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.Status($"Couldn't write PCSX2's settings: {ex.Message}", StatusKind.Error);
        }
        RefreshDisplay();
    }

    private void Pcsx2FolderBox_LostFocus(object sender, RoutedEventArgs e) => SetPcsx2Folder(Pcsx2FolderBox.Text.Trim());

    private void Pcsx2FolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "PCSX2 folder", InitialDirectory = pcsx2Data ?? "" };
        if (dialog.ShowDialog(this) == true) SetPcsx2Folder(dialog.FolderName);
    }

    private void SetPcsx2Folder(string folder)
    {
        if (folder == (pcsx2Data ?? state.Settings.Pcsx2Folder ?? "")) return;
        state.Settings.Pcsx2Folder = folder.Length == 0 ? null : folder;
        state.Settings.Save();
        RefreshDisplay();
    }

    // ---- connection ----

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Adapter): RefreshAdapter(); RefreshConnection(); break;
            case nameof(AppState.AddressBook): RefreshMap(); break;
            default: RefreshConnection(); break;
        }
    }

    private void RefreshConnection()
    {
        bool usable = state.AdapterUsable;
        bool connected = state.IsConnected;
        ConnSummary.Text = usable ? state.ConnectionSummary : $"Not available for {state.Adapter.Name} yet";
        ConnDetail.Text = usable ? state.ConnectionDetail : "This game's adapter is planned for a later milestone.";
        ConnError.Text = usable ? state.ConnectionError ?? "" : "";
        ConnError.Visibility = string.IsNullOrEmpty(ConnError.Text) ? Visibility.Collapsed : Visibility.Visible;
        ConnDot.Fill = (Brush)FindResource(connected ? "SuccessBrush" : "ControlBorderStrongBrush");
        ConnectButton.Content = state.Connecting ? "Connecting…" : connected ? "Disconnect" : "Connect";
        ConnectButton.IsEnabled = usable && !state.Connecting;
        SlotBox.IsEnabled = FastBox.IsEnabled = usable && !state.UsesRpcs3 && !state.UsesAzimuth;
        foreach (var view in views) view.ConnectionChanged();
        if (pcsx2Data == null && !initializing) RefreshDisplay();   // PCSX2 may have just started, so it can be found now
        if (StatusText.Text is "Ready." or "Waiting for PCSX2…" or "Waiting for RPCS3…" or "Waiting for Azimuth…" or "")
            ShowStatus(connected ? "Ready." : $"Waiting for {state.EmulatorName}…", StatusKind.Hint);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (state.IsConnected)
        {
            state.Disconnect();
            return;
        }
        state.Settings.AutoConnect = true;
        state.Settings.Save();
        await state.ConnectAsync();
    }

    private void SlotBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SlotBox.Text.Trim(), out int slot) && slot is > 0 and < 65536)
        {
            if (slot == state.Settings.PineSlot) return;
            state.Settings.PineSlot = slot;
            state.Settings.Save();
            state.Status($"PINE slot set to {slot}. Reconnect to use it.", StatusKind.Info);
        }
        else
        {
            SlotBox.Text = state.Settings.PineSlot.ToString();
            state.Status("The PINE slot is a port number between 1 and 65535 (PCSX2's default is 28011).", StatusKind.Error);
        }
    }

    private void FastBox_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        state.Settings.FastSnapshots = FastBox.IsChecked == true;
        state.Settings.Save();
        state.Status("Reconnect to apply the snapshot setting.", StatusKind.Info);
    }

    // ---- voice ----

    private void SpeakBox_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        state.Settings.SpeakInstructions = state.Voice.Enabled = SpeakBox.IsChecked == true;
        state.Settings.Save();
    }

    private void VoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || VoiceBox.SelectedItem is not string v) return;
        state.Voice.Voice = state.Settings.Voice = v;
        state.Settings.Save();
    }

    private void RateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RateText == null) return;
        int r = (int)RateSlider.Value;
        RateText.Text = FormatRate(r);
        if (initializing) return;
        state.Voice.Rate = state.Settings.VoiceRate = r;
        state.Settings.Save();
    }

    private static string FormatRate(int r) => r == 0 ? "0" : r > 0 ? $"+{r}" : $"−{-r}";

    private async void VoiceTest_Click(object sender, RoutedEventArgs e)
    {
        bool was = state.Voice.Enabled;
        state.Voice.Enabled = true;
        state.Voice.Play(Cue.Watching);
        await Task.Delay(300);
        await state.Voice.SayAsync("Full throttle. Hold it.");
        state.Voice.Enabled = was;
    }

    // ---- address map ----

    private void RefreshMap()
    {
        var book = state.AddressBook;
        int confirmed = book.Entries.Count(x => x.Status == AddressStatus.Confirmed);
        int candidates = book.Entries.Count(x => x.Status == AddressStatus.Candidate);
        MapSummary.Text = book.Entries.Count == 0
            ? "Nothing mapped yet. Save addresses from capture results or the watch list."
            : $"{confirmed} confirmed · {candidates} candidate{(candidates == 1 ? "" : "s")}. Click one to watch it.";
        MapList.ItemsSource = book.Entries.Where(x => x.Status != AddressStatus.Rejected).OrderBy(x => x.Name).ToList();
    }

    private void MapEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AddressEntry entry) return;
        state.AddWatch(entry.AddressValue, entry.Kind, entry.Name);
        TabWatch.IsChecked = true;
    }

    private void OpenMapFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(state.AddressBookPath)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}

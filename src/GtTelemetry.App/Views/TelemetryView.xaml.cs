using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GtTelemetry.App.Azimuth;
using GtTelemetry.Core.Adapters;
using GtTelemetry.Core.Azimuth;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Nfsu2;
using GtTelemetry.Core.Telemetry;

namespace GtTelemetry.App.Views;

/// <summary>Streams the game's telemetry as Forza Data Out and shows what's being sent. The Gran Turismo games and NFSU2
/// go through <see cref="TelemetryStreamer"/>; Forza Horizon 2 and 3 through Azimuth Telemetry's
/// <see cref="TelemetryEngine"/>, which finds the player's car itself. Same settings and readout for both.</summary>
public partial class TelemetryView : UserControl, IAppView
{
    /// <summary>Follows the selected game and the app's current connection, which is replaced when the PINE slot or
    /// snapshot setting changes.</summary>
    private sealed class LiveSource(AppState state) : ITelemetrySource
    {
        private Pcsx2Connection? current;
        private Gt4SpecIITelemetry? gt4;
        private Ps3GtTelemetry? ps3;
        private Nfsu2Reader? nfsu2;

        public TelemetryFrame Read()
        {
            if (state.UsesAzimuth) return TelemetryFrame.NotDriving;
            if (state.UsesBrowser) return (nfsu2 ??= new Nfsu2Reader(state.Browser)).Read();
            if (state.UsesRpcs3)
            {
                var layouts = Ps3CarLayout.ForGame(state.Adapter.Id);
                if (ps3?.Layouts != layouts) ps3 = new Ps3GtTelemetry(layouts, state.Rpcs3, state.Rpcs3);
                return ps3.Read();
            }
            var c = state.Connection;
            if (!ReferenceEquals(c, current))
            {
                current = c;
                gt4 = new Gt4SpecIITelemetry(c);
            }
            return gt4!.Read();
        }
    }

    private AppState? state;
    private bool loading = true;
    private TelemetryStreamer? streamer;
    private CancellationTokenSource? cts;
    private Task? running;
    private StreamStats? stats;
    private TelemetryEngine? engine;
    private readonly EngineStatus engineStatus = new();
    private readonly ObservableCollection<string> log = [];
    private string? shimMessage;
    private bool shimMessageIsError;
    private readonly DispatcherTimer display = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>Telemetry started or stopped, or the game changed whether it can (the Start button follows it).</summary>
    public event Action? RunningChanged;

    /// <summary>The Azimuth folder was picked or found (the Controls tab reads its bindings from there).</summary>
    public event Action? AzimuthFolderChanged;

    public TelemetryView()
    {
        InitializeComponent();
        LogList.ItemsSource = log;
        display.Tick += (_, _) => Tick();
    }

    public string Hint => state?.UsesAzimuth == true
        ? "Start now or later; it waits for the game. If the readout isn't your car, press Next car."
        : "Pausing the game, menus and loading screens send “not driving”, so force feedback and motion stop.";

    public void Attach(AppState s)
    {
        state = s;
        var set = s.Settings;
        HostBox.Text = set.TelemetryHost;
        PortBox.Text = set.TelemetryPort.ToString();
        FormatBox.SelectedIndex = set.TelemetryFormat == "Dash" ? 1 : 0;
        RateBox.SelectedItem = RateBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == set.TelemetryHz.ToString()) ?? RateBox.Items[1];
        AutoStartBox.IsChecked = set.TelemetryAutoStart;
        loading = false;
        display.Start();
        AdapterChanged();
        if (set.TelemetryAutoStart && Supported) Start();
    }

    public void SetActive(bool active)
    {
        if (active) RefreshScreens();
    }

    public void AdapterChanged()
    {
        if (state == null) return;
        bool az = state.UsesAzimuth;
        // Switching between FH2 and FH3 keeps the engine (it finds the game itself); between Azimuth and an emulator
        // it has to change.
        if (IsRunning && az != (engine != null)) Restart();
        if (!Supported) Stop();
        var show = az ? Visibility.Visible : Visibility.Collapsed;
        CarRow.Visibility = LogBox.Visibility = AspectSection.Visibility = show;
        if (az) RefreshScreens();
        UpdateButton();
        ShowLive();
    }

    public void Shutdown()
    {
        display.Stop();
        Stop();
    }

    public bool Supported => state?.Adapter.SupportOf(Feature.Telemetry) == Support.Available;

    public bool IsRunning => engine != null || running is { IsCompleted: false };

    /// <summary>The start/stop hotkey. Returns whether it's now running.</summary>
    public bool Toggle()
    {
        if (IsRunning) Stop();
        else Start();
        return IsRunning;
    }

    private StreamOptions Options()
    {
        var set = state!.Settings;
        return new StreamOptions(set.TelemetryHost, set.TelemetryPort, set.TelemetryFormat == "Dash" ? ForzaFormat.Dash : ForzaFormat.Horizon, set.TelemetryHz);
    }

    private void Start()
    {
        if (state == null || IsRunning || !Supported) return;
        Setting_Changed(this, new RoutedEventArgs());   // take a half-typed address or port
        var options = Options();
        if (state.UsesAzimuth) StartEngine(options);
        else StartStreamer(options);
        state.Status($"Streaming {options.Format} packets to {options.Host}:{options.Port} at {options.Hz:0} Hz.", StatusKind.Live);
        UpdateButton();
    }

    private void StartStreamer(StreamOptions options)
    {
        var s = state!;
        streamer = new TelemetryStreamer(new LiveSource(s), options);
        streamer.Stats += st => stats = st;
        stats = null;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var st = streamer;
        running = Task.Run(async () =>
        {
            try { await st.RunAsync(token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
            {
                Dispatcher.Invoke(() => s.Status($"Telemetry stopped: couldn't send to {options.Host}:{options.Port}. {ex.Message}", StatusKind.Error));
            }
            finally
            {
                _ = Dispatcher.BeginInvoke(() => { UpdateButton(); ShowLive(); });
            }
        });
    }

    private void StartEngine(StreamOptions options)
    {
        var e = new TelemetryEngine(null, new TelemetryOptions(options.Host, options.Port,
            options.Format == ForzaFormat.Dash ? PacketFormat.Dash : PacketFormat.Horizon, (int)options.Hz));
        e.Log += line => Dispatcher.BeginInvoke(() => AddLog(line));
        engine = e;
        AddLog("Started.");
        e.Start();
    }

    private void Stop()
    {
        if (!IsRunning) return;
        if (engine != null)
        {
            engine.Dispose();
            engine = null;
            AddLog("Stopped.");
        }
        cts?.Cancel();
        try { running?.Wait(500); } catch (AggregateException) { }
        state?.Status("Telemetry stopped.", StatusKind.Info);
        UpdateButton();
        ShowLive();
    }

    private void Restart()
    {
        if (!IsRunning) return;
        Stop();
        Start();
    }

    private void UpdateButton()
    {
        RunningChanged?.Invoke();
        if (!Supported) StateText.Text = $"Telemetry isn't available for {state?.Adapter.Name} yet.";
    }

    private void Tick()
    {
        if (engine != null)
        {
            engine.Snapshot(engineStatus);
            if (engineStatus.State == EngineState.Error)
            {
                // The engine gave up (a bad address and so on): say why.
                string why = engineStatus.Detail;
                Stop();
                state?.Status("Telemetry stopped: " + why, StatusKind.Error);
            }
        }
        if (IsRunning) ShowLive();
    }

    private static TelemetryFrame FromSample(CarSample s) => new()
    {
        Driving = s.Driving,
        Rpm = s.Rpm,
        MaxRpm = s.RedlineRpm,
        IdleRpm = s.IdleRpm,
        SpeedMps = s.SpeedMps,
        Throttle = s.Throttle,
        Brake = s.Brake,
        Clutch = s.Clutch,
        Handbrake = s.Handbrake,
        Steer = s.Steer,
        Gear = s.Gear,
        AccelRight = float.NaN,   // not read from the Forza games
        Wheels = Enumerable.Range(0, 4).Select(i => new WheelState { SuspensionNormalized = s.SuspensionNormalized[i] }).ToArray(),
    };

    private void ShowLive()
    {
        bool az = engine != null;
        TelemetryFrame f;
        if (!IsRunning) f = TelemetryFrame.NotDriving;
        else if (az) f = engineStatus.State is EngineState.Sending or EngineState.Paused ? FromSample(engineStatus.Sample) : TelemetryFrame.NotDriving;
        else f = streamer?.Latest ?? TelemetryFrame.NotDriving;

        if (!IsRunning)
        {
            if (Supported) StateText.Text = "Not streaming";
            StateDot.Fill = (Brush)FindResource("ControlBorderStrongBrush");
            RateText.Text = "";
        }
        else if (az)
        {
            var g = engine!.Game;
            StateText.Text = engineStatus.State switch
            {
                EngineState.Sending => $"Driving: sending live {g?.ShortName} data",
                EngineState.Paused => "Game paused: sending idle packets until you resume",
                EngineState.NoCar => $"Connected to {g?.ShortName}. {engineStatus.Detail}",
                EngineState.WrongGame => engineStatus.Detail,
                EngineState.WaitingForGame => engineStatus.Detail.Length > 0 ? engineStatus.Detail : "Waiting for azimuth.exe: start the game in Azimuth",
                _ => "Starting…",
            };
            StateDot.Fill = (Brush)FindResource(engineStatus.State == EngineState.Sending ? "SuccessBrush"
                : engineStatus.State == EngineState.WrongGame ? "ErrorTextBrush" : "ControlBorderStrongBrush");
            RateText.Text = $"{engineStatus.Sent:N0} sent";
        }
        else
        {
            var st = stats;
            bool connected = state?.IsConnected == true;
            StateText.Text = f.Driving ? "Driving: sending live data"
                : !connected ? $"Waiting for {state?.EmulatorName}: sending “not driving”"
                : state?.Game?.Status == EmulatorStatus.Shutdown ? $"No game running in the connected {state.EmulatorName}: sending “not driving”"
                : "Not driving: paused, menu or loading";
            StateDot.Fill = (Brush)FindResource(f.Driving ? "SuccessBrush" : "ControlBorderStrongBrush");
            RateText.Text = st == null ? "" : $"{st.ActualHz:0} packets/s · {st.Sent:N0} sent";
        }

        bool show = f.Driving;
        GearText.Text = !show ? "–" : f.Gear switch { < 0 => "R", 0 => "N", _ => f.Gear.ToString() };
        RpmText.Text = show ? f.Rpm.ToString("N0") : "–";
        RedlineText.Text = show && f.MaxRpm > 0 ? $"/ {f.MaxRpm:N0}" : "";
        SpeedText.Text = show ? $"{f.SpeedMps * 2.23694f:F0} mph" : "–";
        SpeedAltText.Text = show ? $"{f.SpeedMps * 3.6f:F0} km/h" : "";
        ThrottleScale.ScaleX = show ? Math.Clamp(f.Throttle, 0, 1) : 0;
        BrakeScale.ScaleX = show ? Math.Clamp(f.Brake, 0, 1) : 0;
        ClutchScale.ScaleX = show ? Math.Clamp(f.Clutch, 0, 1) : 0;
        HandbrakeScale.ScaleX = show ? Math.Clamp(f.Handbrake, 0, 1) : 0;
        float steer = show ? Math.Clamp(f.Steer, -1, 1) : 0;
        SteerLeftScale.ScaleX = steer < 0 ? -steer : 0;
        SteerRightScale.ScaleX = steer > 0 ? steer : 0;
        LatGText.Text = show && !float.IsNaN(f.AccelRight) ? $"{f.AccelRight / 9.81:0.00} g" : "–";
        var w = f.Wheels;
        // Surface rumble isn't found in the Forza games yet, so don't claim "Track".
        SurfaceText.Text = !show || w == null || az ? "–"
            : w.Any(x => x.OnRumbleStrip) ? "Kerb"
            : w.Any(x => x.SurfaceRumble > 0) ? "Off track"
            : "Track";
        SuspensionText.Text = show && w is { Length: 4 } ? string.Join(" ", w.Select(x => $"{Math.Clamp(x.SuspensionNormalized, 0, 1) * 100:0}")) : "–";

        NextCarButton.IsEnabled = RescanButton.IsEnabled = az && engineStatus.Cars > 0;
        CarInfoText.Text = !az ? "Not running."
            : engineStatus.Car == 0 ? $"{engineStatus.Cars} car(s) found · {engineStatus.Sent:N0} packets sent"
            : $"{engineStatus.Cars} car(s) found · reading {engineStatus.Car:X}{(engineStatus.Pinned ? " (picked by Next car)" : "")} · {engineStatus.Sent:N0} packets sent";
    }

    private void NextCar_Click(object sender, RoutedEventArgs e) => engine?.NextCar();

    private void Rescan_Click(object sender, RoutedEventArgs e) => engine?.Rescan();

    private void AddLog(string line)
    {
        log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (log.Count > 200) log.RemoveAt(0);
        LogScroll.ScrollToEnd();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || state == null) return;
        var set = state.Settings;
        string host = HostBox.Text.Trim();
        if (host.Length == 0) HostBox.Text = host = set.TelemetryHost;
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is <= 0 or > 65535)
        {
            PortBox.Text = set.TelemetryPort.ToString();
            state.Status("The port is a number between 1 and 65535, and must match the port set in SimHub.", StatusKind.Error);
            return;
        }
        string format = (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Horizon";
        int hz = int.Parse((RateBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "60");
        if (host == set.TelemetryHost && port == set.TelemetryPort && format == set.TelemetryFormat && hz == set.TelemetryHz) return;
        set.TelemetryHost = host;
        set.TelemetryPort = port;
        set.TelemetryFormat = format;
        set.TelemetryHz = hz;
        set.Save();
        if (sender != this) Restart();
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || state == null) return;
        state.Settings.TelemetryAutoStart = AutoStartBox.IsChecked == true;
        state.Settings.Save();
    }

    // ---- Azimuth: its folder, Render Aspect Ratio ----

    private string? AzimuthDir => WideScreen.Folder(state?.Settings.AzimuthFolder);

    /// <summary>Render Aspect Ratio: Set puts the shim (dinput8.dll) next to azimuth.exe with the game resolution in
    /// azimuth-span.ini; Clear takes both away. Called when the window is activated too.</summary>
    public void RefreshScreens()
    {
        if (state is not { UsesAzimuth: true }) return;
        var set = state.Settings;
        if (WideScreen.FolderNextToApp == null && !WideScreen.IsAzimuthFolder(set.AzimuthFolder)
            && WideScreen.FindRunningAzimuth() is string found)
        {
            set.AzimuthFolder = found;
            set.Save();
            AzimuthFolderChanged?.Invoke();
        }
        string? folder = AzimuthDir;

        AzimuthFolderRow.Visibility = WideScreen.FolderNextToApp == null ? Visibility.Visible : Visibility.Collapsed;
        AzimuthFolderText.Text = folder == null ? "Azimuth's folder isn't known: put this app next to azimuth.exe, or" : $"Azimuth: {folder}";

        var shim = WideScreen.State(folder);
        bool isSet = shim is ShimState.UpToDate or ShimState.Outdated;
        var (w, h) = isSet && folder != null ? WideScreen.ReadSize(folder) : (set.AspectWidth, set.AspectHeight);
        if (!AspectWidthBox.IsKeyboardFocused && !AspectHeightBox.IsKeyboardFocused)
        {
            AspectWidthBox.Text = w?.ToString() ?? "";
            AspectHeightBox.Text = h?.ToString() ?? "";
        }

        string text = shim == ShimState.Foreign ? "Azimuth's folder has a dinput8.dll from something else, so this is left alone."
            : isSet && w is int sw && h is int sh
                ? $"Set: {sw} × {sh} ({new ScreenRow(0, 0, sw, sh, Array.Empty<int>()).Aspect}). Pick it under Aspect in Azimuth's launcher, with Fullscreen."
            : "";
        bool error = shim == ShimState.Foreign;
        if (shimMessage != null) { text = shimMessage; error = shimMessageIsError; }
        ShimStatus.Text = text;
        ShimStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShimStatus.Foreground = (Brush)FindResource(error ? "ErrorTextBrush" : "TextBrush");

        SetAspectButton.IsEnabled = folder != null && shim != ShimState.Foreign;
        ClearAspectButton.IsEnabled = isSet;
    }

    private void SetAspect_Click(object sender, RoutedEventArgs e)
    {
        if (AzimuthDir is not string folder || state == null) return;
        bool ok = int.TryParse(AspectWidthBox.Text.Trim(), out int w) & int.TryParse(AspectHeightBox.Text.Trim(), out int h);
        if (!ok || w < 640 || h < 360 || w > 16384 || h > 8640)
        {
            ShowShimMessage("Enter the width and height in pixels, e.g. 10240 and 1440.", true);
            return;
        }
        state.Settings.AspectWidth = w;
        state.Settings.AspectHeight = h;
        state.Settings.Save();

        // A new DLL needs Azimuth closed; a new size alone doesn't.
        string? error = WideScreen.State(folder) == ShimState.UpToDate ? null : WideScreen.Install(folder);
        if (error == null)
        {
            try { WideScreen.WriteSize(folder, w, h); }
            catch (Exception ex) { error = "Couldn't save the resolution: " + ex.Message; }
        }
        if (error == null)
        {
            AddLog($"Render Aspect Ratio: set {w} × {h} in {folder}");
            bool runningNow = WideScreen.AzimuthRunning(folder);
            ShowShimMessage(runningNow ? $"Set: {w} × {h}. Restart Azimuth to use it." : null, false);
        }
        else ShowShimMessage(error, true);
    }

    private void ClearAspect_Click(object sender, RoutedEventArgs e)
    {
        if (AzimuthDir is not string folder) return;
        string? error = WideScreen.Remove(folder);
        if (error == null)
        {
            try { WideScreen.WriteSize(folder, null, null); } catch { }
            AddLog("Render Aspect Ratio: cleared in " + folder);
        }
        ShowShimMessage(error ?? "Cleared. Set Azimuth's Aspect back to one it offers.", error != null);
    }

    private void ShowShimMessage(string? text, bool error)
    {
        shimMessage = text;
        shimMessageIsError = error;
        RefreshScreens();
        shimMessage = null;
    }

    private void BrowseAzimuth_Click(object sender, RoutedEventArgs e)
    {
        if (state == null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with azimuth.exe" };
        if (AzimuthDir is string current) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!WideScreen.IsAzimuthFolder(dlg.FolderName))
        {
            ShowShimMessage("There's no azimuth.exe in that folder.", true);
            return;
        }
        state.Settings.AzimuthFolder = dlg.FolderName;
        state.Settings.Save();
        AzimuthFolderChanged?.Invoke();
        RefreshScreens();
    }
}

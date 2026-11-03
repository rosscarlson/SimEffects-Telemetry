using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GtTelemetry.Core.Adapters;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Telemetry;

namespace GtTelemetry.App.Views;

/// <summary>Streams the game's telemetry as Forza Data Out and shows what's being sent.</summary>
public partial class TelemetryView : UserControl, IAppView
{
    /// <summary>Follows the app's current connection, which is replaced when the PINE slot or snapshot setting changes.</summary>
    private sealed class LiveSource(Func<Pcsx2Connection> connection) : ITelemetrySource
    {
        private Pcsx2Connection? current;
        private Gt4SpecIITelemetry? reader;

        public TelemetryFrame Read()
        {
            var c = connection();
            if (!ReferenceEquals(c, current))
            {
                current = c;
                reader = new Gt4SpecIITelemetry(c);
            }
            return reader!.Read();
        }
    }

    private AppState? state;
    private bool loading = true;
    private TelemetryStreamer? streamer;
    private CancellationTokenSource? cts;
    private Task? running;
    private StreamStats? stats;
    private readonly DispatcherTimer display = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public TelemetryView()
    {
        InitializeComponent();
        display.Tick += (_, _) => ShowLive();
    }

    public string Hint => "Pausing the game, menus and loading screens send “not driving”, so force feedback and motion stop.";

    public void Attach(AppState s)
    {
        state = s;
        var set = s.Settings;
        HostBox.Text = set.TelemetryHost;
        PortBox.Text = set.TelemetryPort.ToString();
        FormatBox.SelectedIndex = set.TelemetryFormat == "Dash" ? 1 : 0;
        RateBox.SelectedIndex = set.TelemetryHz <= 30 ? 0 : 1;
        AutoStartBox.IsChecked = set.TelemetryAutoStart;
        loading = false;
        UpdateButton();
        if (set.TelemetryAutoStart && Supported) Start();
    }

    public void SetActive(bool active) { }

    public void AdapterChanged()
    {
        if (!Supported) Stop();
        UpdateButton();
    }

    public void Shutdown() => Stop();

    private bool Supported => state?.Adapter.SupportOf(Feature.Telemetry) == Support.Available;

    private bool IsRunning => running is { IsCompleted: false };

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning) Stop();
        else Start();
    }

    private void Start()
    {
        if (state == null || IsRunning) return;
        var set = state.Settings;
        var options = new StreamOptions(set.TelemetryHost, set.TelemetryPort,
                                        set.TelemetryFormat == "Dash" ? ForzaFormat.Dash : ForzaFormat.Horizon, set.TelemetryHz);
        var s = state;
        streamer = new TelemetryStreamer(new LiveSource(() => s.Connection), options);
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
        display.Start();
        state.Status($"Streaming {options.Format} packets to {options.Host}:{options.Port} at {options.Hz:0} Hz.", StatusKind.Live);
        UpdateButton();
    }

    private void Stop()
    {
        if (!IsRunning) return;
        cts?.Cancel();
        try { running?.Wait(500); } catch (AggregateException) { }
        display.Stop();
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
        bool on = IsRunning;
        StartButton.IsEnabled = Supported;
        StartButton.Style = (Style)FindResource(on ? "DangerButton" : "AccentButton");
        StartIcon.Text = on ? "" : "";
        StartLabel.Text = on ? "Stop" : "Start streaming";
        if (!Supported) StateText.Text = $"Telemetry isn't available for {state?.Adapter.Name} yet.";
    }

    private void ShowLive()
    {
        if (!IsRunning || streamer == null)
        {
            if (Supported) StateText.Text = "Not streaming";
            StateDot.Fill = (Brush)FindResource("ControlBorderStrongBrush");
            RateText.Text = "";
            foreach (var t in new[] { SpeedText, RpmText, GearText, ThrottleText, BrakeText, LatGText, SurfaceText }) t.Text = "—";
            return;
        }
        var f = streamer.Latest;
        var st = stats;
        bool connected = state?.IsConnected == true;
        StateText.Text = f.Driving ? "Driving: sending live data"
            : !connected ? "Waiting for PCSX2: sending “not driving”"
            : state?.Game?.Status == EmulatorStatus.Shutdown ? "No game running in the connected PCSX2: sending “not driving”"
            : "Not driving: paused, menu or loading";
        StateDot.Fill = (Brush)FindResource(f.Driving ? "SuccessBrush" : "ControlBorderStrongBrush");
        RateText.Text = st == null ? "" : $"{st.ActualHz:0} packets/s · {st.Sent:N0} sent";
        if (!f.Driving)
        {
            foreach (var t in new[] { SpeedText, RpmText, GearText, ThrottleText, BrakeText, LatGText, SurfaceText }) t.Text = "—";
            return;
        }
        SpeedText.Text = $"{f.SpeedMps * 3.6:0} km/h";
        RpmText.Text = $"{f.Rpm:0}";
        GearText.Text = f.Gear switch { < 0 => "R", 0 => "N", _ => f.Gear.ToString() };
        ThrottleText.Text = $"{f.Throttle * 100:0}%";
        BrakeText.Text = $"{f.Brake * 100:0}%";
        LatGText.Text = $"{f.AccelRight / 9.81:0.00} g";
        var w = f.Wheels;
        SurfaceText.Text = w == null ? "—"
            : w.Any(x => x.OnRumbleStrip) ? "Kerb"
            : w.Any(x => x.SurfaceRumble > 0) ? "Off track"
            : "Track";
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
        Restart();
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || state == null) return;
        state.Settings.TelemetryAutoStart = AutoStartBox.IsChecked == true;
        state.Settings.Save();
    }
}

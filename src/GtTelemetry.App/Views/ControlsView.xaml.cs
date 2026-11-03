using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GtTelemetry.App.Azimuth;
using GtTelemetry.App.Input;

namespace GtTelemetry.App.Views;

/// <summary>The Controls tab, for the Azimuth games: Azimuth's racing wheel bindings (data\azimuth.ini), one set for
/// every game it runs. From Azimuth Telemetry.</summary>
public partial class ControlsView : UserControl, IAppView
{
    private AppState? state;
    private readonly List<BindingRow> bindings = AzimuthControls.NewRows();
    private readonly DispatcherTimer captureTick = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly DispatcherTimer diagramTick = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private WheelInput? wheels;
    private BindingCapture? bindCapture;
    private BindingRow? bindRow;
    private DateTime bindDeadline;
    private bool loading, controlsLoaded, controlsDirty, active;
    private string? controlsMessage;
    private bool controlsMessageIsError;

    public ControlsView()
    {
        InitializeComponent();
        BindingList.ItemsSource = bindings;
        captureTick.Tick += (_, _) => CaptureStep();
        diagramTick.Tick += (_, _) => RefreshDiagram();
    }

    public string Hint => "Bind… listens for the control. Save writes the bindings to Azimuth while it's closed.";

    private string? AzimuthDir => WideScreen.Folder(state?.Settings.AzimuthFolder);

    public void Attach(AppState s) => state = s;

    public void SetActive(bool on)
    {
        active = on;
        if (on) ShowControls();
        else
        {
            if (bindCapture != null) EndBindCapture(null, false);
            diagramTick.Stop();
        }
    }

    /// <summary>The Azimuth folder was picked or found: read its bindings again.</summary>
    public void FolderChanged()
    {
        controlsLoaded = false;
        if (active) ShowControls();
    }

    public void Shutdown()
    {
        captureTick.Stop();
        diagramTick.Stop();
        wheels?.Dispose();
    }

    /// <summary>Esc cancels a Bind… in progress. Returns true if the key was used.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (bindCapture == null || e.Key != Key.Escape) return false;
        e.Handled = true;
        EndBindCapture("Cancelled.", false);
        return true;
    }

    /// <summary>A controller button is being bound here, so it shouldn't do anything else.</summary>
    public bool Capturing => bindCapture != null;

    private void ShowControls()
    {
        string? folder = AzimuthDir;
        bool usable = WideScreen.IsAzimuthFolder(folder);

        ControlsIntro.Text = !usable ? "Put this app in Azimuth's folder, or pick the folder under Render Aspect Ratio on the Telemetry tab."
            : $"Azimuth's racing wheel bindings in {folder}, one set for every game it runs. Bind… listens for the control; Save writes them with Azimuth closed.";
        var show = usable ? Visibility.Visible : Visibility.Collapsed;
        DiagramBox.Visibility = ControlsSettingsRow.Visibility = BindingBox.Visibility = ControlsActions.Visibility = CaptureText.Visibility = show;
        if (!usable) { diagramTick.Stop(); return; }

        if (!controlsLoaded) LoadControls();
        else if (wheels == null) EnsureWheels();
        RefreshControlsStatus();
        diagramTick.Start();
        RefreshDiagram();
    }

    // The picture at the top: each binding (as edited, saved or not) against
    // the controllers right now.
    private void RefreshDiagram()
    {
        if (wheels == null || !IsVisible) return;
        List<WheelSnapshot> shots;
        try { shots = wheels.Read(); }
        catch { return; }
        var values = new Dictionary<string, double>();
        foreach (var row in bindings)
        {
            if (!row.IsSet) continue;
            double rest = row.Kind == CaptureKind.Steering ? 0.5 : 0;
            values[row.Key] = AzimuthControls.Evaluate(row.Value, shots, rest) ?? rest;
        }
        int rotation = int.TryParse(RotationBox.Text.Trim(), out int r) ? Math.Clamp(r, 180, 2520) : 900;
        Diagram.Show(values, rotation);
    }

    private void EnsureWheels()
    {
        if (wheels == null)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;
            wheels = new WheelInput(new WindowInteropHelper(window).Handle);
        }
        try { wheels.Refresh(); }
        catch (Exception ex) { ShowControlsMessage("Couldn't read the controllers: " + ex.Message, true); }
        var w = wheels;
        foreach (var row in bindings) row.SetDescriber(v => AzimuthControls.Describe(v, w.Devices));
    }

    private void LoadControls()
    {
        if (AzimuthDir is not string folder) return;
        EnsureWheels();
        AzimuthControls.Loaded? loaded;
        try { loaded = AzimuthControls.Load(folder); }
        catch (Exception ex) { ShowControlsMessage("Couldn't read azimuth.ini: " + ex.Message, true); return; }
        if (loaded == null) { ShowControlsMessage("There's no data\\azimuth.ini yet; start Azimuth once first.", true); return; }
        foreach (var row in bindings) row.Value = loaded.Bindings.GetValueOrDefault(row.Key, "");
        loading = true;
        RotationBox.Text = loaded.Rotation ?? "900";
        ForceBox.Text = loaded.Force ?? "100";
        loading = false;
        controlsLoaded = true;
        controlsDirty = false;
        controlsMessage = null;
    }

    private void RefreshControlsStatus()
    {
        bool running = AzimuthDir is string f && WideScreen.AzimuthRunning(f);
        string text;
        bool error = false;
        if (controlsMessage != null) { text = controlsMessage; error = controlsMessageIsError; }
        else if (controlsDirty && running) { text = "Unsaved changes. Close Azimuth to save them (it rewrites its settings when it closes)."; error = true; }
        else if (controlsDirty) text = "Unsaved changes.";
        else text = $"{wheels?.Devices.Count ?? 0} controllers connected.";
        ControlsStatus.Text = text;
        ControlsStatus.Foreground = (Brush)FindResource(error ? "ErrorTextBrush" : "TextBrush");
        SaveControlsButton.IsEnabled = controlsDirty && bindCapture == null;
        if (bindCapture == null)
            CaptureText.Text = "Steering: turn right. Pedals: press fully, then let go. Buttons: press one (a hat or D-pad direction works too). Esc cancels. Don't re-detect on Azimuth's Controls page afterwards, or it may replace these.";
    }

    private void ShowControlsMessage(string text, bool error)
    {
        controlsMessage = text;
        controlsMessageIsError = error;
        RefreshControlsStatus();
    }

    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BindingRow row) return;
        if (bindCapture != null) EndBindCapture(null, false);
        EnsureWheels();
        if (wheels == null || wheels.Devices.Count == 0)
        {
            ShowControlsMessage("No controllers found. Plug the wheel in and try again.", true);
            controlsMessage = null;
            return;
        }
        controlsMessage = null;
        bindRow = row;
        bindCapture = new BindingCapture(row.Kind, wheels.Read());
        bindDeadline = DateTime.UtcNow.AddSeconds(15);
        row.IsCapturing = true;
        RefreshControlsStatus();
        CaptureText.Text = $"{row.Label}: {bindCapture.Prompt}";
        captureTick.Start();
    }

    private void CaptureStep()
    {
        if (bindCapture == null || bindRow == null || wheels == null) { captureTick.Stop(); return; }
        string? result = bindCapture.Feed(wheels.Read());
        CaptureText.Text = $"{bindRow.Label}: {bindCapture.Prompt}";
        if (result != null)
        {
            var row = bindRow;
            row.Value = result;
            controlsDirty = true;
            EndBindCapture(null, false);
            ShowControlsMessage($"{row.Label}: {row.Display}. Save when you're done.", false);
            controlsMessage = null;
        }
        else if (DateTime.UtcNow > bindDeadline) EndBindCapture("Nothing was moved or pressed; Bind… to try again.", true);
    }

    private void EndBindCapture(string? message, bool error)
    {
        captureTick.Stop();
        if (bindRow != null) bindRow.IsCapturing = false;
        bindRow = null;
        bindCapture = null;
        if (message != null) ShowControlsMessage(message, error);
        else RefreshControlsStatus();
        controlsMessage = null;  // shown once; the next refresh shows the usual status
    }

    private void ClearBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BindingRow row) return;
        row.Value = "";
        controlsDirty = true;
        RefreshControlsStatus();
    }

    private void ControlsNumber_LostFocus(object sender, RoutedEventArgs e)
    {
        if (loading || sender is not TextBox box) return;
        bool rotation = box == RotationBox;
        if (!int.TryParse(box.Text.Trim(), out int v)) v = rotation ? 900 : 100;
        v = rotation ? Math.Clamp(v, 180, 2520) : Math.Clamp(v, 0, 200);
        string was = box.Text;
        box.Text = v.ToString();
        var loaded = AzimuthDir is string f ? AzimuthControls.Load(f) : null;
        if (loaded == null || (rotation ? loaded.Rotation : loaded.Force) != box.Text || was != box.Text) controlsDirty = true;
        RefreshControlsStatus();
    }

    private void SaveControls_Click(object sender, RoutedEventArgs e)
    {
        if (AzimuthDir is not string folder) return;
        if (WideScreen.AzimuthRunning(folder))
        {
            ShowControlsMessage("Close Azimuth first; it rewrites its settings when it closes, which would undo these.", true);
            controlsMessage = null;
            return;
        }
        var keys = bindings.ToDictionary(r => "wheel_" + r.Key, r => r.Value);
        keys["wheel_rotation"] = RotationBox.Text.Trim();
        keys["wheel_force"] = ForceBox.Text.Trim();
        try
        {
            AzimuthControls.Save(folder, keys);
            controlsDirty = false;
            ShowControlsMessage("Saved to Azimuth. Start the game to use them.", false);
            state?.Status("Controls: saved Azimuth's wheel bindings in " + folder, StatusKind.Info);
        }
        catch (Exception ex)
        {
            ShowControlsMessage("Couldn't save azimuth.ini: " + ex.Message, true);
        }
        controlsMessage = null;
    }

    private void ReloadControls_Click(object sender, RoutedEventArgs e)
    {
        if (bindCapture != null) EndBindCapture(null, false);
        controlsLoaded = false;
        ShowControls();
    }
}

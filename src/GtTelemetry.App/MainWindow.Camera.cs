using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GtTelemetry.App.Camera;
using GtTelemetry.Core.Azimuth;
using CameraAction = GtTelemetry.App.Camera.CameraAction;

namespace GtTelemetry.App;

/// <summary>One row of the camera card's binding list.</summary>
public sealed class CameraKeyRow(CameraAction action, string name) : Observable
{
    private string keyText = "", padText = "", padTip = "";

    public CameraAction Action { get; } = action;

    public string Name { get; } = name;

    public string KeyText { get => keyText; set => Set(ref keyText, value); }

    public string PadText { get => padText; set => Set(ref padText, value); }

    public string PadTip { get => padTip; set => Set(ref padTip, value); }
}

// The camera card: FOV and seat on keys or controller buttons, the overlay over the game, and rebinding. One set of
// bindings for every game: GT4 goes through CameraController, the Azimuth games through Azimuth Telemetry's
// CameraAdjuster.
public partial class MainWindow
{
    private CameraController? camera;
    private CameraAdjuster? azimuthCamera;
    private readonly CameraStatus azimuthCameraStatus = new();
    private readonly DispatcherTimer azimuthCameraTick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool azimuthFlash;
    private KeyboardHook? keyboard;
    private Joysticks? joysticks;
    private CameraOverlay? overlay;
    private List<CameraKeyRow> keyRows = [];
    private CameraKeyRow? capturingKey, capturingPad;
    private Dictionary<KeyCombo, CameraAction> keyBindings = [];
    private Dictionary<PadButton, CameraAction> padBindings = [];

    private void SetUpCamera()
    {
        CameraEnabledBox.IsChecked = state.Settings.CameraEnabled;
        keyRows = CameraKeys.All.Select(k => new CameraKeyRow(k.Action, k.Name)).ToList();
        CameraKeyList.ItemsSource = keyRows;

        overlay = new CameraOverlay();
        camera = new CameraController(state, Dispatcher);
        camera.Changed += (snap, flash) =>
        {
            if (state.UsesAzimuth) return;
            CameraHeading.Text = snap.Heading;
            CameraHeading.Visibility = snap.Adjustable ? Visibility.Visible : Visibility.Collapsed;
            CameraDetail.Text = snap.Detail;
            if (flash && keyboard?.GameForeground() is { } game && game != IntPtr.Zero) overlay.Flash(snap.Heading, snap.Detail, game);
            else if (overlay.IsVisible) overlay.Flash(snap.Heading, snap.Detail, IntPtr.Zero, restartTimer: false);
        };
        keyboard = new KeyboardHook((combo, shift) =>
        {
            if (!state.Settings.CameraEnabled || capturingKey != null || capturingPad != null || !keyBindings.TryGetValue(combo, out var action)) return false;
            DoCamera(action, shift);
            return true;
        });
        if (!keyboard.Installed) state.Status("Couldn't watch the keyboard, so the camera hotkeys won't work.", StatusKind.Error);
        joysticks = new Joysticks(Dispatcher);
        joysticks.Pressed += Joystick_Pressed;
        azimuthCameraTick.Tick += (_, _) => RefreshAzimuthCamera();
        RefreshKeys();
        CameraAdapterChanged();
    }

    /// <summary>Runs the Azimuth camera only while an Azimuth game is selected (it writes to the game's camera once
    /// something is changed).</summary>
    private void CameraAdapterChanged()
    {
        if (state.UsesAzimuth && azimuthCamera == null)
        {
            var settings = state.Settings;
            // Keys are "<game>/<view>"; Azimuth Telemetry 0.4.0 saved the cockpit view under the game id alone.
            azimuthCamera = new CameraAdjuster(key =>
                settings.AzimuthCamera.TryGetValue(key, out var a) ? a
                : key.EndsWith("/cockpit") && settings.AzimuthCamera.TryGetValue(key[..^"/cockpit".Length], out var old) ? old
                : new CameraAdjust());
            azimuthCamera.Changed += (key, a) => Dispatcher.BeginInvoke(() =>
            {
                if (a.IsZero) settings.AzimuthCamera.Remove(key);
                else settings.AzimuthCamera[key] = a;
                settings.Save();
            });
            azimuthCamera.Start();
            azimuthCameraTick.Start();
        }
        else if (!state.UsesAzimuth && azimuthCamera != null)
        {
            azimuthCameraTick.Stop();
            azimuthCamera.Dispose();
            azimuthCamera = null;
        }
    }

    /// <summary>A camera key or button: GT4's controller takes it with a bigger step for Shift; the Azimuth games step
    /// 1 degree and 1 cm, five at a time with Shift.</summary>
    private void DoCamera(CameraAction action, bool big)
    {
        if (azimuthCamera is not { } az)
        {
            camera?.Enqueue(action, big);
            return;
        }
        var a = action switch
        {
            CameraAction.FovWider => Core.Azimuth.CameraAction.FovUp,
            CameraAction.FovNarrower => Core.Azimuth.CameraAction.FovDown,
            CameraAction.SeatForward => Core.Azimuth.CameraAction.SeatForward,
            CameraAction.SeatBack => Core.Azimuth.CameraAction.SeatBack,
            CameraAction.SeatUp => Core.Azimuth.CameraAction.SeatUp,
            CameraAction.SeatDown => Core.Azimuth.CameraAction.SeatDown,
            CameraAction.SeatLeft => Core.Azimuth.CameraAction.SeatLeft,
            CameraAction.SeatRight => Core.Azimuth.CameraAction.SeatRight,
            _ => Core.Azimuth.CameraAction.Reset,
        };
        int times = big && a != Core.Azimuth.CameraAction.Reset ? 5 : 1;
        for (int i = 0; i < times; i++) az.Do(a);
        azimuthFlash = true;
        RefreshAzimuthCamera();
    }

    /// <summary>The card (and the overlay after a key) for the Azimuth games, worded as Azimuth Telemetry had it.</summary>
    private void RefreshAzimuthCamera()
    {
        if (azimuthCamera == null) return;
        azimuthCamera.Snapshot(azimuthCameraStatus);
        var s = azimuthCameraStatus;
        var a = s.Adjust;
        string heading, detail;
        bool adjustable = state.Settings.CameraEnabled && s.Game != null && s.View != null && s.GameFovDeg != null;
        if (!state.Settings.CameraEnabled)
        {
            heading = "Camera";
            detail = "Off.";
        }
        else if (!adjustable)
        {
            heading = "Camera";
            detail = (s.Text.Length > 0 ? s.Text : "Waiting for the game.") + " Changes apply to the view you're driving in, each view its own.";
        }
        else
        {
            var view = s.View!;
            float fov = s.GameFovDeg!.Value;
            heading = $"{view.Name}: FOV {fov + a.FovDeg:0}°" + (a.FovDeg == 0 ? " (the game's own)" : $" (game {fov:0}°, {a.FovDeg:+0;−0}°)");
            detail = (view.Seat == null ? "Seat keys work in the cockpit, dashboard, hood and bumper views"
                : a.X == 0 && a.Y == 0 && a.Z == 0 ? "Seat: the game's own position" : "Seat: " + DescribeSeat(a)) + $" · {s.Game!.ShortName}";
        }
        CameraHeading.Text = heading;
        CameraHeading.Visibility = adjustable ? Visibility.Visible : Visibility.Collapsed;
        CameraDetail.Text = detail;
        if (azimuthFlash && keyboard?.GameForeground() is { } game && game != IntPtr.Zero) overlay?.Flash(heading, detail, game);
        else if (overlay?.IsVisible == true) overlay.Flash(heading, detail, IntPtr.Zero, restartTimer: false);
        azimuthFlash = false;
    }

    private static string DescribeSeat(CameraAdjust a)
    {
        var parts = new List<string>();
        if (a.Z != 0) parts.Add($"{Math.Abs(a.Z) * 100:0} cm {(a.Z > 0 ? "forward" : "back")}");
        if (a.Y != 0) parts.Add($"{Math.Abs(a.Y) * 100:0} cm {(a.Y > 0 ? "up" : "down")}");
        if (a.X != 0) parts.Add($"{Math.Abs(a.X) * 100:0} cm {(a.X > 0 ? "right" : "left")}");
        return string.Join(", ", parts);
    }

    private void ShutdownCamera()
    {
        azimuthCameraTick.Stop();
        azimuthCamera?.Dispose();
        keyboard?.Dispose();
        joysticks?.Dispose();
        camera?.Dispose();
        overlay?.Close();
    }

    private void RefreshKeys()
    {
        keyBindings = [];
        padBindings = [];
        foreach (var row in keyRows)
        {
            var combo = CameraKeys.For(state.Settings, row.Action);
            if (combo is { } c) keyBindings[c] = row.Action;
            row.KeyText = row == capturingKey ? "Press keys…" : combo?.ToString() ?? "Not set";

            var button = CameraKeys.ButtonFor(state.Settings, row.Action);
            if (button is { } b) padBindings[b] = row.Action;
            row.PadText = row == capturingPad ? "Press button…" : button is { } p ? $"Button {p.Button}" : "Add button";
            row.PadTip = button is { } q && joysticks != null
                ? $"{joysticks.NameOf(q.Device)}, button {q.Button}. Click, then press another button to change it. Delete clears it, Esc cancels."
                : "Click, then press a button on your wheel, button box or controller. Esc cancels.";
        }
    }

    private void Joystick_Pressed(PadButton button, bool repeat)
    {
        if (ControlsView.Capturing) return;   // binding Azimuth's controls: buttons mean that
        if (CaptureStartStopButton(button, repeat)) return;
        if (capturingPad is { } row)
        {
            if (repeat) return;
            capturingPad = null;
            // A button does one thing: take it off any other action first.
            foreach (var other in keyRows.Where(r => r != row && CameraKeys.ButtonFor(state.Settings, r.Action) == button))
                state.Settings.CameraButtons.Remove(other.Action.ToString());
            state.Settings.CameraButtons[row.Action.ToString()] = button.ToString();
            state.Settings.Save();
            RefreshKeys();
            state.Status($"{row.Name}: {joysticks!.NameOf(button.Device)}, button {button.Button}.", StatusKind.Info);
            return;
        }
        if (StartStopButtonPressed(button, repeat)) return;
        if (!state.Settings.CameraEnabled || capturingKey != null || !padBindings.TryGetValue(button, out var action)) return;
        if (repeat && action == CameraAction.Reset) return;
        if (keyboard?.GameForeground() is not { } game || game == IntPtr.Zero) return;
        DoCamera(action, KeyboardHook.ShiftDown());
    }

    private void CameraEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        state.Settings.CameraEnabled = CameraEnabledBox.IsChecked == true;
        state.Settings.Save();
    }

    private void CameraKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CameraKeyRow row) return;
        capturingPad = null;
        capturingKey = row;
        RefreshKeys();
    }

    private void CameraPad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CameraKeyRow row) return;
        capturingKey = null;
        capturingPad = row;
        RefreshKeys();
    }

    private void CameraDefaultKeys_Click(object sender, RoutedEventArgs e)
    {
        capturingKey = capturingPad = null;
        state.Settings.CameraKeys.Clear();
        state.Settings.Save();
        RefreshKeys();
    }

    /// <summary>While a row is waiting for keys, the next Ctrl/Alt combination becomes its binding. Esc cancels and
    /// Delete clears, for a waiting button too.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CaptureStartStopKey(e) || ControlsView.HandleKey(e)) return;
        if (capturingKey == null && capturingPad == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            capturingKey = capturingPad = null;
            RefreshKeys();
            return;
        }
        if (key is Key.Delete or Key.Back)
        {
            if (capturingKey is { } k) state.Settings.CameraKeys[k.Action.ToString()] = "";
            if (capturingPad is { } p) state.Settings.CameraButtons.Remove(p.Action.ToString());
            capturingKey = capturingPad = null;
            state.Settings.Save();
            RefreshKeys();
            return;
        }
        if (capturingKey is not { } row) return;   // waiting for a controller button
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        var mods = Keyboard.Modifiers & ~ModifierKeys.Shift;
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            state.Status("Use Ctrl or Alt with the key, so it doesn't clash with the game's own controls. Shift is kept for bigger steps.", StatusKind.Error);
            return;
        }
        var combo = new KeyCombo(mods, key);
        capturingKey = null;
        foreach (var other in keyRows.Where(r => r != row && CameraKeys.For(state.Settings, r.Action) == combo))
            state.Settings.CameraKeys[other.Action.ToString()] = "";
        state.Settings.CameraKeys[row.Action.ToString()] = combo.ToString();
        state.Settings.Save();
        RefreshKeys();
        state.Status($"{row.Name}: {combo}.", StatusKind.Info);
    }
}

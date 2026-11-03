using System.Windows;
using System.Windows.Input;
using GtTelemetry.App.Camera;

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

// The camera card: FOV and seat on keys or controller buttons, the overlay over the game, and rebinding.
public partial class MainWindow
{
    private CameraController? camera;
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
            CameraHeading.Text = snap.Heading;
            CameraHeading.Visibility = snap.Adjustable ? Visibility.Visible : Visibility.Collapsed;
            CameraDetail.Text = snap.Detail;
            if (flash && keyboard?.Pcsx2Foreground() is { } game && game != IntPtr.Zero) overlay.Flash(snap.Heading, snap.Detail, game);
            else if (overlay.IsVisible) overlay.Flash(snap.Heading, snap.Detail, IntPtr.Zero, restartTimer: false);
        };
        keyboard = new KeyboardHook((combo, shift) =>
        {
            if (!state.Settings.CameraEnabled || capturingKey != null || capturingPad != null || !keyBindings.TryGetValue(combo, out var action)) return false;
            camera.Enqueue(action, shift);
            return true;
        });
        if (!keyboard.Installed) state.Status("Couldn't watch the keyboard, so the camera hotkeys won't work.", StatusKind.Error);
        joysticks = new Joysticks(Dispatcher);
        joysticks.Pressed += Joystick_Pressed;
        RefreshKeys();
    }

    private void ShutdownCamera()
    {
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
        if (!state.Settings.CameraEnabled || capturingKey != null || !padBindings.TryGetValue(button, out var action)) return;
        if (repeat && action == CameraAction.Reset) return;
        if (keyboard?.Pcsx2Foreground() is not { } game || game == IntPtr.Zero) return;
        camera?.Enqueue(action, KeyboardHook.ShiftDown());
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

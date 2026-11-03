using System.Windows;
using System.Windows.Input;
using GtTelemetry.App.Camera;

namespace GtTelemetry.App;

/// <summary>One row of the camera card's key list.</summary>
public sealed class CameraKeyRow(CameraAction action, string name) : Observable
{
    private string keyText = "";

    public CameraAction Action { get; } = action;

    public string Name { get; } = name;

    public string KeyText { get => keyText; set => Set(ref keyText, value); }
}

// The camera card: FOV and seat hotkeys, the overlay over the game, and rebinding keys.
public partial class MainWindow
{
    private CameraController? camera;
    private KeyboardHook? keyboard;
    private CameraOverlay? overlay;
    private List<CameraKeyRow> keyRows = [];
    private CameraKeyRow? capturing;
    private Dictionary<KeyCombo, CameraAction> bindings = [];

    private void SetUpCamera()
    {
        CameraEnabledBox.IsChecked = state.Settings.CameraEnabled;
        keyRows = CameraKeys.All.Select(k => new CameraKeyRow(k.Action, k.Name)).ToList();
        CameraKeyList.ItemsSource = keyRows;
        RefreshKeys();

        overlay = new CameraOverlay();
        camera = new CameraController(state, Dispatcher);
        camera.Changed += (snap, flash) =>
        {
            CameraHeading.Text = snap.Heading;
            CameraDetail.Text = snap.Detail;
            if (flash && keyboard?.Pcsx2Foreground() is { } game && game != IntPtr.Zero) overlay.Flash(snap.Heading, snap.Detail, game);
            else if (overlay.IsVisible) overlay.Flash(snap.Heading, snap.Detail, IntPtr.Zero, restartTimer: false);
        };
        keyboard = new KeyboardHook((combo, shift) =>
        {
            if (!state.Settings.CameraEnabled || capturing != null || !bindings.TryGetValue(combo, out var action)) return false;
            camera.Enqueue(action, shift);
            return true;
        });
        if (!keyboard.Installed) state.Status("Couldn't watch the keyboard, so the camera hotkeys won't work.", StatusKind.Error);
    }

    private void ShutdownCamera()
    {
        keyboard?.Dispose();
        camera?.Dispose();
        overlay?.Close();
    }

    private void RefreshKeys()
    {
        bindings = [];
        foreach (var row in keyRows)
        {
            var combo = CameraKeys.For(state.Settings, row.Action);
            if (combo is { } c) bindings[c] = row.Action;
            row.KeyText = row == capturing ? "Press keys…" : combo?.ToString() ?? "Not set";
        }
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
        capturing = row;
        RefreshKeys();
    }

    private void CameraDefaultKeys_Click(object sender, RoutedEventArgs e)
    {
        capturing = null;
        state.Settings.CameraKeys.Clear();
        state.Settings.Save();
        RefreshKeys();
    }

    /// <summary>While a key row is waiting, the next Ctrl/Alt combination becomes its binding.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (capturing == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            capturing = null;
            RefreshKeys();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        var mods = Keyboard.Modifiers & ~ModifierKeys.Shift;
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            state.Status("Use Ctrl or Alt with the key, so it doesn't clash with the game's own controls. Shift is kept for bigger steps.", StatusKind.Error);
            return;
        }
        var combo = new KeyCombo(mods, key);
        var row = capturing;
        capturing = null;
        // A combination does one thing: take it off any other action first.
        foreach (var other in keyRows.Where(r => r != row && CameraKeys.For(state.Settings, r.Action) == combo))
            state.Settings.CameraKeys[other.Action.ToString()] = "";
        state.Settings.CameraKeys[row.Action.ToString()] = combo.ToString();
        state.Settings.Save();
        RefreshKeys();
        state.Status($"{row.Name}: {combo}.", StatusKind.Info);
    }
}

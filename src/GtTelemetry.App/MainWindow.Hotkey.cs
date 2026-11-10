using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GtTelemetry.App.Camera;
using GtTelemetry.App.Input;
using GtTelemetry.Core.Azimuth;

namespace GtTelemetry.App;

// The start/stop hotkey card (from Azimuth Telemetry): a key, system-wide, or a button on a wheel, button box or
// controller starts and stops telemetry for any game, and can say so.
public partial class MainWindow
{
    private GlobalHotkey? startStopHotkey;
    private bool capturingStartStop;
    private readonly Voice toggleVoice = new();
    private DateTime lastStartStopButton;

    /// <summary>Called once the window has a handle (RegisterHotKey needs one).</summary>
    private void SetUpStartStop()
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source == null) return;
        startStopHotkey = new GlobalHotkey(source);
        startStopHotkey.Pressed += () => ToggleTelemetry();
        ApplyStartStop();
    }

    private void ShutdownStartStop() => startStopHotkey?.Dispose();

    private void ToggleTelemetry()
    {
        bool on = TelemetryView.Toggle();
        toggleVoice.Say(on ? "Telemetry on" : "Telemetry off", beep: false);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => TelemetryView.Toggle();

    /// <summary>The Start button in the Game card follows the Telemetry tab, however it was started or stopped.</summary>
    private void UpdateStartButton()
    {
        bool on = TelemetryView.IsRunning;
        StartButton.IsEnabled = TelemetryView.Supported;
        StartButton.Style = (Style)FindResource(on ? "DangerButton" : "AccentButton");
        StartIcon.Text = on ? "" : "";
        StartLabel.Text = on ? "Stop" : "Start";
    }

    private void ApplyStartStop()
    {
        var s = state.Settings;
        startStopHotkey?.Unregister();
        string? warning = null;
        if (capturingStartStop) HotkeyText.Text = "Press a key or a wheel/controller button…";
        else if (PadButton.Parse(s.StartStopButton) is { } b)
        {
            HotkeyText.Text = $"{joysticks?.NameOf(b.Device) ?? "Controller " + b.Device} button {b.Button}";
        }
        else
        {
            var h = s.StartStopKey;
            HotkeyText.Text = h.ToString();
            if (h.Kind == HotkeyKind.Keyboard && startStopHotkey != null && !startStopHotkey.Register(h.VirtualKey, h.Modifiers))
                warning = $"Another app is already using {h}. Pick a different key.";
        }
        HotkeyWarning.Text = warning ?? "";
        HotkeyWarning.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        SetHotkeyButton.Content = capturingStartStop ? "Cancel" : "Set…";
    }

    private void SetHotkey_Click(object sender, RoutedEventArgs e)
    {
        capturingStartStop = !capturingStartStop;
        ApplyStartStop();
        Focus();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        capturingStartStop = false;
        state.Settings.StartStopKey = new HotkeyBinding();
        state.Settings.StartStopButton = null;
        state.Settings.Save();
        ApplyStartStop();
    }

    /// <summary>While waiting for the start/stop key, the next key (with any modifiers) becomes it. Esc cancels.</summary>
    private bool CaptureStartStopKey(KeyEventArgs e)
    {
        if (!capturingStartStop) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return true;
        capturingStartStop = false;
        if (key != Key.Escape)
        {
            state.Settings.StartStopKey = new HotkeyBinding
            {
                Kind = HotkeyKind.Keyboard,
                VirtualKey = KeyInterop.VirtualKeyFromKey(key),
                Modifiers = Keyboard.Modifiers,
            };
            state.Settings.StartStopButton = null;
            state.Settings.Save();
        }
        ApplyStartStop();
        return true;
    }

    /// <summary>While waiting for the start/stop key, a controller button becomes it.</summary>
    private bool CaptureStartStopButton(PadButton button, bool repeat)
    {
        if (!capturingStartStop) return false;
        if (repeat) return true;
        capturingStartStop = false;
        state.Settings.StartStopButton = button.ToString();
        state.Settings.StartStopKey = new HotkeyBinding();
        state.Settings.Save();
        ApplyStartStop();
        return true;
    }

    /// <summary>The start/stop button, from anywhere. Some devices show up twice (e.g. a Fanatec base): one press, one
    /// toggle.</summary>
    private bool StartStopButtonPressed(PadButton button, bool repeat)
    {
        if (PadButton.Parse(state.Settings.StartStopButton) != button) return false;
        if (repeat || DateTime.UtcNow - lastStartStopButton < TimeSpan.FromMilliseconds(400)) return true;
        lastStartStopButton = DateTime.UtcNow;
        ToggleTelemetry();
        return true;
    }
}

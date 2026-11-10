using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GtTelemetry.App.Input;

namespace GtTelemetry.App;

// The Force feedback card: NFSU2's own wheel effects, played on the wheel (Input/Nfsu2ForceFeedback.cs).
public partial class MainWindow
{
    private sealed record FfbRow(string Mark, string Name, string Detail, double Opacity);

    private Nfsu2ForceFeedback? ffb;
    private DispatcherTimer? ffbTimer;
    private bool ffbLoading;
    private string ffbShown = "";

    private void SetUpFfb()
    {
        ffbLoading = true;
        var s = state.Settings;
        FfbEnabledBox.IsChecked = s.FfbEnabled;
        FfbStrengthSlider.Value = s.FfbStrength;
        FfbConstantSlider.Value = s.FfbConstant;
        FfbVibrationSlider.Value = s.FfbVibration;
        FfbSpringSlider.Value = s.FfbSpring;
        FfbDamperSlider.Value = s.FfbDamper;
        LoadFfbWheels();
        ffbLoading = false;

        ffb = new Nfsu2ForceFeedback(state, new WindowInteropHelper(this).Handle);
        ffbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        ffbTimer.Tick += (_, _) => ShowFfb();
        ffbTimer.Start();
    }

    private void ShutdownFfb()
    {
        ffbTimer?.Stop();
        ffb?.Dispose();
        ffb = null;
    }

    private void LoadFfbWheels()
    {
        var wheels = FfbPlayer.Wheels();
        bool was = ffbLoading;
        ffbLoading = true;
        FfbWheelBox.ItemsSource = wheels;
        FfbWheelBox.SelectedItem = wheels.FirstOrDefault(w => w.Instance.ToString() == state.Settings.FfbWheel) ?? wheels.FirstOrDefault();
        ffbLoading = was;
    }

    private void ShowFfb()
    {
        if (ffb == null || FfbCard.Visibility != Visibility.Visible) return;
        FfbStatus.Text = ffb.Status;
        FfbStatus.Foreground = ffb.HasError ? (System.Windows.Media.Brush)FindResource("ErrorTextBrush") : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        var list = ffb.Effects;
        string key = string.Join("|", list.Select(e => $"{e.Slot}{e.Name}{e.Detail}{e.Playing}"));
        if (key == ffbShown) return;
        ffbShown = key;
        FfbList.ItemsSource = list.OrderByDescending(e => e.Playing).ThenBy(e => e.Slot)
            .Select(e => new FfbRow(e.Playing ? "" : "", e.Name, e.Detail, e.Playing ? 1.0 : 0.5)).ToList();
    }

    private void FfbEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (ffbLoading) return;
        state.Settings.FfbEnabled = FfbEnabledBox.IsChecked == true;
        state.Settings.Save();
    }

    private void FfbWheelBox_DropDownOpened(object? sender, EventArgs e) => LoadFfbWheels();

    private void FfbWheelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ffbLoading || FfbWheelBox.SelectedItem is not FfbWheel w) return;
        state.Settings.FfbWheel = w.Instance.ToString();
        state.Settings.Save();
    }

    private void FfbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || FindName(slider.Name.Replace("Slider", "Text")) is not TextBlock text) return;
        int v = (int)slider.Value;
        text.Text = $"{v}%";
        if (ffbLoading) return;
        var s = state.Settings;
        switch (slider.Name)
        {
            case nameof(FfbStrengthSlider): s.FfbStrength = v; break;
            case nameof(FfbConstantSlider): s.FfbConstant = v; break;
            case nameof(FfbVibrationSlider): s.FfbVibration = v; break;
            case nameof(FfbSpringSlider): s.FfbSpring = v; break;
            case nameof(FfbDamperSlider): s.FfbDamper = v; break;
        }
        s.Save();
    }
}

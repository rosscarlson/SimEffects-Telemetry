using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GtTelemetry.App.Camera;

/// <summary>
/// A small read-out drawn over the game window while the camera is being adjusted. Click-through, never takes focus,
/// and fades out a couple of seconds after the last change.
/// </summary>
public sealed class CameraOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    private readonly TextBlock title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock detail = new() { FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
    private readonly DispatcherTimer hide = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public CameraOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        title.Foreground = detail.Foreground = Brushes.White;
        detail.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new StackPanel { Children = { title, detail } },
        };
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            fade.Completed += (_, _) => { if (Opacity == 0) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        };
    }

    /// <summary>Shows the read-out near the top-left of <paramref name="gameWindow"/> (or where it was, if zero).
    /// Without <paramref name="restartTimer"/> it only updates the text of a read-out that's already showing.</summary>
    public void Flash(string heading, string text, IntPtr gameWindow, bool restartTimer = true)
    {
        title.Text = heading;
        detail.Text = text;
        if (!restartTimer) return;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        if (gameWindow != IntPtr.Zero && GetWindowRect(gameWindow, out var r))
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowPos(h, HWND_TOPMOST, r.Left + 32, r.Top + 80, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        hide.Stop();
        hide.Start();
    }
}

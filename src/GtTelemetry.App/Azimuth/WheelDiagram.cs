using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GtTelemetry.App.Azimuth;

// A picture of Azimuth's racing wheel controls, live: the wheel turns with the
// steering (over the set rotation), its paddles, A B X Y, View, Menu and
// D-pad light up, the pedals and handbrake fill, and the H-shifter shows the
// gear. What isn't bound is faint. Keys are Azimuth's binding names
// (AzimuthControls.Actions). Adapted from DaisysApp's ForzaWheel diagram.
public sealed class WheelDiagram : Viewbox
{
    const double W = 630, H = 250, WheelX = 150, WheelY = 125;

    readonly Canvas _canvas = new() { Width = W, Height = H };
    readonly Canvas _wheel = new() { Width = W, Height = H };
    readonly RotateTransform _turn = new(0, WheelX, WheelY);
    readonly TextBlock _steerText = Label("", 0, 0);
    // Lights (lit while pressed), and bars (filled as far as the axis goes).
    readonly List<(string Key, Shape Shape, FrameworkElement Fade)> _lights = new();
    readonly List<(string Key, Rectangle Fill, double Top, double Height, FrameworkElement Fade)> _bars = new();

    public WheelDiagram()
    {
        Stretch = Stretch.Uniform;
        MaxHeight = 230;
        Child = _canvas;

        // the paddles, behind the wheel
        Paddle("left_paddle", "−", WheelX - 142);
        Paddle("right_paddle", "+", WheelX + 120);

        // the wheel, which turns
        _wheel.RenderTransform = _turn;
        _canvas.Children.Add(_wheel);
        var rim = new Ellipse { Width = 220, Height = 220, StrokeThickness = 18 };
        rim.SetResourceReference(Shape.StrokeProperty, "ControlBorderStrongBrush");
        Place(_wheel, rim, WheelX - 110, WheelY - 110);
        foreach (var (x, y, w, h) in new[] { (WheelX - 100, WheelY - 8, 70.0, 16.0), (WheelX + 30, WheelY - 8, 70.0, 16.0), (WheelX - 8, WheelY + 30, 16.0, 70.0) })
        {
            var spoke = new Rectangle { Width = w, Height = h, RadiusX = 4, RadiusY = 4 };
            spoke.SetResourceReference(Shape.FillProperty, "ControlBorderBrush");
            Place(_wheel, spoke, x, y);
        }
        var hub = new Ellipse { Width = 84, Height = 84, StrokeThickness = 1 };
        hub.SetResourceReference(Shape.FillProperty, "ControlBrush");
        hub.SetResourceReference(Shape.StrokeProperty, "ControlBorderStrongBrush");
        Place(_wheel, hub, WheelX - 42, WheelY - 42);
        var top = new Rectangle { Width = 8, Height = 18, Fill = Brushes.OrangeRed };
        Place(_wheel, top, WheelX - 4, WheelY - 119);

        // on the wheel: the D-pad left, A B X Y right, View and Menu on the hub
        Light(_wheel, "dpad_up", Square(14), WheelX - 64, WheelY - 24, "");
        Light(_wheel, "dpad_down", Square(14), WheelX - 64, WheelY + 10, "");
        Light(_wheel, "dpad_left", Square(14), WheelX - 81, WheelY - 7, "");
        Light(_wheel, "dpad_right", Square(14), WheelX - 47, WheelY - 7, "");
        Light(_wheel, "y", Dot(16), WheelX + 49, WheelY - 27, "Y");
        Light(_wheel, "x", Dot(16), WheelX + 31, WheelY - 8, "X");
        Light(_wheel, "b", Dot(16), WheelX + 67, WheelY - 8, "B");
        Light(_wheel, "a", Dot(16), WheelX + 49, WheelY + 11, "A");
        Light(_wheel, "view", Pill(20, 10), WheelX - 26, WheelY - 22, "");
        Light(_wheel, "menu", Pill(20, 10), WheelX + 6, WheelY - 22, "");
        Place(_canvas, _steerText, WheelX - 80, H - 16);
        _steerText.Width = 160;
        _steerText.TextAlignment = TextAlignment.Center;

        // the pedals and the handbrake
        Bar("clutch", "Clutch", 326);
        Bar("brake", "Brake", 366);
        Bar("throttle", "Throttle", 406);
        Bar("handbrake", "Handbrake", 452);

        // the H-shifter: 1 3 5 7 across the top, 2 4 6 R across the bottom
        const double sx = 516, sTop = 52, sBottom = 168, step = 28;
        for (int c = 0; c < 4; c++)
        {
            var gate = new Line { X1 = sx + c * step, X2 = sx + c * step, Y1 = sTop, Y2 = sBottom, StrokeThickness = 3 };
            gate.SetResourceReference(Shape.StrokeProperty, "ControlBorderBrush");
            _canvas.Children.Add(gate);
        }
        var across = new Line { X1 = sx, X2 = sx + 3 * step, Y1 = (sTop + sBottom) / 2, Y2 = (sTop + sBottom) / 2, StrokeThickness = 3 };
        across.SetResourceReference(Shape.StrokeProperty, "ControlBorderBrush");
        _canvas.Children.Add(across);
        string[] topRow = { "gear_1", "gear_3", "gear_5", "gear_7" }, bottomRow = { "gear_2", "gear_4", "gear_6", "reverse" };
        for (int c = 0; c < 4; c++)
        {
            Light(_canvas, topRow[c], Dot(20), sx + c * step - 10, sTop - 10, topRow[c][^1].ToString());
            Light(_canvas, bottomRow[c], Dot(20), sx + c * step - 10, sBottom - 10, c == 3 ? "R" : bottomRow[c][^1].ToString());
        }
        Place(_canvas, Label("Shifter", 0, 0), sx - 6, 16);
    }

    // values: axes 0-1 (steering 0.5 = centre), buttons 0 or 1; only bound
    // keys have one. rotation: the wheel's lock-to-lock degrees.
    public void Show(IReadOnlyDictionary<string, double> values, int rotation)
    {
        foreach (var (key, shape, fade) in _lights)
        {
            bool on = values.TryGetValue(key, out var v) && v > 0.5;
            shape.SetResourceReference(Shape.FillProperty, on ? "AccentBrush" : "ControlBrush");
            fade.Opacity = values.ContainsKey(key) ? 1 : 0.35;
        }
        foreach (var (key, fill, top, height, fade) in _bars)
        {
            double v = values.TryGetValue(key, out var x) ? Math.Clamp(x, 0, 1) : 0;
            fill.Height = v * height;
            Canvas.SetTop(fill, top + height - fill.Height);
            fade.Opacity = values.ContainsKey(key) ? 1 : 0.35;
        }
        bool steering = values.TryGetValue("steering", out var s);
        double angle = ((steering ? s : 0.5) - 0.5) * rotation;
        _turn.Angle = angle;
        _wheel.Opacity = steering ? 1 : 0.5;
        _steerText.Text = steering ? $"Steering {angle:+0;−0;0}°" : "Steering not bound";
    }

    // ---- drawing ----

    void Paddle(string key, string sign, double x)
    {
        var paddle = new Rectangle { Width = 22, Height = 60, RadiusX = 6, RadiusY = 6 };
        Light(_canvas, key, paddle, x, WheelY - 50, sign);
    }

    void Bar(string key, string name, double x)
    {
        const double top = 52, height = 116;
        var frame = new Canvas();
        var outline = new Rectangle { Width = 30, Height = height, RadiusX = 4, RadiusY = 4, StrokeThickness = 1 };
        outline.SetResourceReference(Shape.StrokeProperty, "ControlBorderStrongBrush");
        outline.SetResourceReference(Shape.FillProperty, "ControlBrush");
        Place(frame, outline, x, top);
        var fill = new Rectangle { Width = 30, Height = 0, RadiusX = 4, RadiusY = 4 };
        fill.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Place(frame, fill, x, top + height);
        var label = Label(name, 0, 0, 10);
        label.Width = 60;
        label.TextAlignment = TextAlignment.Center;
        Place(frame, label, x - 15, top + height + 6);
        _canvas.Children.Add(frame);
        _bars.Add((key, fill, top, height, frame));
    }

    void Light(Canvas on, string key, Shape shape, double x, double y, string text)
    {
        shape.StrokeThickness = 1;
        shape.SetResourceReference(Shape.StrokeProperty, "ControlBorderStrongBrush");
        shape.SetResourceReference(Shape.FillProperty, "ControlBrush");
        var holder = new Canvas();
        Place(holder, shape, x, y);
        if (text.Length > 0)
        {
            var t = Label(text, 0, 0, 10);
            t.Width = shape.Width;
            t.TextAlignment = TextAlignment.Center;
            t.FontWeight = FontWeights.SemiBold;
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Place(holder, t, x, y + (shape.Height - 13) / 2);
        }
        on.Children.Add(holder);
        _lights.Add((key, shape, holder));
    }

    static Shape Dot(double size) => new Ellipse { Width = size, Height = size };
    static Shape Square(double size) => new Rectangle { Width = size, Height = size, RadiusX = 3, RadiusY = 3 };
    static Shape Pill(double w, double h) => new Rectangle { Width = w, Height = h, RadiusX = h / 2, RadiusY = h / 2 };

    static TextBlock Label(string text, double x, double y, double size = 11)
    {
        var t = new TextBlock { Text = text, FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        return t;
    }

    static void Place(Panel on, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        on.Children.Add(e);
    }
}

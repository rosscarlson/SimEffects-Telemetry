using System.IO;
using System.Text;
using GtTelemetry.App.Azimuth;
using GtTelemetry.App.Input;
using Xunit;

public class AzimuthIniTests
{
    static string NewFolder(string ini)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ht-ini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(AzimuthControls.IniPath(folder), ini, Encoding.Unicode);
        return folder;
    }

    const string Sample =
        "[launcher]\r\ngame=1a96545b\r\nwheel_steering=00040eb7:axis:0:0:65535\r\nwheel_throttle=\r\nwheel_rotation=900\r\nplayer_name=Exigeous\r\n" +
        "[title-1a96545b]\r\nfolder=G:\\ROMs\\Forza Horizon 2\r\naspect=display\r\n";

    [Fact]
    public void LoadReadsLauncherBindings()
    {
        var loaded = AzimuthControls.Load(NewFolder(Sample))!;
        Assert.Equal("00040eb7:axis:0:0:65535", loaded.Bindings["steering"]);
        Assert.Equal("", loaded.Bindings["throttle"]);
        Assert.Equal("", loaded.Bindings["gear_3"]);  // missing lines read as unset
        Assert.Equal("900", loaded.Rotation);
        Assert.Null(loaded.Force);
    }

    [Fact]
    public void SaveChangesOnlyThoseKeysAndKeepsUtf16()
    {
        string folder = NewFolder(Sample);
        AzimuthControls.Save(folder, new Dictionary<string, string>
        {
            ["wheel_throttle"] = "57572497:axis:2:0:65535",
            ["wheel_force"] = "80",
        });
        string path = AzimuthControls.IniPath(folder);
        byte[] raw = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, raw[..2]);  // UTF-16 LE, as Azimuth writes it
        string text = File.ReadAllText(path, Encoding.Unicode);
        Assert.Contains("wheel_throttle=57572497:axis:2:0:65535\r\n", text);
        Assert.Contains("player_name=Exigeous\r\nwheel_force=80\r\n[title-1a96545b]", text);  // new key stays in [launcher]
        Assert.Contains("aspect=display", text);
        Assert.Equal(Sample, File.ReadAllText(path + ".before-azimuth-telemetry", Encoding.Unicode));
        Assert.Equal("80", AzimuthControls.Load(folder)!.Force);
    }

    [Fact]
    public void DescribeNamesDevicesAndUsesAzimuthsButtonNumbers()
    {
        var pedals = new WheelDevice { Id = "57572497", Name = "Simjack-Pedals", Instance = Guid.NewGuid(), AxisNames = new Dictionary<int, string> { [2] = "Z Axis" }, ButtonCount = 0 };
        Assert.Equal("Simjack-Pedals · Z Axis · 0 → 65535", AzimuthControls.Describe("57572497:axis:2:0:65535", new[] { pedals }));
        Assert.Equal("Device 00040eb7 (not connected) · Button 2", AzimuthControls.Describe("00040eb7:button:1", new[] { pedals }));
        Assert.Equal("Device 00040eb7 (not connected) · Hat 1 up", AzimuthControls.Describe("00040eb7:hat:0:up", Array.Empty<WheelDevice>()));
    }
}

public class BindingCaptureTests
{
    static readonly WheelDevice Wheel = new() { Id = "00040eb7", Name = "FANATEC Wheel", Instance = Guid.NewGuid(), AxisNames = new Dictionary<int, string> { [0] = "Wheel Axis", [6] = "Combined Pedals" }, ButtonCount = 8 };
    static readonly WheelDevice Pedals = new() { Id = "57572497", Name = "Simjack-Pedals", Instance = Guid.NewGuid(), AxisNames = new Dictionary<int, string> { [2] = "Z", [3] = "Rx" }, ButtonCount = 0 };

    static List<WheelSnapshot> Shot(int steer = 32768, int z = 0, int rx = 65535, int button = -1, int pov = -1)
    {
        var w = new int[WheelInput.AxisSlots]; w[0] = steer; w[6] = 32768;
        var p = new int[WheelInput.AxisSlots]; p[2] = z; p[3] = rx;
        var buttons = new bool[128];
        if (button >= 0) buttons[button] = true;
        return new() { new(Wheel, w, buttons, new[] { pov, -1, -1, -1 }), new(Pedals, p, new bool[128], new[] { -1, -1, -1, -1 }) };
    }

    [Fact]
    public void SteeringTakesTheAxisTurnedRight()
    {
        var c = new BindingCapture(CaptureKind.Steering, Shot());
        Assert.Null(c.Feed(Shot(steer: 40000)));  // a small turn isn't enough
        Assert.Equal("00040eb7:axis:0:0:65535", c.Feed(Shot(steer: 60000)));
        var reversed = new BindingCapture(CaptureKind.Steering, Shot());
        Assert.Equal("00040eb7:axis:0:65535:0", reversed.Feed(Shot(steer: 5000)));
    }

    [Fact]
    public void PedalWaitsForReleaseAndRecordsRestThenPressed()
    {
        var c = new BindingCapture(CaptureKind.Pedal, Shot());
        Assert.Null(c.Feed(Shot(z: 30000)));
        Assert.Null(c.Feed(Shot(z: 65000)));
        Assert.Equal("Now let it go…", c.Prompt);
        Assert.Equal("57572497:axis:2:0:65535", c.Feed(Shot(z: 500)));

        // A pedal that rests at the top (reads backwards) keeps that order.
        var inverted = new BindingCapture(CaptureKind.Pedal, Shot());
        Assert.Null(inverted.Feed(Shot(rx: 20000)));
        Assert.Null(inverted.Feed(Shot(rx: 0)));
        Assert.Equal("57572497:axis:3:65535:0", inverted.Feed(Shot(rx: 65535)));
    }

    [Fact]
    public void ButtonsAndHatsUseAzimuthsForm()
    {
        var held = new BindingCapture(CaptureKind.Button, Shot(button: 3));
        Assert.Null(held.Feed(Shot(button: 3)));  // already held when Bind was pressed
        Assert.Equal("00040eb7:button:5", new BindingCapture(CaptureKind.Button, Shot()).Feed(Shot(button: 5)));
        Assert.Equal("00040eb7:hat:0:right", new BindingCapture(CaptureKind.Button, Shot()).Feed(Shot(pov: 9000)));
        Assert.Equal("00040eb7:hat:0:up", new BindingCapture(CaptureKind.Button, Shot()).Feed(Shot(pov: 0)));
    }

    [Fact]
    public void EvaluatesBindingsAgainstLiveInput()
    {
        // Brake bound inverted (rest 65535, pressed 0), half pressed.
        Assert.Equal(0.5, AzimuthControls.Evaluate($"{Pedals.Id}:axis:3:65535:0", Shot(rx: 32767), 0)!.Value, 3);
        Assert.Equal(1.0, AzimuthControls.Evaluate($"{Wheel.Id}:axis:0:0:65535", Shot(steer: 65535), 0.5)!.Value, 3);
        Assert.Equal(1, AzimuthControls.Evaluate($"{Wheel.Id}:button:5", Shot(button: 5)));
        Assert.Equal(0, AzimuthControls.Evaluate($"{Wheel.Id}:button:4", Shot(button: 5)));
        Assert.Equal(1, AzimuthControls.Evaluate($"{Wheel.Id}:hat:0:up", Shot(pov: 4500)));   // diagonal: up and right
        Assert.Equal(1, AzimuthControls.Evaluate($"{Wheel.Id}:hat:0:right", Shot(pov: 4500)));
        Assert.Equal(0, AzimuthControls.Evaluate($"{Wheel.Id}:hat:0:down", Shot(pov: 4500)));
        Assert.Null(AzimuthControls.Evaluate("12345678:button:0", Shot()));  // device not connected
    }
}

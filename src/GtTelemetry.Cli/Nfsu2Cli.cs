using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GtTelemetry.Core.Browser;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Nfsu2;
using GtTelemetry.Core.Scanning;
using GtTelemetry.Core.Telemetry;

namespace GtTelemetry.Cli;

/// <summary>
/// gtt nfsu2: NFSU2 on nfsu2.xyz through the browser bridge (extension/nfsu2). Each command hosts the bridge itself
/// and waits for the extension to connect, so close the app first. "serve" keeps the bridge up and offers a plain
/// probe port for discovery scripts.
/// </summary>
internal static class Nfsu2Cli
{
    public const int ProbePort = 28021;

    public static int Run(List<string> a)
    {
        double wait = double.Parse(Program.TakeOption(a, "--wait") ?? "15", CultureInfo.InvariantCulture);
        if (a.Count == 0) return Help();
        string sub = a[0].ToLowerInvariant();
        var rest = a.Skip(1).ToList();
        using var bridge = new BrowserBridge();
        try { bridge.Start(); }
        catch (BrowserBridgeException ex) { throw new MemorySourceException(ex.Message, ex); }
        if (sub == "serve") return Serve(bridge);

        if (!WaitFor(bridge, wait)) return 2;
        var mem = new BrowserMemorySource(bridge);
        return sub switch
        {
            "info" => Info(bridge),
            "read" => Read(mem, rest),
            "watch" => Watch(mem),
            "effects" => Effects(mem),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine($"""
            gtt nfsu2 — Need for Speed: Underground 2 on nfsu2.xyz, through the Chrome extension

              nfsu2 info                    The page, its memory and controllers (and whether each has the FFB spoof)
              nfsu2 read <addr> [type] [n]  Read values from the game's memory
              nfsu2 watch                   Print the telemetry the app would send, 5 times a second (Ctrl+C stops)
              nfsu2 effects                 Print the game's force feedback effects with the steering, 5 times a second
              nfsu2 serve                   Keep the bridge up; log the page's state and rumble; probe port {ProbePort}
                                            (lines: "R <addr> <len>" -> "OK <len>" + bytes, "S" -> state JSON)

            Options: --wait S (seconds to wait for the extension, default 15)
            Close SimEffects Telemetry first: the bridge listens on 127.0.0.1:{BrowserBridge.DefaultPort}.
            """);
        return 1;
    }

    private static bool WaitFor(BrowserBridge bridge, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (!bridge.HasMemory && sw.Elapsed.TotalSeconds < seconds) Thread.Sleep(100);
        if (bridge.HasMemory) return true;
        Console.Error.WriteLine(bridge.IsPageConnected
            ? "The page is connected but the game hasn't started (no memory yet). Press Play on nfsu2.xyz."
            : "No page connected. Open nfsu2.xyz in Chrome with the extension loaded (chrome://extensions, Load unpacked: extension\\nfsu2).");
        return false;
    }

    private static int Info(BrowserBridge bridge)
    {
        var s = bridge.State;
        Console.WriteLine($"Extension:  {s.ExtensionVersion} (protocol {s.Protocol})");
        Console.WriteLine($"Page:       {s.Page} ({(s.Visible ? "visible" : "hidden")})");
        Console.WriteLine($"Memory:     {s.MemoryBytes / 1048576.0:0} MB");
        Console.WriteLine($"Rumble:     {s.RumbleCalls} playEffect calls so far");
        foreach (var p in s.Pads)
            Console.WriteLine($"Pad {p.Index}:      {p.Id} [{p.Mapping}] {p.Axes} axes {p.Buttons} buttons{(p.Spoofed ? ", FFB spoofed" : "")}");
        return 0;
    }

    private static int Read(BrowserMemorySource mem, List<string> a)
    {
        if (a.Count < 1) return Help();
        uint addr = ValueKinds.ParseAddress(a[0]);
        var kind = a.Count > 1 ? ValueKinds.Parse(a[1]) : ValueKind.F32;
        int count = a.Count > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 1;
        var buf = new byte[kind.Size() * count];
        mem.Read(addr, buf);
        for (int i = 0; i < count; i++)
        {
            int off = i * kind.Size();
            Console.WriteLine($"{ValueKinds.FormatAddress(addr + (uint)off)}  {kind.Label(),-4} {kind.Format(kind.Read(buf.AsSpan(off))),14}   {Convert.ToHexString(buf, off, kind.Size())}");
        }
        return 0;
    }

    private static int Watch(BrowserMemorySource mem)
    {
        var reader = new Nfsu2Reader(mem);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var shown = Stopwatch.StartNew();
        TelemetryFrame f = TelemetryFrame.NotDriving;
        while (!cts.IsCancellationRequested)
        {
            // Read at the app's 60 Hz, show 5 a second.
            f = reader.Read();
            if (shown.ElapsedMilliseconds >= 200)
            {
                shown.Restart();
                Console.WriteLine(!f.Driving ? $"{DateTime.Now:HH:mm:ss.fff} not driving (car {reader.Car:x8})" : string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:HH:mm:ss.fff} gear {f.Gear,2} rpm {f.Rpm,5:0} {f.SpeedMps * 3.6f,5:0.0} km/h  thr {f.Throttle:0.00} brk {f.Brake:0.00} hb {f.Handbrake:0.00} steer {f.Steer,5:0.00}  " +
                    $"v fwd {f.VelocityForwardMps,6:0.0} right {f.VelocityRightMps,5:0.0} up {f.VelocityUpMps,5:0.0}  a fwd {f.AccelForward,5:0.0} right {f.AccelRight,5:0.0} up {f.AccelUp,5:0.0}  " +
                    $"yaw rate {f.YawRate,5:0.00} pitch {f.PitchRate,5:0.00} roll {f.RollRate,5:0.00}  yaw {f.Yaw * 57.3f,4:0} pitch {f.Pitch * 57.3f,4:0.0} roll {f.Roll * 57.3f,4:0.0}"));
            }
            Thread.Sleep(16);
        }
        return 0;
    }

    private static int Effects(BrowserMemorySource mem)
    {
        var reader = new Nfsu2Reader(mem);
        var effects = new Nfsu2Effects(mem);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var shown = Stopwatch.StartNew();
        bool said = false;
        while (!cts.IsCancellationRequested)
        {
            var f = reader.Read();
            var ff = effects.Read();
            if (effects.Table != 0 && !said)
            {
                said = true;
                Console.WriteLine($"effect table at 0x{effects.Table:x8}");
            }
            if (shown.ElapsedMilliseconds >= 200)
            {
                shown.Restart();
                var playing = ff.Effects.Where(e => e.Playing).Select(e => e.IsPeriodic
                    ? $"#{e.Slot} {e.Name} {e.Magnitude}/{e.PeriodUs / 1000}ms"
                    : $"#{e.Slot} {e.Name} {e.Magnitude}");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:HH:mm:ss.fff} {(ff.Running ? "run " : "stop")} steer {f.Steer,5:0.00} a right {f.AccelRight,5:0.0} yaw rate {f.YawRate,5:0.00} {f.SpeedMps * 3.6f,4:0} km/h | " +
                    $"const {ff.ConstantForce,6} spring {ff.Spring.Offset}/{ff.Spring.Coefficient}/{ff.Spring.Saturation} damper {ff.Damper} | {ff.Effects.Count} fx: {string.Join(", ", playing)}"));
            }
            Thread.Sleep(16);
        }
        return 0;
    }

    // ---- serve: the bridge plus a probe port ----

    private static int Serve(BrowserBridge bridge)
    {
        bridge.Log += m => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {m}");
        bridge.StateChanged += () =>
        {
            var s = bridge.State;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} state: memory {s.MemoryBytes / 1048576.0:0} MB, {(s.Visible ? "visible" : "hidden")}, " +
                              $"pads [{string.Join("; ", s.Pads.Select(p => $"{p.Index} {p.Id}{(p.Spoofed ? " (spoofed)" : "")}"))}]");
        };
        bridge.Rumble += (s, w) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} rumble strong {s:0.00} weak {w:0.00}");

        var probe = new TcpListener(IPAddress.Loopback, ProbePort);
        probe.Start();
        Console.WriteLine($"Bridge on 127.0.0.1:{bridge.Port}, probe port 127.0.0.1:{ProbePort}. Ctrl+C to stop.");
        var mem = new BrowserMemorySource(bridge);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await probe.AcceptTcpClientAsync(cts.Token); }
                catch (OperationCanceledException) { return; }
                _ = Task.Run(() => ServeProbe(c, mem, bridge));
            }
        });
        try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); } catch (AggregateException) { }
        probe.Stop();
        return 0;
    }

    private static void ServeProbe(TcpClient c, BrowserMemorySource mem, BrowserBridge bridge)
    {
        using (c)
        {
            c.NoDelay = true;
            var stream = c.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    if (parts[0] == "S")
                    {
                        Reply(stream, JsonSerializer.Serialize(bridge.State) + "\n");
                        continue;
                    }
                    if (parts[0] != "R" || parts.Length < 3) { Reply(stream, "ERR expected R <addr> <len> or S\n"); continue; }
                    uint addr = ValueKinds.ParseAddress(parts[1]);
                    int len = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    var buf = new byte[len];
                    try { mem.Read(addr, buf); }
                    catch (MemorySourceException ex) { Reply(stream, "ERR " + ex.Message.Replace('\n', ' ') + "\n"); continue; }
                    Reply(stream, $"OK {len}\n");
                    stream.Write(buf);
                }
            }
            catch (IOException) { }
        }
    }

    private static void Reply(NetworkStream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
}

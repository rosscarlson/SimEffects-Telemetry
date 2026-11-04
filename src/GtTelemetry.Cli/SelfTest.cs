using System.Buffers.Binary;
using GtTelemetry.Core;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Telemetry;
using GtTelemetry.Core.Scanning;
using GtTelemetry.Core.Sessions;
using GtTelemetry.Core.Speech;

namespace GtTelemetry.Cli;

/// <summary>
/// Runs real session scripts against synthetic RAM with planted values and a scripted "driver", and checks that the
/// planted addresses survive while decoys (booleans, counters, noise) are eliminated. No emulator needed.
/// </summary>
internal static class SelfTest
{
    private sealed class FakeDriver : IAnnouncer
    {
        public int Said;
        public Task SayAsync(string text, CancellationToken ct = default) { Interlocked.Increment(ref Said); return Task.CompletedTask; }
        public void Play(Cue cue) { }
    }

    /// <summary>RAM whose planted values follow the current step's label; everything else is static or noise.</summary>
    private sealed class FakeRam(Func<int> step, SessionScript script) : ISnapshotSource
    {
        private readonly byte[] ram = MakeRam();
        private readonly Random rng = new(7);
        private uint frame;
        private float rising;

        private static byte[] MakeRam()
        {
            var r = new byte[Pcsx2Connection.RamSize];
            new Random(1).NextBytes(r.AsSpan(0x00100000, 0x00400000));
            return r;
        }

        public void ReadBulk(uint address, Span<byte> dest)
        {
            int si = step();
            double label = si >= 0 && si < script.Steps.Count ? script.Steps[si].Label ?? 0 : 0;
            bool discrete = script.Steps.All(s => s.Label is null || s.Label >= 1);   // gears: exact integers
            double v = label == 0.5 ? 0.47 : label;                 // a human never holds exactly half
            if (!discrete) v += (rng.NextDouble() - 0.5) * 0.004 * Math.Abs(v);    // small sensor jitter
            BinaryPrimitives.WriteSingleLittleEndian(ram.AsSpan(0x00500000), (float)v);           // target f32
            ram[0x00500010] = discrete ? (byte)label : (byte)Math.Clamp(Math.Round(v * 127 + 128), 0, 255); // target u8
            ram[0x00500020] = (byte)(v > 0.01 ? 1 : 0);                                           // decoy boolean
            BinaryPrimitives.WriteUInt32LittleEndian(ram.AsSpan(0x00600000), ++frame);            // decoy frame counter
            rising += 13.7f;
            BinaryPrimitives.WriteSingleLittleEndian(ram.AsSpan(0x00500030), rising);             // decoy always-rising float
            rng.NextBytes(ram.AsSpan(0x00800000, 0x10000));                                       // noise region
            ram.AsSpan((int)address, dest.Length).CopyTo(dest);
        }
    }

    public static async Task<int> RunAsync()
    {
        int failures = 0;
        foreach (var id in new[] { "throttle", "steering", "gear", "race-state" })
        {
            var (scripts, _) = SessionScript.LoadAll(ProjectPaths.Sessions);
            var script = scripts.First(s => s.Id == id);
            foreach (var st in script.Steps) { st.Settle = 0; st.Sample = 0.15; }

            var driver = new FakeDriver();
            var ram = new FakeRam(() => driver.Said - 2, script);   // first Say is the intro
            var runner = new SessionRunner(ram, driver, new SessionOptions { MaxSampleHz = 60 });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await runner.RunAsync(script, CancellationToken.None);

            bool Has(uint a, ValueKind k) => result.Candidates.Any(c => c.Address == a && c.Kind == k);
            var expect = new List<(string What, bool Ok)>();
            bool analog = script.Steps.Select(s => s.Label).Distinct().Count() > 2;
            bool signed = script.Steps.Any(s => s.Label < 0);
            if (script.Kinds().Contains(ValueKind.F32)) expect.Add(("planted f32 found", Has(0x00500000, ValueKind.F32)));
            if (script.Kinds().Contains(ValueKind.U8)) expect.Add(("planted u8 found", Has(0x00500010, ValueKind.U8)));
            if (analog && !signed) expect.Add(("boolean decoy rejected", !Has(0x00500020, ValueKind.U8)));
            expect.Add(("frame counter rejected", !Has(0x00600000, ValueKind.U32)));
            expect.Add(("rising float rejected", !Has(0x00500030, ValueKind.F32)));
            expect.Add(("noise rejected", !result.Candidates.Any(c => c.Address is >= 0x00800000 and < 0x00810000)));

            Console.WriteLine($"{id,-11} {sw.ElapsedMilliseconds,5} ms  {result.TotalSurvivors,6:N0} survivors  " +
                              string.Join("  ", expect.Select(e => (e.Ok ? "ok " : "FAIL ") + e.What)));
            foreach (var c in result.Candidates.Take(4))
                Console.WriteLine($"              {c.AddressText} {c.Kind.Label(),-4} {c.Score:0.00}  {string.Join(" | ", c.StepValues.Select(v => c.Kind.Format(v)))}  ({c.Notes})");
            failures += expect.Count(e => !e.Ok);
        }

        // Forza Data Out: Horizon offsets are the Dash offsets shifted by 12 after the Sled block (232).
        var frame = new Core.Telemetry.TelemetryFrame
        {
            Driving = true, Rpm = 6500, MaxRpm = 7500, IdleRpm = 800, SpeedMps = 30, VelocityForwardMps = 30,
            Throttle = 1, Brake = 0.5f, Steer = -1, Gear = 3, AccelRight = 2, AccelUp = 3, AccelForward = 4, YawRate = 0.5f,
            Wheels =
            [
                new() { SuspensionNormalized = 0.25f, SlipRatio = 0.3f, SlipAngle = 0.4f },
                new(), new(),
                new() { OnRumbleStrip = true, SurfaceRumble = 0.7f, SuspensionMeters = 0.05f },
            ],
        };
        var h = Core.Telemetry.ForzaDataOut.Encode(frame, 1234, Core.Telemetry.ForzaFormat.Horizon);
        var d = Core.Telemetry.ForzaDataOut.Encode(frame, 1234, Core.Telemetry.ForzaFormat.Dash);
        var packetChecks = new (string What, bool Ok)[]
        {
            ("sizes", h.Length == 324 && d.Length == 311),
            ("race on", BitConverter.ToInt32(h, 0) == 1),
            ("rpm", BitConverter.ToSingle(h, 16) == 6500 && BitConverter.ToSingle(h, 8) == 7500),
            ("speed", BitConverter.ToSingle(h, 256) == 30 && BitConverter.ToSingle(d, 244) == 30),
            ("pedals", h[315] == 255 && h[316] == 128),
            ("gear", h[319] == 3 && d[307] == 3),
            ("steer", (sbyte)h[320] == -127),
            ("accel", BitConverter.ToSingle(h, 20) == 2 && BitConverter.ToSingle(h, 24) == 3 && BitConverter.ToSingle(h, 28) == 4),
            ("yaw rate", BitConverter.ToSingle(h, 48) == 0.5f),
            ("wheels", BitConverter.ToSingle(h, 68) == 0.25f && BitConverter.ToSingle(h, 84) == 0.3f && BitConverter.ToSingle(h, 164) == 0.4f
                       && Math.Abs(BitConverter.ToSingle(h, 180) - 0.5f) < 1e-6),
            ("surface", BitConverter.ToInt32(h, 128) == 1 && BitConverter.ToSingle(h, 160) == 0.7f && BitConverter.ToSingle(h, 208) == 0.05f),
            ("not driving", BitConverter.ToInt32(Core.Telemetry.ForzaDataOut.Encode(Core.Telemetry.TelemetryFrame.NotDriving, 1, Core.Telemetry.ForzaFormat.Horizon), 0) == 0),
        };
        Console.WriteLine("forza-out   " + string.Join("  ", packetChecks.Select(e => (e.Ok ? "ok " : "FAIL ") + e.What)));
        failures += packetChecks.Count(e => !e.Ok);

        failures += DisplayChecks();
        failures += Gt6DisplayChecks();
        failures += Gt5DisplayChecks();
        failures += CameraChecks();
        failures += Ps3Checks(Ps3CarLayout.Gt6);
        failures += Ps3Checks(Ps3CarLayout.Gt6v105);
        failures += Ps3Checks(Ps3CarLayout.Gt5);
        failures += Ps3Checks(Ps3CarLayout.Gt5v211);

        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Plain RAM for the camera checks; the "game" copies the table's seat into the camera object on demand.</summary>
    private sealed class PlainRam : IMemorySource, ISnapshotSource
    {
        public readonly byte[] Ram = new byte[Pcsx2Connection.RamSize];
        public string Name => "test";
        public bool IsConnected => true;
        public void Connect() { }
        public void Disconnect() { }
        public void Dispose() { }
        public void Read(uint address, Span<byte> dest) => Ram.AsSpan((int)address, dest.Length).CopyTo(dest);
        public void ReadBulk(uint address, Span<byte> dest) => Read(address, dest);
        public void Write(uint address, ReadOnlySpan<byte> data) => data.CopyTo(Ram.AsSpan((int)address));
        public void F(uint a, float v) => BinaryPrimitives.WriteSingleLittleEndian(Ram.AsSpan((int)a), v);
        public float F(uint a) => BinaryPrimitives.ReadSingleLittleEndian(Ram.AsSpan((int)a));
    }

    /// <summary>PS3 memory for the GT6 checks: one mapped, big-endian range.</summary>
    private sealed class Ps3Ram(uint start, int size) : IMemorySource, IRegionSource
    {
        public readonly byte[] Ram = new byte[size];
        public string Name => "test";
        public bool IsConnected => true;
        public void Connect() { }
        public void Disconnect() { }
        public void Dispose() { }
        public void Read(uint address, Span<byte> dest) => Ram.AsSpan((int)(address - start), dest.Length).CopyTo(dest);
        public void Write(uint address, ReadOnlySpan<byte> data) => data.CopyTo(Ram.AsSpan((int)(address - start)));
        public IReadOnlyList<GuestRegion> Regions(uint from, uint to) => [new(start, (uint)size)];
        public void F(uint a, float v) => BinaryPrimitives.WriteSingleBigEndian(Ram.AsSpan((int)(a - start)), v);
        public void B(uint a, byte v) => Ram[a - start] = v;
    }

    /// <summary>GT5/GT6: a car block laid out like the ones mapped on 2026-11-03, found by search and read into a frame.</summary>
    private static int Ps3Checks(Ps3CarLayout L)
    {
        const uint heap = 0x42000000, car = 0x42787400;
        var m = new Ps3Ram(heap, 0x01000000);
        void Car(uint c, float rpm)
        {
            void V(int o, float x, float y, float z) { m.F(c + (uint)o, x); m.F(c + (uint)o + 4, y); m.F(c + (uint)o + 8, z); }
            V(L.AxisForward, 0, 1, 0);
            V(L.AxisRight, 1, 0, 0);
            V(L.AxisUp, 0, 0, 1);
            V(L.AngularVelocity, 0.1f, 0.2f, 0.3f);
            V(L.Velocity, 0, 20, 0);
            V(L.Accel, 1, 2, 3);
            m.F(c + (uint)L.Rpm, rpm);
            m.F(c + (uint)L.Speed, 20);
            m.B(c + (uint)L.Gear, 3);
            m.F(c + (uint)L.Throttle, 0.6f);
            m.F(c + (uint)L.Steer, 0.75f);
            for (int i = 0; i < 4; i++)
            {
                int w = L.Wheel0 + i * L.WheelStride;
                V(w + L.WheelMatrix, 1, 0, 0);
                V(w + L.WheelMatrix + 12, 0, 1, 0);
                V(w + L.WheelMatrix + 24, 0, 0, 1);
                m.F(c + (uint)(w + L.WheelSuspension), 0.2f);
            }
            int fl = L.Wheel0, fr = fl + L.WheelStride;
            m.B(c + (uint)(fl + L.WheelSurface), 1);
            m.B(c + (uint)(fr + L.WheelSurface), 2);
            m.F(c + (uint)(fl + L.WheelSlipRatio), -0.05f);
            m.F(c + (uint)(fl + L.WheelSlipAngle), 0.075f);
        }
        // A decoy: unit axes without the wheels of a car.
        m.F(0x42100000 + (uint)L.AxisForward, 1);
        m.F(0x42100000 + (uint)L.AxisRight + 4, 1);
        m.F(0x42100000 + (uint)L.AxisUp + 8, 1);
        Car(car, 5000);

        var t = new Ps3GtTelemetry(L, m, m);
        var found = t.Search();
        var f = t.Read();
        var w = f.Wheels ?? new WheelState[4];
        bool stillDriving = t.Read().Driving;
        Thread.Sleep(250);
        bool paused = !t.Read().Driving;
        m.F(car + (uint)L.Rpm, 5100);
        bool resumed = t.Read().Driving;
        Car(0x42900000, 3000);
        m.F(car + (uint)L.AxisUp, 5);   // the old block is gone
        bool lost = !t.Read().Driving && t.Car == 0;
        var again = new Ps3GtTelemetry(L, m, m).Search();

        var checks = new (string What, bool Ok)[]
        {
            ("car found", found.Count == 1 && found[0] == car),
            ("driving", f.Driving && stillDriving),
            ("engine", f.Rpm == 5000 && f.Gear == 3 && f.SpeedMps == 20 && f.VelocityForwardMps == 20),
            ("inputs", Math.Abs(f.Throttle - 0.6f) < 1e-6 && Math.Abs(f.Steer - 0.5f) < 1e-6 && f.Brake == 0),
            ("rates", Math.Abs(f.PitchRate + 0.1f) < 1e-6 && Math.Abs(f.RollRate + 0.2f) < 1e-6 && Math.Abs(f.YawRate + 0.3f) < 1e-6),
            ("accel", f.AccelForward == 1 && f.AccelRight == 2 && f.AccelUp == 3),
            ("kerb", w[0].OnRumbleStrip && !w[1].OnRumbleStrip && w[1].SurfaceRumble > 0 && !w[2].OnRumbleStrip && w[2].SurfaceRumble == 0),
            ("slip", Math.Abs(w[0].SlipRatio - 0.5f) < 1e-5 && Math.Abs(w[0].SlipAngle - 0.5f) < 1e-5),
            ("paused", paused && resumed),
            ("car lost", lost),
            ("car moved", again.Count == 1 && again[0] == 0x42900000),
        };
        Console.WriteLine($"{L.Game.ToLowerInvariant(),-12}" + string.Join("  ", checks.Select(e => (e.Ok ? "ok " : "FAIL ") + e.What)));
        return checks.Count(e => !e.Ok);
    }

    /// <summary>The camera on RAM laid out like Laguna Seca's: three table copies with cockpit and hood entries.</summary>
    private static int CameraChecks()
    {
        var m = new PlainRam();
        uint[] tables = [0x01C72000, 0x01D08000, 0x01D9E000];
        foreach (var t in tables)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(m.Ram.AsSpan((int)t + 0x2E4), 3);
            m.F(t + 0x2E8, 0.325f); m.F(t + 0x2EC, -0.985f); m.F(t + 0x2F0, 0.03f);   // cockpit
            m.F(t + 0xA4, 0.0265f);
            m.F(t + 0xA8, 0f); m.F(t + 0xAC, -0.9079f); m.F(t + 0xB0, -0.2395f);      // hood
        }
        BinaryPrimitives.WriteUInt32LittleEndian(m.Ram.AsSpan(0x007342B8), 0x01E6F4F0);
        BinaryPrimitives.WriteUInt32LittleEndian(m.Ram.AsSpan((int)Core.Camera.Gt4SpecIICamera.FovInstruction), 0x3C013F80);
        m.F(0x00C670A4, 1f); m.F(0x00C670A8, 100f);
        void Frame(uint entry) => m.Ram.AsSpan((int)entry, 12).CopyTo(m.Ram.AsSpan(0x00C670E0));   // the game's per-frame copy

        var cam = new Core.Camera.Gt4SpecIICamera(m, m);
        int found = 0;
        cam.ViewFound += _ => found++;
        Frame(tables[0] + 0x2E8);
        var cockpit = cam.Poll().View;
        bool foundCockpit = cockpit is { Name: "Cockpit", Key: "0.3250,-0.9850,0.0300" };
        if (cockpit != null) cam.SetSeat(cockpit, new Core.Camera.SeatOffset(0.01f, 0.03f, -0.05f));
        // Paused: the camera keeps showing the old seat while more changes are made.
        bool paused = cam.Poll().View == cockpit;
        if (cockpit != null) cam.SetSeat(cockpit, new Core.Camera.SeatOffset(0.02f, 0.03f, -0.05f));
        paused &= cam.Poll().View == cockpit;
        bool allCopies = tables.All(t => Math.Abs(m.F(t + 0x2EC) - (-1.015f)) < 1e-5 && Math.Abs(m.F(t + 0x2E8) - 0.305f) < 1e-5
                                          && Math.Abs(m.F(t + 0x2F0) - (-0.02f)) < 1e-5);
        Frame(tables[0] + 0x2E8);
        bool stillCockpit = cam.Poll().View == cockpit && found == 1;
        Frame(tables[0] + 0xA8);
        var hood = cam.Poll().View;
        bool foundHood = hood is { Name: "Hood" } && found == 2;
        Frame(tables[0] + 0x2E8);
        bool backToCockpit = cam.Poll().View == cockpit && found == 2;
        // A restarted race reloads the table with the game's values: the view is found again as new.
        foreach (var t in tables) { m.F(t + 0x2E8, 0.325f); m.F(t + 0x2EC, -0.985f); m.F(t + 0x2F0, 0.03f); }
        Frame(tables[0] + 0x2E8);
        var again = cam.Poll().View;
        bool reloaded = again != null && again != cockpit && again.Offset.IsZero && found == 3;

        bool fovRoundTrip = Core.Camera.Gt4SpecIICamera.FromMultiplier(1f) == 0x3F80 && Core.Camera.Gt4SpecIICamera.ToMultiplier(0x3FA0) == 1.25f;
        cam.SetFov(0x3F7E);
        bool fovWritten = BinaryPrimitives.ReadUInt32LittleEndian(m.Ram.AsSpan((int)Core.Camera.Gt4SpecIICamera.FovInstruction)) == 0x3C013F7E;
        BinaryPrimitives.WriteUInt32LittleEndian(m.Ram.AsSpan((int)Core.Camera.Gt4SpecIICamera.FovInstruction), 0x27BDFFF0);
        bool fovGuarded = !cam.SetFov(0x3F80) && cam.ReadFov() == null
                          && BinaryPrimitives.ReadUInt32LittleEndian(m.Ram.AsSpan((int)Core.Camera.Gt4SpecIICamera.FovInstruction)) == 0x27BDFFF0;

        var checks = new (string What, bool Ok)[]
        {
            ("cockpit found", foundCockpit), ("while paused", paused), ("all copies moved", allCopies), ("still cockpit", stillCockpit),
            ("hood found", foundHood), ("back to cockpit", backToCockpit), ("race restart", reloaded),
            ("fov steps", fovRoundTrip), ("fov written", fovWritten), ("fov guarded", fovGuarded),
        };
        Console.WriteLine("camera      " + string.Join("  ", checks.Select(e => (e.Ok ? "ok " : "FAIL ") + e.What)));
        return checks.Count(e => !e.Ok);
    }

    private static bool Same(string[] group, string values) =>
        string.Join(" ", group.Where(l => l.StartsWith("patch=")).Select(l => l.Split(',')[4].Split(' ')[0])) == values;

    /// <summary>Display modes on a scratch copy of PCSX2's files shaped like the real ones.</summary>
    private static int DisplayChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gtt-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "inis"));
            Directory.CreateDirectory(Path.Combine(dir, "gamesettings"));
            Directory.CreateDirectory(Path.Combine(dir, "cheats"));
            var ini = Core.Display.Gt4SpecIIDisplay.SettingsPath(dir);
            var pnach = Path.Combine(dir, "cheats", "646B2E29.pnach");
            File.WriteAllText(ini, "[EmuCore]\r\nEnableCheats = true\r\n\r\n\r\n[Cheats]\r\nEnable = 32:9 Ultrawide Hack\r\nEnable = Deinterlace menus\r\n");
            File.WriteAllText(pnach, "gametitle=GT4\r\n[32:9 Ultrawide Hack]\r\npatch=1,EE,0099C3C8,word,40000000\r\n");

            var modes = Core.Display.Gt4SpecIIDisplay.Modes;
            var before = Core.Display.Gt4SpecIIDisplay.Read(dir);
            var r1 = Core.Display.Gt4SpecIIDisplay.Apply(dir, modes.First(m => m.Id == "32:9"));
            var after = Core.Display.Gt4SpecIIDisplay.Read(dir);
            var iniText = File.ReadAllText(ini);
            var r2 = Core.Display.Gt4SpecIIDisplay.Apply(dir, modes.First(m => m.Id == "32:9"));
            Core.Display.Gt4SpecIIDisplay.Apply(dir, modes.First(m => m.Id == "16:9"));
            var back = Core.Display.Gt4SpecIIDisplay.Read(dir);
            var pnachText = File.ReadAllText(pnach);
            var iniText16 = File.ReadAllText(ini);

            // Files written while the app was called GT Telemetry: still read as 32:9, and renamed on the next Apply.
            File.WriteAllText(ini, "[EmuCore]\r\nEnableCheats = true\r\n\r\n[Cheats]\r\nEnable = GT Telemetry 32:9 Ultrawide (HUD scaled)\r\n\r\n[EmuCore/GS]\r\nAspectRatio = Stretch\r\n");
            File.WriteAllText(pnach, "gametitle=GT4\r\n// ---- GT Telemetry: managed display patches. GT Telemetry rewrites this block; edit outside it. ----\r\n[GT Telemetry 32:9 Ultrawide (HUD scaled)]\r\n// ---- end of GT Telemetry block ----\r\n");
            var legacy = Core.Display.Gt4SpecIIDisplay.Read(dir);
            Core.Display.Gt4SpecIIDisplay.Apply(dir, modes.First(m => m.Id == "32:9"));
            var renamedIni = File.ReadAllText(ini);
            var renamedPnach = File.ReadAllText(pnach);
            bool renamed = legacy.Mode?.Id == "32:9" && !renamedIni.Contains("GT Telemetry") && !renamedPnach.Contains("GT Telemetry")
                           && renamedIni.Contains("Enable = " + Core.Display.Gt4SpecIIDisplay.ManagedCheat)
                           && renamedPnach.Split(Core.Display.Gt4SpecIIDisplay.ManagedCheat).Length == 2;

            var checks = new (string What, bool Ok)[]
            {
                ("custom detected", before.Mode == null),
                ("applied", after.Mode?.Id == "32:9" && iniText.Contains("AspectRatio = Stretch") && iniText.Contains("Enable = Deinterlace menus")
                            && !iniText.Contains("Enable = 32:9 Ultrawide Hack")),
                ("backed up", r1.Backups.Count == 2 && r1.Backups.All(File.Exists)),
                ("idempotent", r2.Changed.Count == 0),
                ("one block", pnachText.Split(Core.Display.Gt4SpecIIDisplay.ManagedCheat).Length == 2 && pnachText.StartsWith("gametitle=GT4\r\n")),
                ("16:9", back.Mode?.Id == "16:9" && iniText16.Contains("Enable = Deinterlace menus")),
                // The formulas must reproduce Silentwarior112's own 32:9 (his "64:9" group) and 48:9 values.
                ("formula 32:9", Same(Core.Display.Gt4SpecIIDisplay.Ultrawide("x", 32, ""),
                    "40000000 3C01C4A0 3C01443B 3C013EC0 03CF 3C013ECD 34210000 3C013EC0 3C014420 3C01C420 3C01C320 3C014320 3C01C420 3C014420")),
                ("formula 48:9", Same(Core.Display.Gt4SpecIIDisplay.Ultrawide("x", 48, "")[3..11],
                    "40400000 3C01C507 3C014495 3C013E80 05B7 3C013E89 34210000 3C013E80")),
                ("64:9 block", pnachText.Contains(Core.Display.Gt4SpecIIDisplay.ManagedCheat64) && pnachText.Contains("word,40800000")),
                ("old names", renamed),
            };
            Console.WriteLine("display     " + string.Join("  ", checks.Select(e => (e.Ok ? "ok " : "FAIL ") + e.What)));
            return checks.Count(e => !e.Ok);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>GT6 display: its RPCS3 config on a scratch folder shaped like the real one, and the aspect found and set in
    /// memory laid out like 1.05's display block.</summary>
    private static int Gt6DisplayChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gtt-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            Directory.CreateDirectory(Path.Combine(dir, "GuiConfigs"));
            const string global = "Core:\n  PPU Threads: 2\n  Accurate RSX reservation access: false\n  Libraries Control:\n    - libvdec.sprx:lle\n"
                                + "Video:\n  Renderer: Vulkan\n  Read Color Buffers: false\n  Stretch To Display Area: false\n  Vulkan:\n    Adapter: \"\"\n"
                                + "  Write Color Buffers: false\nAudio:\n  Volume: 100\n";
            File.WriteAllText(Path.Combine(dir, "config", "config.yml"), global);
            File.WriteAllText(Path.Combine(dir, "GuiConfigs", "config_database.dat"),
                "{\"games\":{\"BCUS98296\":{\"config\":\"Core:\\n  Accurate RSX reservation access: true\\nVideo:\\n  Read Color Buffers: true\\n  Write Color Buffers: true\"}}}");
            var modes = Core.Display.Gt6Display.Modes;
            var path = Core.Display.Gt6Display.ConfigPath(dir, "BCUS98296");

            bool noneYet = Core.Display.Gt6Display.ReadStretch(dir, "BCUS98296") == null;
            var r1 = Core.Display.Gt6Display.ApplyConfig(dir, "BCUS98296", modes.First(m => m.Id == "32:9"));
            var wide = File.ReadAllText(path);
            var r2 = Core.Display.Gt6Display.ApplyConfig(dir, "BCUS98296", modes.First(m => m.Id == "21:9"));
            var r3 = Core.Display.Gt6Display.ApplyConfig(dir, "BCUS98296", modes.First(m => m.Id == "16:9"));
            bool narrow = Core.Display.Gt6Display.ReadStretch(dir, "BCUS98296") == false;
            string expected = global.Replace("reservation access: false", "reservation access: true").Replace("Buffers: false", "Buffers: true")
                                    .Replace("Display Area: false", "Display Area: true");

            // Without RPCS3's database the known GT6 fixes are used; a key missing from the config is added to its section.
            File.Delete(Path.Combine(dir, "GuiConfigs", "config_database.dat"));
            var known = Core.Display.Gt6Display.DatabaseFixes(dir, "BCUS98296");
            var added = Core.Display.YamlLines.Set("Core:\n  A: 1\nVideo:\n  B: 2\n\nAudio:\n  C: 3\n", ["Video", "D"], "true");
            var nested = Core.Display.YamlLines.Set(global, ["Video", "Vulkan", "Adapter"], "\"GPU\"");

            // The display block: 1280 × 720 twice, aspect twice, 1, 0.5, 0.5, 1.0.
            var ram = new Ps3Ram(0x4FF00000, 0x100000);
            uint at = 0x4FFD33AC;
            foreach (var (o, v) in new[] { (-0x10, 1280u), (-0xC, 720u), (-8, 1280u), (-4, 720u), (8, 1u) })
                BinaryPrimitives.WriteUInt32BigEndian(ram.Ram.AsSpan((int)(at - 0x4FF00000) + o), v);
            ram.F(at, 16f / 9); ram.F(at + 4, 16f / 9); ram.F(at + 0xC, 0.5f); ram.F(at + 0x10, 0.5f); ram.F(at + 0x14, 1f);
            var writer = new Core.Display.Gt6AspectWriter(ram, ram);
            var first = writer.Apply(32f / 9);
            float now = BinaryPrimitives.ReadSingleBigEndian(ram.Ram.AsSpan((int)(at - 0x4FF00000)));
            var second = writer.Apply(32f / 9);
            var empty = new Core.Display.Gt6AspectWriter(new Ps3Ram(0x4FF00000, 0x1000), new Ps3Ram(0x4FF00000, 0x1000)).Apply(32f / 9);

            var checks = new (string What, bool Ok)[]
            {
                ("no config", noneYet),
                ("32:9 config", wide == expected && r1.Backups.Count == 0),
                ("21:9 same", r2.Changed.Count == 0),
                ("16:9 config", narrow && r3.Backups.Count == 1 && File.ReadAllText(path) == expected.Replace("Display Area: true", "Display Area: false")),
                ("known fixes", known.Count == 3),
                ("add key", added == "Core:\n  A: 1\nVideo:\n  B: 2\n  D: true\n\nAudio:\n  C: 3\n"),
                ("nested key", nested == global.Replace("Adapter: \"\"", "Adapter: \"GPU\"")),
                ("aspect set", writer.Address == at && first is > 1.77f and < 1.78f && Math.Abs(now - 32f / 9) < 1e-5f && second is > 3.55f and < 3.56f),
                ("no block", empty == null),
            };
            Console.WriteLine("gt6 display " + string.Join("  ", checks.Select(e => (e.Ok ? "ok " : "FAIL ") + e.What)));
            return checks.Count(e => !e.Ok);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>GT5 display: the patch turned on with each mode's value in patch_config.yml next to another patch, read
    /// back, turned off for 16:9, and a patch_config.yml made from nothing.</summary>
    private static int Gt5DisplayChecks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gtt-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            File.WriteAllText(Path.Combine(dir, "config", "config.yml"), "Core:\n  SPU Block Size: Safe\nVideo:\n  Stretch To Display Area: false\n");
            const string hash = "PPU-d73f342bf28ee016ef3d0ccb309b1acb03d8ecce";
            const string other = hash + ":\n  Unlock FPS:\n    Gran Turismo 5:\n      BCUS98114:\n        02.11:\n          Enabled: true\n";
            var patchConfig = Core.Display.Gt5Display.PatchConfigPath(dir);
            var modes = Core.Display.Gt5Display.Modes;

            var fresh = Core.Display.Gt5Display.Apply(dir, "BCUS98114", modes[2]);
            var made = File.ReadAllText(patchConfig);
            var config = File.ReadAllText(Core.Display.Gt6Display.ConfigPath(dir, "BCUS98114"));
            File.WriteAllText(patchConfig, other);
            Core.Display.Gt5Display.Apply(dir, "BCUS98114", modes[1]);
            var wide = File.ReadAllText(patchConfig);
            var read21 = Core.Display.Gt5Display.ReadMode(dir, "BCUS98114").Id;
            var same = Core.Display.Gt5Display.Apply(dir, "BCUS98114", modes[1]);
            Core.Display.Gt5Display.Apply(dir, "BCUS98114", modes[0]);
            var off = File.ReadAllText(patchConfig);
            var read169 = Core.Display.Gt5Display.ReadMode(dir, "BCUS98114").Id;

            const string ar = "  Aspect Ratio:\n    Gran Turismo 5:\n      BCUS98114:\n        02.11:\n          Enabled: true\n"
                            + "          Configurable Values:\n            Aspect Ratio: ";
            var checks = new (string What, bool Ok)[]
            {
                ("new file", made == hash + ":\n" + ar + "3.555555555555556\n" && fresh.Backups.Count == 0),
                ("gt5 config", config.Contains("SPU Block Size: Mega") && config.Contains("Stretch To Display Area: true")
                               && config.Contains("Disable Blit Engine Upscaling: true")),
                ("21:9 patch", wide == other + ar + "2.388888888888889\n" && read21 == "21:9"),
                ("21:9 same", same.Changed.Count == 0),
                ("16:9 off", off == other + ar.Replace("Enabled: true\n          Configurable", "Enabled: false\n          Configurable") + "2.388888888888889\n"
                             && read169 == "16:9"),
            };
            Console.WriteLine("gt5 display " + string.Join("  ", checks.Select(e => (e.Ok ? "ok " : "FAIL ") + e.What)));
            return checks.Count(e => !e.Ok);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

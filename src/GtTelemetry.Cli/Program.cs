using System.Diagnostics;
using System.Globalization;
using GtTelemetry.Core;
using GtTelemetry.Core.Adapters;
using GtTelemetry.Core.Addresses;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;
using GtTelemetry.Core.Sessions;
using GtTelemetry.Core.Speech;
using GtTelemetry.Core.Telemetry;

namespace GtTelemetry.Cli;

/// <summary>
/// Command-line companion to the app: the same core, driven from a terminal so discovery sessions can be run and
/// scripted while someone drives. Run with no arguments for help.
/// </summary>
internal static class Program
{
    private static int slot = PineClient.DefaultSlot;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var list = args.ToList();
        slot = int.Parse(TakeOption(list, "--slot") ?? PineClient.DefaultSlot.ToString(), CultureInfo.InvariantCulture);
        if (list.Count == 0) return Help();

        try
        {
            string cmd = list[0].ToLowerInvariant();
            var rest = list.Skip(1).ToList();
            return cmd switch
            {
                "info" => Info(),
                "read" => Read(rest),
                "watch" => Watch(rest),
                "write" => Write(rest),
                "snapshot" => Snapshot(rest),
                "bench" => Bench(),
                "sessions" => Sessions(),
                "run" => RunSession(rest).GetAwaiter().GetResult(),
                "say" => Say(rest),
                "pointers" => Pointers(rest),
                "mark" => Mark(rest),
                "stream" => Stream(rest).GetAwaiter().GetResult(),
                "camera" => Camera(),
                "record" => Record(rest).GetAwaiter().GetResult(),
                "selftest" => SelfTest.RunAsync().GetAwaiter().GetResult(),
                _ => Help(),
            };
        }
        catch (MemorySourceException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 3;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            gtt — SimEffects Telemetry discovery tool

              info                                  Connection + game details
              read <addr> [type] [count]            Read values (type: u8 s8 u16 s16 u32 s32 f32; default f32)
              watch <addr:type> ... [--hz N] [--for S]   Live values (Ctrl+C to stop)
              write <addr> <type> <value>           Write one value via PINE
              snapshot <file>                       Dump all 32 MB of EE RAM
              bench                                 Time a full snapshot over PINE and process memory
              sessions                              List guided capture sessions
              run <session> [--save-snapshots] [--no-voice] [--range 0xSTART-0xEND]
                                                    Run a guided capture (spoken instructions)
              say <text>                            Speak through the session voice
              pointers <addr> [maxOffset]           Find pointers to (or just before) an address
              mark <name> <addr> <type> [candidate|confirmed|rejected] [notes...] [--units U] [--source S]
                                                    Save a named address to data/gt4-spec2/addresses.json
              stream [--game gt4|gt5|gt6] [--host H] [--port 8000] [--format horizon|dash] [--hz 60] [--for S]
                                                    Send telemetry as Forza Data Out over UDP (SimHub);
                                                    gt5 and gt6 read RPCS3's memory directly
              record <session> [--range 0xSTART-0xEND|car] [--hz 30] [--no-voice]
                                                    Record a region continuously while the session's steps are spoken
              camera                                Current camera view, seat copies and FOV (read only)

            Options: --slot N (PINE slot, default 28011)
            """);
        return 1;
    }

    // ---- connection ----

    private static Pcsx2Connection Connect(bool quiet = false)
    {
        var c = new Pcsx2Connection(slot);
        c.Connect();
        if (!quiet)
        {
            if (c.PineError != null) Console.Error.WriteLine($"[PINE] {c.PineError}");
            if (c.ProcessError != null) Console.Error.WriteLine($"[process] {c.ProcessError}");
        }
        return c;
    }

    private static int Info()
    {
        using var c = new Pcsx2Connection(slot);
        try { c.Connect(); } catch (MemorySourceException) { }
        Console.WriteLine($"PINE (slot {slot}):   {(c.Pine.IsConnected ? "connected" : "not connected — " + c.PineError)}");
        Console.WriteLine($"Process memory:     {(c.Process.IsConnected ? $"attached (pid {c.Process.ProcessId}, EEmem found)" : "not attached — " + c.ProcessError)}");
        Console.WriteLine($"Snapshots via:      {c.SnapshotPath}");
        var info = c.GetGameInfo();
        if (info != null)
        {
            Console.WriteLine($"Emulator:           {info.EmulatorVersion}");
            Console.WriteLine($"Status:             {info.Status}");
            Console.WriteLine($"Game:               {info.Title} [{info.Id}] version {info.GameVersion} crc/uuid {info.Uuid}");
            var adapter = AdapterCatalog.ForGameId(info.Id);
            Console.WriteLine($"Adapter:            {adapter?.Name ?? "none matches this game"}");
        }
        return c.IsConnected ? 0 : 2;
    }

    // ---- read / write / watch ----

    private static int Read(List<string> a)
    {
        if (a.Count < 1) return Help();
        uint addr = ValueKinds.ParseAddress(a[0]);
        var kind = a.Count > 1 ? ValueKinds.Parse(a[1]) : ValueKind.F32;
        int count = a.Count > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 1;
        using var c = Connect();
        var buf = new byte[kind.Size() * count];
        c.Read(addr, buf);
        for (int i = 0; i < count; i++)
        {
            int off = i * kind.Size();
            Console.WriteLine($"{ValueKinds.FormatAddress(addr + (uint)off)}  {kind.Label(),-4} {kind.Format(kind.Read(buf.AsSpan(off))),14}   {Convert.ToHexString(buf, off, kind.Size())}");
        }
        return 0;
    }

    private static int Write(List<string> a)
    {
        if (a.Count < 3) return Help();
        uint addr = ValueKinds.ParseAddress(a[0]);
        var kind = ValueKinds.Parse(a[1]);
        double v = double.Parse(a[2], CultureInfo.InvariantCulture);
        using var c = Connect();
        c.Write(addr, kind.Encode(v));
        var back = new byte[kind.Size()];
        c.Read(addr, back);
        Console.WriteLine($"{ValueKinds.FormatAddress(addr)} {kind.Label()} = {kind.Format(kind.Read(back))}");
        return 0;
    }

    private static int Watch(List<string> a)
    {
        double hz = double.Parse(TakeOption(a, "--hz") ?? "10", CultureInfo.InvariantCulture);
        double? forSeconds = TakeOption(a, "--for") is string f ? double.Parse(f, CultureInfo.InvariantCulture) : null;
        var items = a.Select(s =>
        {
            var parts = s.Split(':');
            return (Addr: ValueKinds.ParseAddress(parts[0]), Kind: parts.Length > 1 ? ValueKinds.Parse(parts[1]) : ValueKind.F32);
        }).ToList();
        if (items.Count == 0) return Help();

        using var c = Connect();
        var buf = new byte[8];
        var sw = Stopwatch.StartNew();
        bool interactive = !Console.IsOutputRedirected;
        while (forSeconds == null || sw.Elapsed.TotalSeconds < forSeconds)
        {
            var line = string.Join("  ", items.Select(it =>
            {
                var span = buf.AsSpan(0, it.Kind.Size());
                c.Read(it.Addr, span);
                return $"{it.Addr:X8}={it.Kind.Format(it.Kind.Read(span)),10}";
            }));
            if (interactive) Console.Write($"\r{sw.Elapsed.TotalSeconds,6:0.0}s  {line}   ");
            else Console.WriteLine($"{sw.Elapsed.TotalSeconds:0.00}\t{line}");
            Thread.Sleep(TimeSpan.FromSeconds(1 / hz));
        }
        if (interactive) Console.WriteLine();
        return 0;
    }

    // ---- snapshots ----

    private static int Snapshot(List<string> a)
    {
        if (a.Count < 1) return Help();
        using var c = Connect();
        var buf = new byte[Pcsx2Connection.RamSize];
        var sw = Stopwatch.StartNew();
        c.ReadBulk(0, buf);
        File.WriteAllBytes(a[0], buf);
        Console.WriteLine($"Wrote {buf.Length:N0} bytes to {a[0]} via {c.SnapshotPath} in {sw.ElapsedMilliseconds} ms.");
        return 0;
    }

    private static int Bench()
    {
        using var c = Connect();
        var buf = new byte[Pcsx2Connection.RamSize];
        if (c.Process.IsConnected)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) c.Process.Read(0, buf);
            Console.WriteLine($"Process memory: {sw.Elapsed.TotalMilliseconds / 10:0.0} ms per 32 MB snapshot");
        }
        if (c.Pine.IsConnected)
        {
            var sw = Stopwatch.StartNew();
            c.Pine.Read(0, buf);
            Console.WriteLine($"PINE:           {sw.Elapsed.TotalMilliseconds:0} ms per 32 MB snapshot");

            var reqs = Enumerable.Range(0, 64).Select(i => ((uint)(0x00100000 + i * 4), 4)).ToArray();
            var small = new byte[64 * 4];
            sw.Restart();
            for (int i = 0; i < 200; i++) c.Pine.ReadMany(reqs, small);
            Console.WriteLine($"PINE:           {sw.Elapsed.TotalMilliseconds / 200:0.00} ms per batch of 64 telemetry-sized reads");
        }
        return 0;
    }

    // ---- sessions ----

    private static int Sessions()
    {
        var (scripts, errors) = SessionScript.LoadAll(ProjectPaths.Sessions);
        foreach (var s in scripts)
            Console.WriteLine($"{s.Id,-12} {s.Name,-22} ~{s.EstimatedDuration().TotalSeconds:0}s  {s.Description}");
        foreach (var e in errors) Console.Error.WriteLine(e);
        return 0;
    }

    private static SessionScript FindScript(string idOrPath)
    {
        if (File.Exists(idOrPath)) return SessionScript.Load(idOrPath);
        var (scripts, _) = SessionScript.LoadAll(ProjectPaths.Sessions);
        return scripts.FirstOrDefault(s => s.Id.Equals(idOrPath, StringComparison.OrdinalIgnoreCase))
               ?? throw new MemorySourceException($"No session '{idOrPath}'. Run 'gtt sessions' to list them.");
    }

    private static async Task<int> RunSession(List<string> a)
    {
        bool save = TakeFlag(a, "--save-snapshots");
        bool mute = TakeFlag(a, "--no-voice");
        string? range = TakeOption(a, "--range");
        if (a.Count < 1) return Help();
        var script = FindScript(a[0]);

        var options = new SessionOptions { CaptureRoot = ProjectPaths.Captures, SaveSnapshots = save };
        if (range != null)
        {
            var p = range.Split('-');
            options.Start = ValueKinds.ParseAddress(p[0]);
            options.Length = ValueKinds.ParseAddress(p[1]) - options.Start;
        }

        using var c = Connect();
        using var voice = new SpeechAnnouncer { Enabled = !mute };
        var runner = new SessionRunner(c, voice, options);
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine($"Session: {script.Name} — snapshots via {c.SnapshotPath}, types {string.Join(" ", script.Kinds().Select(k => k.Label()))}");
        runner.Progress += p =>
        {
            switch (p.Phase)
            {
                case SessionPhase.Speaking:
                    Console.WriteLine($"[{p.StepIndex + 1}/{p.StepCount}] \"{p.Message}\"");
                    break;
                case SessionPhase.StepDone:
                    Console.WriteLine($"        {p.Samples} samples · " + string.Join("  ", p.Survivors.Select(kv => $"{kv.Key.Label()}:{kv.Value:N0}")));
                    break;
            }
        };

        var result = await runner.RunAsync(script, cts.Token);
        Console.WriteLine();
        Console.WriteLine($"Done in {result.Duration.TotalSeconds:0}s. {result.TotalSurvivors:N0} survivors. Results: {result.CaptureDir}");
        Console.WriteLine();
        Console.WriteLine($"{"address",-11} {"type",-4} {"score",6}  values per step  (notes)");
        foreach (var cand in result.Candidates.Take(40))
            Console.WriteLine($"{cand.AddressText,-11} {cand.Kind.Label(),-4} {cand.Score,6:0.00}  {string.Join(" | ", cand.StepValues.Select(v => cand.Kind.Format(v)))}  ({cand.Notes})");
        return 0;
    }

    private static int Say(List<string> a)
    {
        using var voice = new SpeechAnnouncer();
        voice.SayAsync(string.Join(' ', a)).GetAwaiter().GetResult();
        return 0;
    }

    // ---- telemetry ----

    private static async Task<int> Stream(List<string> a)
    {
        var options = new StreamOptions(
            Host: TakeOption(a, "--host") ?? "127.0.0.1",
            Port: int.Parse(TakeOption(a, "--port") ?? "8000", CultureInfo.InvariantCulture),
            Format: Enum.Parse<ForzaFormat>(TakeOption(a, "--format") ?? "horizon", ignoreCase: true),
            Hz: double.Parse(TakeOption(a, "--hz") ?? "60", CultureInfo.InvariantCulture));
        double? forSeconds = TakeOption(a, "--for") is string f ? double.Parse(f, CultureInfo.InvariantCulture) : null;

        string game = (TakeOption(a, "--game") ?? "gt4").ToLowerInvariant();
        var layout = game is "gt5" or "gt6" ? Ps3CarLayout.ForGame(game) : null;
        using var c = layout != null ? null : Connect();
        using var rpcs3 = layout != null ? new Rpcs3ProcessMemory() : null;
        ITelemetrySource source;
        if (rpcs3 != null)
        {
            rpcs3.Connect();
            var sw = Stopwatch.StartNew();
            var gt6Source = new Ps3GtTelemetry(layout!, rpcs3, rpcs3);
            gt6Source.Read();
            var (title, id) = rpcs3.Game();
            Console.WriteLine($"RPCS3 {title} [{id}]: {gt6Source.LastSearchCount} car block(s), using 0x{gt6Source.Car:X8} ({gt6Source.Layout.Game} {gt6Source.Layout.Version} layout); search took {sw.ElapsedMilliseconds} ms");
            source = gt6Source;
        }
        else
        {
            if (!c!.Pine.IsConnected) throw new MemorySourceException("Telemetry needs PINE. " + c.PineError);
            source = new Gt4SpecIITelemetry(c);
        }
        var streamer = new TelemetryStreamer(source, options);
        var cts = forSeconds is double s ? new CancellationTokenSource(TimeSpan.FromSeconds(s)) : new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine($"Sending Forza {options.Format} packets ({ForzaDataOut.Size(options.Format)} bytes) to {options.Host}:{options.Port} at {options.Hz} Hz. Ctrl+C to stop.");
        streamer.Stats += st =>
        {
            var fr = st.Last;
            string state = fr.Driving
                ? $"gear {fr.Gear}  {fr.Rpm,5:0} rpm (max {fr.MaxRpm:0})  {fr.SpeedMps * 2.23694,5:0.0} mph  thr {fr.Throttle,4:P0}  brk {fr.Brake,4:P0}  steer {fr.Steer,5:0.00}"
                  + $"  acc F{fr.AccelForward,5:0.0} R{fr.AccelRight,5:0.0} U{fr.AccelUp,5:0.0}"
                  + (fr.Wheels is { } w
                      ? $"  susp {string.Join(" ", w.Select(x => x.SuspensionNormalized.ToString("0.00", CultureInfo.InvariantCulture)))}"
                        + $"  slip {string.Join(" ", w.Select(x => x.SlipRatio.ToString("0.0", CultureInfo.InvariantCulture)))}"
                        + $"  surface {string.Join("", w.Select(x => x.OnRumbleStrip ? "K" : x.SurfaceRumble > 0 ? "g" : "."))}"
                      : "")
                : "not driving (paused, menu or loading)";
            Console.WriteLine($"{st.Sent,7} sent  {st.ActualHz,5:0.0} Hz  {state}" + (st.Errors > 0 ? $"  [{st.Errors} errors: {st.LastError}]" : ""));
        };
        try { await streamer.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Stopped.");
        return 0;
    }

    /// <summary>
    /// Records a memory region continuously while a script's steps are spoken, for offline analysis of values that
    /// change over time (suspension, slip, accelerations). Output: frames.bin (per frame: f64 seconds, then the region)
    /// and steps.json (when each step started).
    /// </summary>
    private static async Task<int> Record(List<string> a)
    {
        string range = TakeOption(a, "--range") ?? "0x01E60000-0x01E80000";
        double hz = double.Parse(TakeOption(a, "--hz") ?? "30", CultureInfo.InvariantCulture);
        bool mute = TakeFlag(a, "--no-voice");
        if (a.Count < 1) return Help();
        var script = FindScript(a[0]);
        // "--range car" follows the car block (it moves between races): each frame stores the car address (u32) and
        // the region from car − 0x6C10 (the input block) to the end of the car block.
        bool followCar = range.Equals("car", StringComparison.OrdinalIgnoreCase);
        const int CarBefore = 0x6C10, CarAfter = 0x900;
        var p = range.Split('-');
        uint start = followCar ? 0 : ValueKinds.ParseAddress(p[0]);
        int length = followCar ? CarBefore + CarAfter : (int)(ValueKinds.ParseAddress(p[1]) - start);

        using var c = Connect();
        using var voice = new SpeechAnnouncer { Enabled = !mute };
        string dir = Path.Combine(ProjectPaths.Captures, $"{DateTime.Now:yyyyMMdd-HHmmss}-rec-{script.Id}");
        Directory.CreateDirectory(dir);
        var steps = new List<object>();
        var buf = new byte[length];
        var clock = Stopwatch.StartNew();
        long frames = 0;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Capture runs on its own thread for the whole script so step transitions are recorded too.
        var capture = Task.Run(async () =>
        {
            await using var file = new FileStream(Path.Combine(dir, "frames.bin"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            double interval = 1000.0 / hz, next = 0;
            var ptr = new byte[4];
            while (!cts.IsCancellationRequested)
            {
                double t = clock.Elapsed.TotalSeconds;
                file.Write(BitConverter.GetBytes(t));
                if (followCar)
                {
                    c.Read(0x007342B8, ptr);
                    uint car = BitConverter.ToUInt32(ptr) - 0x1AEB0;
                    bool valid = car is >= 0x01000000 and < Pcsx2Connection.RamSize - CarAfter;
                    if (valid) c.ReadBulk(car - CarBefore, buf);
                    else Array.Clear(buf);
                    file.Write(BitConverter.GetBytes(valid ? car : 0u));
                }
                else
                {
                    c.ReadBulk(start, buf);
                }
                file.Write(buf);
                file.Flush();   // so a recording stopped by killing the process is still complete
                frames++;
                next += interval;
                double wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait));
            }
        });

        Console.WriteLine($"Recording {ValueKinds.FormatAddress(start)}+0x{length:X} at {hz} Hz via {c.SnapshotPath} → {dir}");
        try
        {
            await voice.SayAsync(script.Intro, cts.Token);
            foreach (var (step, i) in script.Steps.Select((s, i) => (s, i)))
            {
                Console.WriteLine($"[{i + 1}/{script.Steps.Count}] {step.Say}");
                double said = clock.Elapsed.TotalSeconds;
                await voice.SayAsync(step.Say, cts.Token);
                steps.Add(new { index = i + 1, step.Label, say = step.Say, spokenAt = said, startAt = clock.Elapsed.TotalSeconds, seconds = step.Sample });
                await Task.Delay(TimeSpan.FromSeconds(step.Sample), cts.Token);
            }
            voice.Play(Cue.Done);
            await voice.SayAsync(script.Outro, cts.Token);
        }
        catch (OperationCanceledException) { }
        cts.Cancel();
        await capture;

        File.WriteAllText(Path.Combine(dir, "steps.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            script = script.Id, start = ValueKinds.FormatAddress(start), length, hz, frames, frameSize = 8 + length,
            seconds = clock.Elapsed.TotalSeconds, steps,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{frames} frames ({frames / clock.Elapsed.TotalSeconds:0.0} Hz) in {clock.Elapsed.TotalSeconds:0}s → {dir}");
        return 0;
    }

    private static int Camera()
    {
        using var c = Connect();
        var cam = new Core.Camera.Gt4SpecIICamera(c, c);
        var sw = Stopwatch.StartNew();
        var st = cam.Poll();
        Console.WriteLine($"Poll took {sw.ElapsedMilliseconds} ms.");
        Console.WriteLine(st.View is { } v
            ? $"{v.Name} view, game seat {v.Key} (x sideways, y vertical, z fore/aft)"
            : "No adjustable view (not in a race, or not in the cockpit or hood view).");
        Console.WriteLine($"FOV multiplier {st.FovMultiplier?.ToString("0.000", CultureInfo.InvariantCulture) ?? "— (instruction not found)"}, vertical FOV {st.VerticalFovDegrees?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—"}°");
        return st.View == null ? 2 : 0;
    }

    // ---- analysis helpers ----

    private static int Pointers(List<string> a)
    {
        if (a.Count < 1) return Help();
        uint target = ValueKinds.ParseAddress(a[0]);
        uint maxOff = a.Count > 1 ? ValueKinds.ParseAddress(a[1]) : 0x1000;
        using var c = Connect();
        var ram = new byte[Pcsx2Connection.RamSize];
        c.ReadBulk(0, ram);
        var hits = ManualScanner.FindPointers(ram, 0, target, maxOff);
        foreach (var (addr, ptr, off) in hits)
            Console.WriteLine($"{ValueKinds.FormatAddress(addr)} → 0x{ptr:X8}  (+0x{off:X})");
        Console.WriteLine($"{hits.Count} pointer(s) within 0x{maxOff:X} of {ValueKinds.FormatAddress(target)}.");
        return 0;
    }

    private static int Mark(List<string> a)
    {
        string? units = TakeOption(a, "--units");
        string? source = TakeOption(a, "--source");
        if (a.Count < 3) return Help();
        var status = a.Count > 3 && Enum.TryParse<AddressStatus>(a[3], true, out var s) ? s : AddressStatus.Candidate;
        string? notes = a.Count > 4 ? string.Join(' ', a.Skip(4)) : null;
        string path = ProjectPaths.AddressFile(AdapterCatalog.Gt4SpecII.DataFolder!);
        var book = AddressBook.Load(path);
        book.Game = AdapterCatalog.Gt4SpecII.Name;
        var e = book.Upsert(a[0], ValueKinds.ParseAddress(a[1]), ValueKinds.Parse(a[2]), status, notes, source, units);
        book.Save(path);
        Console.WriteLine($"{e.Name} = {e.Address} {e.Type} [{e.Status}] → {path}");
        return 0;
    }

    // ---- arg helpers ----

    private static string? TakeOption(List<string> a, string name)
    {
        int i = a.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= a.Count) return null;
        string v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }

    private static bool TakeFlag(List<string> a, string name) => a.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
}

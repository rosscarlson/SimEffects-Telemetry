using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using GtTelemetry.Core.Azimuth.Games;

namespace GtTelemetry.Core.Azimuth;

public enum ProbeStage { WaitingForGame, Scanning, Countdown, Recording, Done, Failed, Cancelled }

// Records every car-physics object while spoken cues tell the driver what to
// do, so fields can be labelled offline (tools/analyze_probe.py). The driver
// never needs to leave the game.
//
// File: "FH2PROBE" u32 version u32 blockSize, then gzip'd records:
//   1 = frame:  f64 t, u32 count, count x (u64 address, blockSize bytes)
//   2 = marker: f64 t, u16 length, UTF-8 text
public sealed class ProbeRecorder : IDisposable
{
    readonly GameDefinition? _fixedGame;
    GameDefinition _game = GameCatalog.All[0];
    readonly string _folder;
    readonly CancellationTokenSource _cts = new();
    readonly Thread _thread;
    readonly object _lock = new();

    ProbeStage _stage = ProbeStage.WaitingForGame;
    string _message = "Waiting for azimuth.exe to start.";
    double _elapsed;
    string? _path;

    // game null: whichever supported game is running.
    public ProbeRecorder(GameDefinition? game, string folder)
    {
        _fixedGame = game;
        _folder = folder;
        _thread = new Thread(Run) { IsBackground = true, Name = "Probe" };
    }

    public int Seconds => (_fixedGame ?? _game).ProbeSeconds;

    public void Start() => _thread.Start();

    public void Cancel()
    {
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(3000);
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }

    public (ProbeStage Stage, string Message, double Elapsed, string? Path) Status()
    {
        lock (_lock) return (_stage, _message, _elapsed, _path);
    }

    void Set(ProbeStage stage, string message, double elapsed = 0)
    {
        lock (_lock) { _stage = stage; _message = message; _elapsed = elapsed; }
    }

    void Run()
    {
        var ct = _cts.Token;
        using var voice = new Voice();
        try
        {
            if (_fixedGame is { IsMapped: false }) { Set(ProbeStage.Failed, _fixedGame.NotMappedReason!); return; }

            GameProcess? g;
            while ((g = GameProcess.Attach()) == null)
                if (ct.WaitHandle.WaitOne(1000)) throw new OperationCanceledException();
            using var game = g;
            while (true)
            {
                string why;
                var found = _fixedGame == null ? GameCatalog.Detect(g, out why, out _)
                    : _fixedGame.CheckFingerprint(g, out why) ? _fixedGame : null;
                if (found != null) { _game = found; break; }
                Set(ProbeStage.WaitingForGame, why);
                if (g.HasExited) { Set(ProbeStage.Failed, "azimuth.exe closed."); return; }
                if (ct.WaitHandle.WaitOne(1000)) throw new OperationCanceledException();
            }

            Set(ProbeStage.Scanning, "Looking for cars.");
            var cars = CarScanner.Scan(g, _game, ct);
            if (cars.Count == 0)
            {
                Set(ProbeStage.Failed, "No car found. Get into a car in free roam (not a menu), then start the probe again.");
                return;
            }

            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, $"probe-{_game.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
            lock (_lock) _path = path;
            using var fs = File.Create(path);
            fs.Write(Encoding.ASCII.GetBytes("FH2PROBE"));
            fs.Write(BitConverter.GetBytes(1u));
            fs.Write(BitConverter.GetBytes((uint)_game.ProbeBlockSize));
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            using var w = new BinaryWriter(gz);

            voice.Say("Probe starting in five seconds. Switch to the game.");
            for (int i = 5; i > 0; i--)
            {
                Set(ProbeStage.Countdown, $"{cars.Count} car(s) found. Starting in {i} s — switch to the game now.");
                if (ct.WaitHandle.WaitOne(1000)) throw new OperationCanceledException();
            }

            var script = _game.ProbeScript;
            var clock = Stopwatch.StartNew();
            var block = new byte[_game.ProbeBlockSize];
            int cue = 0;
            double nextScan = 10;
            string lastCue = "";
            Task<List<ulong>>? scan = null;
            var period = TimeSpan.FromSeconds(1.0 / 15);
            var next = clock.Elapsed;

            while (!g.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                double t = clock.Elapsed.TotalSeconds;
                if (t >= Seconds) break;

                while (cue < script.Count && t >= script[cue].At)
                {
                    Marker(w, t, script[cue].Say);
                    voice.Say(script[cue].Say);
                    lastCue = script[cue].Say;
                    cue++;
                }
                Set(ProbeStage.Recording, lastCue, t);

                if (scan == null && t >= nextScan) scan = Task.Run(() => CarScanner.Scan(g, _game, ct), ct);
                if (scan is { IsCompleted: true })
                {
                    if (scan.IsCompletedSuccessfully)
                        foreach (var c in scan.Result.Where(c => !cars.Contains(c)))
                        {
                            cars.Add(c);
                            Marker(w, t, $"new car {c:X}");
                        }
                    scan = null;
                    nextScan = t + 10;
                }

                var live = new List<(ulong, byte[])>();
                foreach (var c in cars)
                    if (g.Read(c, block) && _game.IsCar(block)) live.Add((c, (byte[])block.Clone()));
                w.Write((byte)1);
                w.Write(t);
                w.Write((uint)live.Count);
                foreach (var (addr, b) in live) { w.Write(addr); w.Write(b); }

                next += period;
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait); else next = clock.Elapsed;
            }

            Marker(w, clock.Elapsed.TotalSeconds, "end");
            w.Flush();
            voice.Say("Probe finished. Thank you.");
            Set(ProbeStage.Done, g.HasExited ? "The game closed; the recording so far was saved." : "Probe finished.", Seconds);
            Thread.Sleep(2500);  // let the voice finish
        }
        catch (OperationCanceledException)
        {
            Set(ProbeStage.Cancelled, "Probe cancelled.");
        }
        catch (Exception e)
        {
            Set(ProbeStage.Failed, e.Message);
        }
    }

    static void Marker(BinaryWriter w, double t, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        w.Write((byte)2);
        w.Write(t);
        w.Write((ushort)b.Length);
        w.Write(b);
    }
}

// Windows' built-in speech (SAPI), with a beep if it isn't available.
public sealed class Voice : IDisposable
{
    readonly dynamic? _v;

    public Voice()
    {
        try
        {
            var t = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (t != null) _v = Activator.CreateInstance(t);
        }
        catch { _v = null; }
    }

    public void Say(string text, bool beep = true)
    {
        if (beep) try { Console.Beep(880, 150); } catch { }
        try { _v?.Speak(text, 1 /* SVSFlagsAsync */ | 2 /* SVSFPurgeBeforeSpeak */); } catch { }
    }

    public void Dispose() { }
}

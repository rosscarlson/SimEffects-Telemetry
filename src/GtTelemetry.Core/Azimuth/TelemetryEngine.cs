using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GtTelemetry.Core.Azimuth.Games;

namespace GtTelemetry.Core.Azimuth;

public enum PacketFormat { Horizon, Dash }

public sealed record TelemetryOptions(string Host, int Port, PacketFormat Format, int Hz);

public enum EngineState { Stopped, WaitingForGame, WrongGame, NoCar, Sending, Paused, Error }

// A copy of the engine's state for the UI; taken with TelemetryEngine.Snapshot.
public sealed class EngineStatus
{
    public EngineState State;
    public string Detail = "";
    public int Cars;
    public ulong Car;
    public bool Pinned;
    public long Sent;
    public readonly CarSample Sample = new();
}

// Finds the player's car in azimuth.exe and sends Forza Data Out packets on a
// background thread until stopped. Reads game memory only.
public sealed class TelemetryEngine : IDisposable
{
    // Fixed, or (null at the start) whichever supported game is running.
    readonly GameDefinition? _fixedGame;
    volatile GameDefinition? _detected;
    GameDefinition _game = GameCatalog.All[0];
    readonly TelemetryOptions _opt;
    readonly CancellationTokenSource _cts = new();
    readonly object _lock = new();
    readonly EngineStatus _status = new();
    readonly Thread _thread;
    int _nextCar, _rescan;

    // Raised on the engine thread.
    public event Action<string>? Log;

    // The game being read; null until one is detected.
    public GameDefinition? Game => _detected;
    public TelemetryOptions Options => _opt;

    public TelemetryEngine(GameDefinition? game, TelemetryOptions options)
    {
        _fixedGame = game;
        _opt = options;
        _thread = new Thread(Run) { IsBackground = true, Name = "Telemetry", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(3000);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    public void NextCar() => Interlocked.Exchange(ref _nextCar, 1);
    public void Rescan() => Interlocked.Exchange(ref _rescan, 1);

    public void Snapshot(EngineStatus into)
    {
        lock (_lock)
        {
            into.State = _status.State;
            into.Detail = _status.Detail;
            into.Cars = _status.Cars;
            into.Car = _status.Car;
            into.Pinned = _status.Pinned;
            into.Sent = _status.Sent;
            _status.Sample.CopyTo(into.Sample);
        }
    }

    void Set(EngineState state, string detail)
    {
        lock (_lock) { _status.State = state; _status.Detail = detail; }
    }

    void Say(string text) => Log?.Invoke(text);

    void Run()
    {
        var ct = _cts.Token;
        try
        {
            if (_fixedGame is { IsMapped: false }) { Set(EngineState.Error, _fixedGame.NotMappedReason!); return; }
            var target = Resolve(_opt.Host, _opt.Port);
            using var udp = new UdpClient(target.AddressFamily);
            udp.Connect(target);
            Say($"Sending {(_opt.Format == PacketFormat.Horizon ? "Forza Horizon (324-byte)" : "FM7 Dash (311-byte)")} packets to {target} at {_opt.Hz} Hz.");

            while (!ct.IsCancellationRequested)
            {
                using var g = WaitForGame(ct);
                if (g == null) continue;
                RunSession(g, udp, ct);
                _detected = null;
                if (!ct.IsCancellationRequested) Say("Game closed; waiting for it to start again.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Set(EngineState.Error, e.Message);
            Say($"Stopped: {e.Message}");
            return;
        }
        Set(EngineState.Stopped, "");
    }

    static IPEndPoint Resolve(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var ip))
            ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? throw new InvalidOperationException($"Couldn't find the address of \"{host}\".");
        return new IPEndPoint(ip, port);
    }

    GameProcess? WaitForGame(CancellationToken ct)
    {
        Set(EngineState.WaitingForGame, "Waiting for azimuth.exe to start.");
        GameProcess? g;
        while ((g = GameProcess.Attach()) == null)
            if (ct.WaitHandle.WaitOne(1000)) throw new OperationCanceledException();
        Say($"Found azimuth.exe (pid {g.Pid}).");
        string last = "";
        while (true)
        {
            string why;
            bool loaded = true;
            var game = _fixedGame == null ? GameCatalog.Detect(g, out why, out loaded)
                : _fixedGame.CheckFingerprint(g, out why) ? _fixedGame : null;
            if (game != null) { _game = game; _detected = game; break; }
            if (g.HasExited) { g.Dispose(); return null; }
            if (why != last) { Set(loaded ? EngineState.WrongGame : EngineState.WaitingForGame, why); Say(why); last = why; }
            if (ct.WaitHandle.WaitOne(1000)) { g.Dispose(); throw new OperationCanceledException(); }
        }
        Say($"{_game.Name} detected ({_game.BuildDescription}).");
        return g;
    }

    void RunSession(GameProcess g, UdpClient udp, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var block = new byte[_game.BlockSize];
        var prev = new byte[_game.BlockSize];
        var sample = new CarSample();
        var idle = new CarSample();

        List<ulong> cars = new();
        Task<List<ulong>>? scan = null;
        double nextScan = 0, lastChange = 0;
        ulong selected = 0;
        bool pinned = false, paused = false, haveSample = false;
        int readFailures = 0;
        long sent = 0;
        var period = TimeSpan.FromSeconds(1.0 / _opt.Hz);
        var nextTick = clock.Elapsed;
        Set(EngineState.NoCar, "Looking for your car.");

        while (!ct.IsCancellationRequested && !g.HasExited)
        {
            double t = clock.Elapsed.TotalSeconds;

            // Background scan: at start, every 10 s, or when the car is lost.
            if (scan == null && t >= nextScan)
                scan = Task.Run(() => CarScanner.Scan(g, _game, ct), ct);
            if (scan is { IsCompleted: true })
            {
                if (scan.IsCompletedSuccessfully)
                {
                    var fresh = scan.Result;
                    // A scan can miss a car while the game reshuffles memory;
                    // keep the one we're reading as long as it still checks out.
                    if (selected != 0 && !fresh.Contains(selected) && readFailures == 0) fresh.Add(selected);
                    if (!fresh.SequenceEqual(cars)) Say($"{fresh.Count} car(s) found.");
                    cars = fresh;
                    // Follow the player into a new car (unless Next car picked one).
                    if (!pinned && selected != 0)
                    {
                        ulong best = PickCar(g, cars);
                        if (best != 0 && best != selected)
                        {
                            selected = best;
                            Say($"Switched to the player's car ({selected:X}).");
                        }
                    }
                }
                scan = null;
                nextScan = t + 10;
            }

            if (Interlocked.Exchange(ref _rescan, 0) == 1) nextScan = 0;
            if (Interlocked.Exchange(ref _nextCar, 0) == 1 && cars.Count > 0)
            {
                selected = cars[(cars.IndexOf(selected) + 1) % cars.Count];
                pinned = true;
                Say($"Using car {selected:X} ({cars.IndexOf(selected) + 1} of {cars.Count}).");
            }

            if (selected == 0 || !cars.Contains(selected))
            {
                selected = PickCar(g, cars);
                pinned = false;
                if (selected != 0) Say($"Using car {selected:X}.");
            }

            CarSample outSample = idle;
            if (selected != 0)
            {
                if (g.Read(selected, block) && _game.IsCar(block))
                {
                    readFailures = 0;
                    if (!block.AsSpan().SequenceEqual(prev)) { lastChange = t; block.CopyTo(prev, 0); }
                    // Memory frozen for a while = game paused (or alt-tabbed).
                    bool nowPaused = t - lastChange >= 0.4;
                    if (nowPaused != paused) Say(nowPaused ? "Game paused (car data frozen)." : "Resumed.");
                    paused = nowPaused;
                    // Not the player's car any more (new car, replay): look again soon.
                    if (!pinned && !_game.IsPlayer(block) && nextScan > t + 1) nextScan = t + 1;
                    if (!paused)
                    {
                        _game.Fill(block, sample);
                        haveSample = true;
                        outSample = sample;
                    }
                }
                else if (++readFailures < 30)
                {
                    // A failed read is usually transient; repeat the last sample.
                    if (haveSample && !paused) outSample = sample;
                    if (readFailures == 1) Say($"Read of car {selected:X} failed; retrying.");
                }
                else
                {
                    Say($"Car {selected:X} gone; rescanning.");
                    cars.Remove(selected);
                    selected = 0;
                    haveSample = false;
                    readFailures = 0;
                    nextScan = 0;
                }
            }

            var pkt = _opt.Format == PacketFormat.Horizon
                ? ForzaPacket.EncodeHorizon(outSample, (uint)clock.ElapsedMilliseconds)
                : ForzaPacket.EncodeDash(outSample, (uint)clock.ElapsedMilliseconds);
            try { udp.Send(pkt, pkt.Length); sent++; } catch (SocketException) { }

            lock (_lock)
            {
                _status.State = selected == 0 ? EngineState.NoCar : paused ? EngineState.Paused : EngineState.Sending;
                _status.Detail = selected == 0 ? "Looking for your car (get into one in free roam)."
                    : paused ? "Game paused; sending idle packets." : "";
                _status.Cars = cars.Count;
                _status.Car = selected;
                _status.Pinned = pinned;
                _status.Sent = sent;
                outSample.CopyTo(_status.Sample);
            }

            nextTick += period;
            var wait = nextTick - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            else nextTick = clock.Elapsed;
        }
    }

    // The player's car carries the player flag; failing that, take a car
    // that isn't AI-controlled, then any car.
    ulong PickCar(GameProcess g, List<ulong> cars)
    {
        var b = new byte[_game.BlockSize];
        ulong notAi = 0, any = 0;
        foreach (var c in cars)
        {
            if (!g.Read(c, b) || !_game.IsCar(b)) continue;
            if (_game.IsPlayer(b)) return c;
            if (notAi == 0 && !_game.IsAi(b)) notAi = c;
            if (any == 0) any = c;
        }
        return notAi != 0 ? notAi : any;
    }
}

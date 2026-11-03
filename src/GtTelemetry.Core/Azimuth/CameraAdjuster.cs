using System.Numerics;
using GtTelemetry.Core.Azimuth.Games;

namespace GtTelemetry.Core.Azimuth;

public enum CameraAction { FovUp, FovDown, SeatForward, SeatBack, SeatUp, SeatDown, SeatLeft, SeatRight, Reset }

// The player's change to one camera view, on top of the game's own values:
// FOV in degrees, seat in metres (x right, y up, z forward).
public sealed record CameraAdjust(float FovDeg = 0, float X = 0, float Y = 0, float Z = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsZero => FovDeg == 0 && X == 0 && Y == 0 && Z == 0;
}

public sealed class CameraStatus
{
    public string Text = "";
    public GameDefinition? Game;
    public CameraView? View;     // the view in use
    public float? GameFovDeg;    // the game's own FOV for it
    public CameraAdjust Adjust = new();
}

// Changes the field of view (every driving view) and the seat position
// (in-car views) while the game runs. The game sets both when a view starts
// (new car, camera switch) and reads them every frame, so this writes the
// player's change on top of the game's values and writes it again whenever
// the game resets them. All views exist at once; the one in use is the one
// whose state keeps changing (docs/FINDINGS.md). It writes nothing until
// there is a change, and only those fields of camera objects found by
// vtable, checked again before each write.
public sealed class CameraAdjuster : IDisposable
{
    public const float FovStep = 1f, SeatStep = 0.01f;
    const float MinFov = 10, MaxFov = 150, MaxSeat = 1f;
    const int StateBytes = 0x300;  // compared between ticks to see which view runs

    readonly Func<string, CameraAdjust> _load;
    readonly CancellationTokenSource _cts = new();
    readonly Thread _thread;
    readonly object _lock = new();
    readonly CameraStatus _status = new();
    readonly Dictionary<string, CameraAdjust> _adjust = new();  // by view id
    GameDefinition? _game;
    CameraView? _active;

    // Raised on the adjuster's thread when an adjustment changes, to save it.
    // The key is "<game id>/<view id>".
    public event Action<string, CameraAdjust>? Changed;

    // load: the saved adjustment for a "<game id>/<view id>" key.
    public CameraAdjuster(Func<string, CameraAdjust> load)
    {
        _load = load;
        _thread = new Thread(Run) { IsBackground = true, Name = "Camera", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(3000);
        _cts.Dispose();
    }

    public void Snapshot(CameraStatus into)
    {
        lock (_lock)
        {
            into.Text = _status.Text;
            into.Game = _status.Game;
            into.View = _status.View;
            into.GameFovDeg = _status.GameFovDeg;
            into.Adjust = _active != null ? Adjust(_active) : new CameraAdjust();
        }
    }

    CameraAdjust Adjust(CameraView v) => _adjust.TryGetValue(v.Id, out var a) ? a : _adjust[v.Id] = _load($"{_game!.Id}/{v.Id}");

    // From the UI thread (a hotkey): changes the view in use. Ignored until
    // the game runs and a view has been seen.
    public void Do(CameraAction action)
    {
        CameraAdjust next;
        string key;
        lock (_lock)
        {
            if (_game == null || _active is not { } view) return;
            var a = Adjust(view);
            bool seat = view.Seat != null;
            next = action switch
            {
                CameraAction.FovUp => a with { FovDeg = a.FovDeg + FovStep },
                CameraAction.FovDown => a with { FovDeg = a.FovDeg - FovStep },
                CameraAction.SeatForward when seat => a with { Z = Clamp(a.Z + SeatStep) },
                CameraAction.SeatBack when seat => a with { Z = Clamp(a.Z - SeatStep) },
                CameraAction.SeatUp when seat => a with { Y = Clamp(a.Y + SeatStep) },
                CameraAction.SeatDown when seat => a with { Y = Clamp(a.Y - SeatStep) },
                CameraAction.SeatRight when seat => a with { X = Clamp(a.X + SeatStep) },
                CameraAction.SeatLeft when seat => a with { X = Clamp(a.X - SeatStep) },
                CameraAction.Reset => new CameraAdjust(),
                _ => a,
            };
            // Keep the FOV inside sensible limits of the game's own.
            if (_status.GameFovDeg is float f) next = next with { FovDeg = Math.Clamp(next.FovDeg, MinFov - f, MaxFov - f) };
            next = Round(next);
            if (next == a) return;
            _adjust[view.Id] = next;
            key = $"{_game.Id}/{view.Id}";
        }
        Changed?.Invoke(key, next);
    }

    static float Clamp(float v) => Math.Clamp(v, -MaxSeat, MaxSeat);

    // Whole degrees and centimetres, so repeated steps don't drift.
    static CameraAdjust Round(CameraAdjust a) =>
        new(MathF.Round(a.FovDeg), MathF.Round(a.X * 100) / 100, MathF.Round(a.Y * 100) / 100, MathF.Round(a.Z * 100) / 100);

    // One camera object: what the game had and what was last written.
    sealed class Cam
    {
        public required ulong Address;
        public required CameraView View;
        public byte[]? State;
        public ulong FovAt;          // where its FOV lives (in the object, or its view data)
        public float BaseFov, WroteFov;
        public Vector3 BaseSeat, WroteSeat;
        public bool Written;
    }

    void Run()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SetStatus("Waiting for the game.", null, null, null);
                GameProcess? g;
                while ((g = GameProcess.Attach(write: true)) == null)
                    if (ct.WaitHandle.WaitOne(2000)) return;
                using (g) Session(g, ct);
                lock (_lock) { _game = null; _active = null; _adjust.Clear(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { SetStatus("Camera adjuster stopped: " + e.Message, null, null, null); }
    }

    void SetStatus(string text, GameDefinition? game, CameraView? view, float? fov)
    {
        lock (_lock) { _status.Text = text; _status.Game = game; _status.View = view; _status.GameFovDeg = fov; }
    }

    void Session(GameProcess g, CancellationToken ct)
    {
        GameDefinition? game = null;
        while (!g.HasExited && (game = GameCatalog.Detect(g, out string why, out _)) == null)
        {
            SetStatus(why, null, null, null);
            if (ct.WaitHandle.WaitOne(2000)) return;
        }
        if (game == null) return;
        if (game.CameraViews.Count == 0) { SetStatus($"{game.Name}: cameras not mapped.", game, null, null); return; }
        lock (_lock) _game = game;

        var byVtable = new Dictionary<ulong, CameraView>();
        foreach (var v in game.CameraViews) foreach (var vt in v.Vtables) byVtable[vt] = v;
        var cams = new List<Cam>();
        double nextScan = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var state = new byte[StateBytes];

        while (!ct.IsCancellationRequested && !g.HasExited)
        {
            double t = clock.Elapsed.TotalSeconds;
            if (t >= nextScan)
            {
                var found = CarScanner.FindObjects(g, byVtable.Keys, ct);
                var keep = cams.Where(c => found.Any(f => f.Address == c.Address)).ToList();
                foreach (var (addr, vt) in found)
                    if (!keep.Any(c => c.Address == addr)) keep.Add(new Cam { Address = addr, View = byVtable[vt] });
                cams = keep;
                nextScan = t + (cams.Count == 0 ? 15 : 60);
            }

            // Which view is in use: the highest-priority one whose state
            // changed since the last tick (apart from what this wrote).
            CameraView? running = null;
            foreach (var c in cams)
            {
                if (!g.Read(c.Address, state) || BitConverter.ToUInt64(state) is var vt && !c.View.Vtables.Contains(vt)) { nextScan = Math.Min(nextScan, t + 5); continue; }
                MaskOwnFields(c, state);
                bool changed = c.State != null && !state.AsSpan().SequenceEqual(c.State);
                c.State = (byte[])state.Clone();
                if (changed && (running == null || c.View.Priority < running.Priority)) running = c.View;
            }
            CameraView? active;
            lock (_lock) { if (running != null) _active = running; active = _active; }

            float? shownFov = null;
            foreach (var c in cams)
            {
                CameraAdjust adjust;
                lock (_lock) adjust = Adjust(c.View);
                float? fov = Apply(g, c, adjust);
                if (active is { } a && c.View.Id == a.Id && fov != null) shownFov ??= fov;
            }

            SetStatus(active == null ? "Drive for a moment so the view in use can be seen." : "", game, active, shownFov);
            if (ct.WaitHandle.WaitOne(100)) return;
        }
    }

    // Our own FOV and seat values don't count as the view running.
    static void MaskOwnFields(Cam c, byte[] state)
    {
        if (c.View.Fov is int f && f + 4 <= state.Length) state.AsSpan(f, 4).Clear();
        if (c.View.Seat is int s && s + 12 <= state.Length) state.AsSpan(s, 12).Clear();
    }

    // Puts the player's change on top of the game's values for one camera;
    // returns the game's own FOV in degrees, or null if the view isn't set up.
    static float? Apply(GameProcess g, Cam c, CameraAdjust adjust)
    {
        var v = c.View;
        // Where the FOV is: in the object (radians) or its view data (degrees).
        ulong fovAt;
        if (v.Fov is int f) fovAt = c.Address + (ulong)f;
        else if (v.FovData is int p && g.TryU64(c.Address + (ulong)p, out ulong data) && data > 0x10000) fovAt = data + (ulong)v.FovDataField;
        else return null;
        if (!g.TryF32(fovAt, out float fov) || !(fov > 0) || float.IsNaN(fov)) return null;
        float toDeg = v.Fov != null ? 180f / MathF.PI : 1f;

        var pos = Vector3.Zero;
        Span<byte> seat = stackalloc byte[12];
        if (v.Seat is int s)
        {
            if (!g.Read(c.Address + (ulong)s, seat)) return null;
            pos = new Vector3(BitConverter.ToSingle(seat), BitConverter.ToSingle(seat[4..]), BitConverter.ToSingle(seat[8..]));
        }

        // The game's values are the base: until this writes, and again
        // whenever the game sets the view up afresh (values not ours).
        if (!c.Written || fovAt != c.FovAt || fov != c.WroteFov || pos != c.WroteSeat)
        {
            c.BaseFov = fov;
            c.BaseSeat = pos;
            c.FovAt = fovAt;
            c.Written = false;
        }
        float baseDeg = c.BaseFov * toDeg;
        if (adjust.IsZero && !c.Written) return baseDeg;  // nothing to change: leave the game alone

        float wantFov = (baseDeg + adjust.FovDeg) / toDeg;
        var wantSeat = v.Seat != null ? c.BaseSeat + new Vector3(adjust.X, adjust.Y, adjust.Z) : pos;
        if (c.Written && wantFov == c.WroteFov && wantSeat == c.WroteSeat) return baseDeg;

        if (!g.TryU64(c.Address, out ulong vt) || !v.Vtables.Contains(vt)) return null;
        g.WriteF32(fovAt, wantFov);
        if (v.Seat is int at)
        {
            g.WriteF32(c.Address + (ulong)at, wantSeat.X);
            g.WriteF32(c.Address + (ulong)at + 4, wantSeat.Y);
            g.WriteF32(c.Address + (ulong)at + 8, wantSeat.Z);
        }
        c.WroteFov = wantFov;
        c.WroteSeat = wantSeat;
        c.Written = true;
        return baseDeg;
    }
}

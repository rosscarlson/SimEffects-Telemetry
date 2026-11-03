using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Threading;
using GtTelemetry.Core.Adapters;
using GtTelemetry.Core.Camera;
using GtTelemetry.Core.Memory;

namespace GtTelemetry.App.Camera;

/// <summary>What the card and the overlay show.</summary>
public sealed record CameraSnapshot(string Heading, string Detail, bool Adjustable);

/// <summary>
/// Runs the camera on a background loop: follows the current view, puts each car and view's saved seat and FOV back
/// when it appears, and applies hotkey actions. Profiles are changed and saved on the UI thread.
/// </summary>
public sealed class CameraController : IDisposable
{
    private const float Step = 0.01f, BigStep = 0.05f, MaxOffset = 0.6f;
    // Steps of the multiplier's immediate: about 0.8–1.6% each, and five times that with Shift.
    private const int FovStep = 2, FovBigStep = 10;
    private static readonly ushort FovMin = Gt4SpecIICamera.FromMultiplier(0.5f), FovMax = Gt4SpecIICamera.FromMultiplier(2f);

    private readonly AppState state;
    private readonly Dispatcher ui;
    private readonly ConcurrentQueue<(CameraAction Action, bool Big)> actions = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;
    private Pcsx2Connection? connection;
    private Gt4SpecIICamera? camera;
    private CameraView? lastView;
    private bool fovChanged;   // the FOV is something other than the game's own because of us

    /// <summary>Raised on the UI thread after every poll. The flag asks for the overlay (a hotkey was used, or a saved
    /// profile was just put back).</summary>
    public event Action<CameraSnapshot, bool>? Changed;

    public CameraController(AppState state, Dispatcher ui)
    {
        this.state = state;
        this.ui = ui;
        loop = Task.Run(() => RunAsync(cts.Token));
    }

    public void Enqueue(CameraAction action, bool big) => actions.Enqueue((action, big));

    private bool Enabled => state.Settings.CameraEnabled && state.Adapter.SupportOf(Feature.CameraControl) == Support.Available;

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(100, token); }
            catch (OperationCanceledException) { return; }

            bool flash = false;
            CameraSnapshot snap;
            var c = state.Connection;
            if (!Enabled || !c.Pine.IsConnected)
            {
                actions.Clear();
                snap = new CameraSnapshot("Camera", !Enabled ? "Off." : "Waiting for PCSX2…", false);
            }
            else
            {
                if (!ReferenceEquals(c, connection))
                {
                    connection = c;
                    camera = new Gt4SpecIICamera(c, c);
                    camera.ViewFound += RestoreSeat;
                    lastView = null;
                    fovChanged = false;
                }
                try
                {
                    var view = camera!.Poll().View;
                    if (view != lastView)
                    {
                        flash = view != null && Profile(view) != null;
                        lastView = view;
                        ApplyFov(view);
                    }
                    while (actions.TryDequeue(out var a))
                    {
                        Do(a.Action, a.Big, view);
                        flash = true;
                    }
                    snap = Describe(camera.Poll());
                }
                catch (MemorySourceException)
                {
                    snap = new CameraSnapshot("Camera", "Lost the connection to PCSX2.", false);
                }
            }
            var s = snap;
            bool f = flash;
            _ = ui.BeginInvoke(() => Changed?.Invoke(s, f));
        }
    }

    private CameraSnapshot Describe(CameraState st)
    {
        // The degrees come from what was last rendered, so they catch up when a paused game resumes.
        string fov = (st.VerticalFovDegrees is float deg ? $"FOV {deg:0.0}° vertical" : "FOV")
                     + (st.FovMultiplier is float m && m != 1 ? string.Create(CultureInfo.InvariantCulture, $", {(1 / m - 1) * 100:+0.0;−0.0}% from the game's") : "");
        if (st.View is not { } v) return new CameraSnapshot("Camera", "Switch to the cockpit or hood view to adjust it.", false);
        var o = v.Offset;
        return new CameraSnapshot($"{v.Name} view",
            $"Up {Cm(o.Up)}   Right {Cm(o.Right)}   Forward {Cm(o.Forward)}\n{fov}", true);
    }

    private static string Cm(float metres) => string.Create(CultureInfo.InvariantCulture, $"{metres * 100:+0;−0;0} cm");

    private void Do(CameraAction action, bool big, CameraView? view)
    {
        if (view == null || camera == null) return;
        float step = big ? BigStep : Step;
        var o = view.Offset;
        static float Clamp(float v) => Math.Clamp(MathF.Round(v * 1000) / 1000, -MaxOffset, MaxOffset);
        switch (action)
        {
            case CameraAction.SeatUp: o = o with { Up = Clamp(o.Up + step) }; break;
            case CameraAction.SeatDown: o = o with { Up = Clamp(o.Up - step) }; break;
            case CameraAction.SeatRight: o = o with { Right = Clamp(o.Right + step) }; break;
            case CameraAction.SeatLeft: o = o with { Right = Clamp(o.Right - step) }; break;
            case CameraAction.SeatForward: o = o with { Forward = Clamp(o.Forward + step) }; break;
            case CameraAction.SeatBack: o = o with { Forward = Clamp(o.Forward - step) }; break;
            case CameraAction.FovWider:
            case CameraAction.FovNarrower:
                if (camera.ReadFov() is not ushort imm) return;
                int n = imm + (action == CameraAction.FovWider ? -1 : 1) * (big ? FovBigStep : FovStep);
                var fov = (ushort)Math.Clamp(n, FovMin, FovMax);
                camera.SetFov(fov);
                fovChanged = fov != Gt4SpecIICamera.FovDefault;
                Save(view, view.Offset, fov);
                return;
            case CameraAction.Reset:
                camera.SetSeat(view, SeatOffset.Zero);
                camera.SetFov(Gt4SpecIICamera.FovDefault);
                fovChanged = false;
                Save(view, SeatOffset.Zero, Gt4SpecIICamera.FovDefault);
                return;
        }
        camera.SetSeat(view, o);
        Save(view, o, camera.ReadFov());
    }

    /// <summary>A view seen for the first time this race: put its saved seat back.</summary>
    private void RestoreSeat(CameraView view)
    {
        if (Profile(view) is { } p && camera != null)
            camera.SetSeat(view, new SeatOffset(p.Right, p.Up, p.Forward));
    }

    /// <summary>The FOV applies to every view, so it's switched to the view's own when the view changes.</summary>
    private void ApplyFov(CameraView? view)
    {
        if (camera == null) return;
        ushort want = (view != null ? Profile(view)?.Fov : null) ?? Gt4SpecIICamera.FovDefault;
        if (want == Gt4SpecIICamera.FovDefault && !fovChanged) return;   // leave other FOV patches alone
        if (camera.ReadFov() != want) camera.SetFov(want);
        fovChanged = want != Gt4SpecIICamera.FovDefault;
    }

    private string KeyFor(CameraView view) => $"{state.Adapter.Id}|{view.Key}";

    private CameraProfile? Profile(CameraView view)
    {
        string key = KeyFor(view);
        return ui.Invoke(() => state.Settings.CameraProfiles.GetValueOrDefault(key));
    }

    private void Save(CameraView view, SeatOffset o, ushort? fov)
    {
        string key = KeyFor(view);
        bool isDefault = o.IsZero && (fov ?? Gt4SpecIICamera.FovDefault) == Gt4SpecIICamera.FovDefault;
        ui.Invoke(() =>
        {
            var profiles = state.Settings.CameraProfiles;
            if (isDefault) profiles.Remove(key);
            else profiles[key] = new CameraProfile
            {
                View = view.Name, Right = o.Right, Up = o.Up, Forward = o.Forward,
                Fov = fov == Gt4SpecIICamera.FovDefault ? null : fov, Saved = DateTime.Now,
            };
            state.Settings.Save();
        });
    }

    public void Dispose()
    {
        cts.Cancel();
        try { loop.Wait(500); } catch (AggregateException) { }
    }
}

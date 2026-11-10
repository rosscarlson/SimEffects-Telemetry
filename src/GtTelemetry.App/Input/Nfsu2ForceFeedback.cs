using GtTelemetry.Core.Nfsu2;

namespace GtTelemetry.App.Input;

/// <summary>
/// NFSU2's force feedback on the wheel: reads the game's effects through the extension and plays them with
/// <see cref="FfbPlayer"/>, on its own thread, while NFSU2 is the game, the page is connected and it's turned on.
/// Everything stops when the page goes, the game pauses, it's turned off, or the app closes.
/// </summary>
public sealed class Nfsu2ForceFeedback : IDisposable
{
    private readonly AppState state;
    private readonly FfbPlayer player;
    private readonly Thread thread;
    private volatile bool stopping;

    public Nfsu2ForceFeedback(AppState state, IntPtr window)
    {
        this.state = state;
        player = new FfbPlayer(window);
        thread = new Thread(Run) { IsBackground = true, Name = "NFSU2 force feedback", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>The game's effects as last played; empty when not playing.</summary>
    public IReadOnlyList<FfbShown> Effects { get; private set; } = [];

    /// <summary>One line on what the wheel is doing.</summary>
    public string Status { get; private set; } = "Off";

    public bool HasError { get; private set; }

    private void Run()
    {
        var effects = new Nfsu2Effects(state.Browser);
        try
        {
            while (!stopping)
            {
                var s = state.Settings;
                if (!state.UsesBrowser || !s.FfbEnabled || !state.Browser.IsConnected)
                {
                    if (player.IsOpen || player.Error != null) player.Close();
                    Effects = [];
                    (Status, HasError) = (!s.FfbEnabled ? "Off" : "Waiting for nfsu2.xyz", false);
                    Thread.Sleep(200);
                    continue;
                }

                var ffb = effects.Read();
                Guid? wheel = Guid.TryParse(s.FfbWheel, out var g) ? g : null;
                var gains = new FfbGains(s.FfbStrength / 100.0, s.FfbConstant / 100.0, s.FfbVibration / 100.0, s.FfbSpring / 100.0, s.FfbDamper / 100.0);
                Effects = player.Update(ffb, wheel, gains);
                if (!ffb.Running) player.StopAll();

                (Status, HasError) = player.Error != null ? (player.Error, true)
                    : !player.IsOpen ? ("Opening the wheel…", false)
                    : effects.Table == 0 ? ($"{player.WheelName}: waiting for the game's effects (turn force feedback on in the game's controller options)", false)
                    : !ffb.Running ? ($"{player.WheelName}: the game is paused", false)
                    : ($"{player.WheelName}: playing", false);
                Thread.Sleep(8);
            }
        }
        finally
        {
            player.Dispose();
        }
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join(1000);
    }
}

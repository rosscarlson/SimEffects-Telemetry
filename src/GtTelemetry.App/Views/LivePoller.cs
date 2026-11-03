using System.Windows.Threading;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;

namespace GtTelemetry.App.Views;

/// <summary>Something with an address whose current value is shown live.</summary>
public interface ILiveRow
{
    uint Address { get; }

    ValueKind Kind { get; }

    void SetLive(double value);
}

/// <summary>
/// Polls a set of rows on a timer. Reads run on the thread pool (one batched PINE request, or direct process reads);
/// results are applied on the UI thread. Skips a tick if the previous read is still in flight.
/// </summary>
public sealed class LivePoller
{
    private readonly DispatcherTimer timer;
    private readonly Func<AppState?> state;
    private readonly Func<IReadOnlyList<ILiveRow>> rows;
    private bool busy;

    public LivePoller(Func<AppState?> state, Func<IReadOnlyList<ILiveRow>> rows, double hz)
    {
        this.state = state;
        this.rows = rows;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / hz) };
        timer.Tick += async (_, _) => await TickAsync();
    }

    public bool Running
    {
        get => timer.IsEnabled;
        set => timer.IsEnabled = value;
    }

    private async Task TickAsync()
    {
        var s = state();
        if (busy || s == null || !s.IsConnected) return;
        var list = rows();
        if (list.Count == 0) return;
        busy = true;
        try
        {
            var items = list.Select(r => (r.Address, r.Kind)).ToList();
            var conn = s.Connection;
            var values = await Task.Run(() => conn.ReadValues(items));
            for (int i = 0; i < list.Count; i++) list[i].SetLive(values[i]);
        }
        catch (MemorySourceException)
        {
            // Connection dropped; AppState's reconnect loop reports it.
        }
        finally
        {
            busy = false;
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GtTelemetry.Core.Telemetry;

public sealed record StreamOptions(string Host = "127.0.0.1", int Port = 8000, ForzaFormat Format = ForzaFormat.Horizon, double Hz = 60);

public sealed record StreamStats(long Sent, long Errors, double ActualHz, TelemetryFrame Last, string? LastError);

/// <summary>Reads a frame from the source at a fixed rate, encodes it and sends it over UDP until cancelled.</summary>
public sealed class TelemetryStreamer(ITelemetrySource source, StreamOptions options)
{
    /// <summary>Raised about once a second from the streaming thread.</summary>
    public event Action<StreamStats>? Stats;

    /// <summary>The frame most recently sent, for live displays.</summary>
    public TelemetryFrame Latest { get; private set; } = TelemetryFrame.NotDriving;

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient();
        var target = new IPEndPoint(Dns.GetHostAddresses(options.Host).First(a => a.AddressFamily == AddressFamily.InterNetwork), options.Port);
        var clock = Stopwatch.StartNew();
        double interval = 1000.0 / options.Hz;
        double next = 0;
        long sent = 0, errors = 0, sentAtReport = 0;
        double lastReport = 0;
        var last = TelemetryFrame.NotDriving;
        string? lastError = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                last = source.Read();
            }
            catch (Exception ex) when (ex is Memory.MemorySourceException or IOException or SocketException)
            {
                // Emulator gone or between states: tell listeners we're not driving and keep trying.
                last = TelemetryFrame.NotDriving;
                lastError = ex.Message;
                errors++;
            }
            Latest = last;
            var packet = ForzaDataOut.Encode(last, (uint)clock.ElapsedMilliseconds, options.Format);
            await udp.SendAsync(packet, target, ct);
            sent++;

            double now = clock.Elapsed.TotalMilliseconds;
            if (now - lastReport >= 1000)
            {
                Stats?.Invoke(new StreamStats(sent, errors, (sent - sentAtReport) * 1000 / (now - lastReport), last, lastError));
                sentAtReport = sent;
                lastReport = now;
            }

            next += interval;
            double wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            else if (wait < -interval * 5) next = clock.Elapsed.TotalMilliseconds;   // fell behind; don't burst to catch up
        }
    }
}

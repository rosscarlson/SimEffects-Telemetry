using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GtTelemetry.Core.Browser;

/// <summary>A controller as the game's page sees it (the Gamepad API).</summary>
public sealed record BrowserPad(int Index, string Id, string Mapping, int Axes, int Buttons, bool Spoofed);

/// <summary>What the extension last said about the game's page.</summary>
public sealed record BrowserPageState(
    int Protocol, string ExtensionVersion, string Page, long MemoryBytes, bool Visible, int RumbleCalls, IReadOnlyList<BrowserPad> Pads)
{
    public static readonly BrowserPageState None = new(0, "", "", 0, false, 0, []);
}

/// <summary>
/// The app's end of the browser bridge: a WebSocket server on 127.0.0.1 that the Chrome extension
/// (extension/nfsu2) connects to from its service worker. The extension holds the game's WebAssembly memory and
/// answers read requests; the protocol is in docs/nfsu2/bridge.md. One page at a time: a new connection replaces the
/// old one. Only chrome-extension:// origins are accepted, so a web page can't connect.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    public const int DefaultPort = 28020;
    public const int Protocol = 1;
    public const string PathName = "/nfsu2";

    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(10);

    private readonly int port;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[][]>> pending = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    private WebSocket? socket;
    private int nextId;
    private volatile BrowserPageState state = BrowserPageState.None;
    private volatile bool helloSeen;

    public BrowserBridge(int port = DefaultPort) => this.port = port;

    public int Port => port;

    public bool IsListening => listener != null;

    /// <summary>The extension is connected and has said hello.</summary>
    public bool IsPageConnected => socket is { State: WebSocketState.Open } && helloSeen;

    /// <summary>The page holds the game's memory (the game has started loading).</summary>
    public bool HasMemory => IsPageConnected && state.MemoryBytes > 0;

    public BrowserPageState State => state;

    /// <summary>Why listening failed, if it did.</summary>
    public string? ListenError { get; private set; }

    /// <summary>The page connected, said hello, changed state or went away.</summary>
    public event Action? StateChanged;

    /// <summary>The game's rumble levels as the page would have played them (0..1 strong, weak): a sign the game's
    /// force feedback is on.</summary>
    public event Action<double, double>? Rumble;

    public event Action<string>? Log;

    public void Start()
    {
        lock (gate)
        {
            if (listener != null) return;
            var l = new TcpListener(IPAddress.Loopback, port);
            try { l.Start(); }
            catch (SocketException ex)
            {
                ListenError = $"Port {port} on 127.0.0.1 is in use ({ex.SocketErrorCode}). Close whatever else is using it.";
                throw new BrowserBridgeException(ListenError, ex);
            }
            ListenError = null;
            listener = l;
            cts = new CancellationTokenSource();
            _ = AcceptLoop(l, cts.Token);
            _ = PingLoop(cts.Token);
        }
    }

    public void Stop()
    {
        TcpListener? l;
        WebSocket? s;
        lock (gate)
        {
            l = listener; listener = null;
            s = socket; socket = null;
            cts?.Cancel(); cts = null;
        }
        try { l?.Stop(); } catch (SocketException) { }
        s?.Abort();
        s?.Dispose();
        Dropped();
    }

    public void Dispose() => Stop();

    // ---- reads ----

    /// <summary>Reads several ranges in one round trip. Throws <see cref="BrowserBridgeException"/> when the page isn't
    /// there, has no memory yet, the range is outside it, or it doesn't answer in time.</summary>
    public async Task<byte[][]> ReadAsync(IReadOnlyList<(uint Address, int Length)> ranges, TimeSpan timeout)
    {
        var s = socket;
        if (s is not { State: WebSocketState.Open } || !helloSeen)
            throw new BrowserBridgeException("The NFSU2 page isn't connected. Open nfsu2.xyz in Chrome with the SimEffects extension.");
        int id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<byte[][]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            var sb = new StringBuilder(32 + ranges.Count * 24);
            sb.Append("{\"t\":\"read\",\"id\":").Append(id).Append(",\"r\":[");
            for (int i = 0; i < ranges.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(ranges[i].Address.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(ranges[i].Length.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            sb.Append("]}");
            await SendAsync(s, sb.ToString()).ConfigureAwait(false);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (done != tcs.Task) throw new BrowserBridgeException("The NFSU2 page didn't answer a read in time.");
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public void Read(uint address, Span<byte> dest, TimeSpan timeout)
    {
        var r = ReadAsync([(address, dest.Length)], timeout).GetAwaiter().GetResult();
        r[0].AsSpan(0, Math.Min(r[0].Length, dest.Length)).CopyTo(dest);
    }

    // ---- connection ----

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            _ = Serve(client, ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        WebSocket? ws = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            if (!await Handshake(stream, ct).ConfigureAwait(false)) { client.Dispose(); return; }
            ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = PingEvery });
            WebSocket? old;
            lock (gate) { old = socket; socket = ws; }
            if (old != null) { Dropped(); old.Abort(); old.Dispose(); }
            Log?.Invoke("Extension connected");
            await ReceiveLoop(ws, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            bool current;
            lock (gate) { current = socket == ws && ws != null; if (current) socket = null; }
            if (current)
            {
                Log?.Invoke("Extension disconnected");
                Dropped();
            }
            ws?.Dispose();
            client.Dispose();
        }
    }

    private void Dropped()
    {
        helloSeen = false;
        state = BrowserPageState.None;
        foreach (var kv in pending) kv.Value.TrySetException(new BrowserBridgeException("The NFSU2 page disconnected."));
        StateChanged?.Invoke();
    }

    /// <summary>The HTTP upgrade: GET /nfsu2 from a chrome-extension:// origin.</summary>
    private async Task<bool> Handshake(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int n = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (n == buf.Length) return false;
            int r = await stream.ReadAsync(buf.AsMemory(n), timeout.Token).ConfigureAwait(false);
            if (r == 0) return false;
            n += r;
            if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var lines = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n");
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        string origin = headers.GetValueOrDefault("Origin") ?? "";
        bool ok = parts.Length >= 2 && parts[0] == "GET" && parts[1].Split('?')[0] == PathName
            && headers.GetValueOrDefault("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true
            && headers.ContainsKey("Sec-WebSocket-Key")
            && origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            return false;
        }
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        string reply = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), ct).ConfigureAwait(false);
        return true;
    }

    private async Task ReceiveLoop(WebSocket ws, CancellationToken ct)
    {
        var chunk = new byte[64 * 1024];
        var msg = new ArrayBufferWriter<byte>(64 * 1024);
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return;
            msg.Write(chunk.AsSpan(0, r.Count));
            if (!r.EndOfMessage) continue;
            if (r.MessageType == WebSocketMessageType.Text) Handle(msg.WrittenSpan);
            msg.ResetWrittenCount();
        }
    }

    private void Handle(ReadOnlySpan<byte> utf8)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8.ToArray()); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t)) return;
            switch (t.GetString())
            {
                case "r": OnRead(root); break;
                case "hello": OnHello(root); break;
                case "state": OnState(root); break;
                case "rumble":
                    Rumble?.Invoke(Num(root, "s"), Num(root, "w"));
                    break;
            }
        }
    }

    private void OnRead(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl) || !pending.TryGetValue(idEl.GetInt32(), out var tcs)) return;
        if (root.TryGetProperty("err", out var err))
        {
            tcs.TrySetException(new BrowserBridgeException("The NFSU2 page couldn't read: " + err.GetString()));
            return;
        }
        if (!root.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.Array)
        {
            tcs.TrySetException(new BrowserBridgeException("The NFSU2 page sent a read with no data."));
            return;
        }
        var result = new byte[d.GetArrayLength()][];
        int i = 0;
        foreach (var e in d.EnumerateArray()) result[i++] = e.GetBytesFromBase64();
        tcs.TrySetResult(result);
    }

    private void OnHello(JsonElement root)
    {
        int v = root.TryGetProperty("v", out var ve) ? ve.GetInt32() : 0;
        string ext = Str(root, "ext");
        if (v != Protocol) Log?.Invoke($"Extension {ext} speaks bridge protocol {v}; this app speaks {Protocol}");
        state = state with { Protocol = v, ExtensionVersion = ext, Page = Str(root, "page"), MemoryBytes = (long)Num(root, "mem") };
        helloSeen = true;
        Log?.Invoke($"Extension {ext} on {state.Page}");
        StateChanged?.Invoke();
    }

    private void OnState(JsonElement root)
    {
        var pads = new List<BrowserPad>();
        if (root.TryGetProperty("pads", out var pa) && pa.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pa.EnumerateArray())
                pads.Add(new BrowserPad((int)Num(p, "index"), Str(p, "id"), Str(p, "mapping"), (int)Num(p, "axes"), (int)Num(p, "buttons"),
                    p.TryGetProperty("spoofed", out var sp) && sp.ValueKind == JsonValueKind.True));
        }
        var s = state with
        {
            MemoryBytes = (long)Num(root, "mem"),
            Visible = root.TryGetProperty("visible", out var vis) && vis.ValueKind == JsonValueKind.True,
            RumbleCalls = (int)Num(root, "rumbleCalls"),
            Pads = pads,
        };
        bool changed = s.MemoryBytes != state.MemoryBytes || s.Visible != state.Visible || s.Pads.Count != state.Pads.Count;
        state = s;
        if (changed) StateChanged?.Invoke();
    }

    private async Task PingLoop(CancellationToken ct)
    {
        // app-level pings keep the extension's service worker awake (WebSocket control frames don't count)
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingEvery, ct).ConfigureAwait(false);
                var s = socket;
                if (s is { State: WebSocketState.Open })
                {
                    try { await SendAsync(s, "{\"t\":\"ping\",\"id\":0}").ConfigureAwait(false); }
                    catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendAsync(WebSocket s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await sendLock.WaitAsync().ConfigureAwait(false);
        try { await s.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
        finally { sendLock.Release(); }
    }

    private static double Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
}

public sealed class BrowserBridgeException(string message, Exception? inner = null) : Exception(message, inner);

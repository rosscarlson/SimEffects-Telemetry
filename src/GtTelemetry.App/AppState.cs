using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GtTelemetry.Core;
using GtTelemetry.Core.Adapters;
using GtTelemetry.Core.Addresses;
using GtTelemetry.Core.Azimuth;
using GtTelemetry.Core.Browser;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;
using GtTelemetry.Core.Speech;

namespace GtTelemetry.App;

public enum StatusKind { Hint, Live, Info, Error, Progress }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A live-updating watched address (watch list, and the "live" column of result lists).</summary>
public sealed class WatchItem : Observable, Views.ILiveRow
{
    private string name = "";
    private string valueText = "—";
    private double min = double.NaN, max = double.NaN, last = double.NaN;
    private bool changed;

    public WatchItem(uint address, ValueKind kind, string name)
    {
        Address = address;
        Kind = kind;
        this.name = name;
    }

    public uint Address { get; }

    public ValueKind Kind { get; }

    public string AddressText => ValueKinds.FormatAddress(Address);

    public string KindText => Kind.Label();

    public string Name { get => name; set => Set(ref name, value); }

    public string ValueText { get => valueText; private set => Set(ref valueText, value); }

    public string MinText => double.IsNaN(min) ? "—" : Kind.Format(min);

    public string MaxText => double.IsNaN(max) ? "—" : Kind.Format(max);

    /// <summary>True briefly after the value changes (drives the accent highlight).</summary>
    public bool Changed { get => changed; private set => Set(ref changed, value); }

    public double Value => last;

    public void Update(double v)
    {
        Changed = !double.IsNaN(last) && v != last;
        last = v;
        ValueText = Kind.Format(v);
        if (double.IsNaN(min) || v < min) { min = v; Raise(nameof(MinText)); }
        if (double.IsNaN(max) || v > max) { max = v; Raise(nameof(MaxText)); }
    }

    public void SetLive(double value) => Update(value);

    public void ResetRange()
    {
        min = max = double.NaN;
        Raise(nameof(MinText));
        Raise(nameof(MaxText));
    }
}

/// <summary>
/// Everything the views share: settings, the emulator connection (PCSX2, RPCS3 or the browser bridge, by adapter), the
/// voice, the watch list and the address map.
/// Lives on the UI thread; memory I/O is pushed to the thread pool by callers.
/// </summary>
public sealed class AppState : Observable
{
    private readonly DispatcherTimer reconnectTimer;
    private bool connecting;
    private string connectionSummary = "Not connected";
    private string connectionDetail = "";
    private string? connectionError;
    private string? rpcs3Error;
    private AzimuthStatus azimuth = AzimuthStatus.NotRunning;
    private GameInfo? game;
    private GameAdapter adapter;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

    public AppState(AppSettings settings)
    {
        Settings = settings;
        adapter = AdapterCatalog.ForId(settings.AdapterId) ?? AdapterCatalog.Gt4SpecII;
        Connection = new Pcsx2Connection(settings.PineSlot, settings.FastSnapshots);
        Voice = new SpeechAnnouncer(settings.Voice, settings.VoiceRate) { Enabled = settings.SpeakInstructions };
        foreach (var w in settings.Watch)
        {
            try { Watch.Add(new WatchItem(ValueKinds.ParseAddress(w.Address), ValueKinds.Parse(w.Type), w.Name)); }
            catch (FormatException) { }
        }
        LoadAddressBook();

        Browser = new BrowserMemorySource(Bridge);
        Bridge.StateChanged += () => dispatcher.BeginInvoke(BrowserChanged);
        StartBridge();

        reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        reconnectTimer.Tick += async (_, _) => await MaintainConnectionAsync();
        reconnectTimer.Start();
    }

    public AppSettings Settings { get; }

    public Pcsx2Connection Connection { get; private set; }

    /// <summary>RPCS3's memory, for PS3 adapters.</summary>
    public Rpcs3ProcessMemory Rpcs3 { get; } = new();

    /// <summary>The selected game runs in RPCS3 rather than PCSX2.</summary>
    public bool UsesRpcs3 => Adapter.Source == MemorySourceKind.Rpcs3Process;

    /// <summary>The selected game runs in Azimuth (Forza Horizon 2 and 3). Its telemetry engine attaches by itself;
    /// this only tracks whether azimuth.exe runs and which game.</summary>
    public bool UsesAzimuth => Adapter.Source == MemorySourceKind.Azimuth;

    /// <summary>The Chrome extension's connection (NFSU2 on nfsu2.xyz). Listens while the app runs, so the game is
    /// recognised when its page connects.</summary>
    public BrowserBridge Bridge { get; } = new();

    /// <summary>The browser game's memory through <see cref="Bridge"/>. Read-only.</summary>
    public BrowserMemorySource Browser { get; }

    /// <summary>The selected game runs in Chrome (NFSU2 on nfsu2.xyz).</summary>
    public bool UsesBrowser => Adapter.Source == MemorySourceKind.Browser;

    /// <summary>azimuth.exe and its game, as of the last connection check.</summary>
    public AzimuthStatus Azimuth { get => azimuth; private set => Set(ref azimuth, value); }

    public string EmulatorName => UsesAzimuth ? "Azimuth" : UsesRpcs3 ? "RPCS3" : UsesBrowser ? "nfsu2.xyz" : "PCSX2";

    public SpeechAnnouncer Voice { get; }

    public ObservableCollection<WatchItem> Watch { get; } = [];

    public AddressBook AddressBook { get; private set; } = new();

    public string AddressBookPath => ProjectPaths.AddressFile(Adapter.DataFolder ?? Adapter.Id);

    public GameAdapter Adapter
    {
        get => adapter;
        set
        {
            if (!Set(ref adapter, value)) return;
            Settings.AdapterId = value.Id;
            Settings.Save();
            LoadAddressBook();
            Game = null;
            UpdateConnectionText();
        }
    }

    /// <summary>The selected adapter can talk to a running game in this build.</summary>
    public bool AdapterUsable => Adapter.SupportOf(Feature.MemoryAccess) == Support.Available;

    public bool IsConnected => UsesAzimuth ? Azimuth.Game != null : UsesRpcs3 ? Rpcs3.IsConnected : UsesBrowser ? Browser.IsConnected : Connection.IsConnected;

    public bool Connecting { get => connecting; private set => Set(ref connecting, value); }

    public string ConnectionSummary { get => connectionSummary; private set => Set(ref connectionSummary, value); }

    public string ConnectionDetail { get => connectionDetail; private set => Set(ref connectionDetail, value); }

    public string? ConnectionError { get => connectionError; private set => Set(ref connectionError, value); }

    public GameInfo? Game { get => game; private set => Set(ref game, value); }

    /// <summary>PCSX2 is running but PINE can't be reached (it's turned off in PCSX2's settings).</summary>
    public bool PineMissing { get; private set; }

    // ---- status line (bottom of the main card) ----

    public event Action<string, StatusKind>? StatusChanged;

    public void Status(string text, StatusKind kind = StatusKind.Info) => StatusChanged?.Invoke(text, kind);

    // ---- connection ----

    public async Task ConnectAsync()
    {
        if (Connecting || !AdapterUsable) return;
        Connecting = true;
        ConnectionSummary = "Connecting…";
        try
        {
            if (UsesAzimuth)
            {
                Azimuth = await Task.Run(AzimuthStatus.Check);
                return;
            }
            if (UsesBrowser)
            {
                StartBridge();
                return;
            }
            if (UsesRpcs3)
            {
                await Task.Run(TryConnectRpcs3);
                RefreshRpcs3Game();
                return;
            }
            if (Connection.FastSnapshots != Settings.FastSnapshots || Connection.Pine.Slot != Settings.PineSlot)
            {
                Connection.Dispose();
                Connection = new Pcsx2Connection(Settings.PineSlot, Settings.FastSnapshots);
            }
            var c = Connection;
            await Task.Run(() =>
            {
                try { c.Connect(); } catch (MemorySourceException) { /* errors are on PineError/ProcessError */ }
            });
            await RefreshGameAsync();
        }
        finally
        {
            Connecting = false;
            UpdateConnectionText();
        }
    }

    public void Disconnect()
    {
        Settings.AutoConnect = false;
        Settings.Save();
        Connection.Disconnect();
        Rpcs3.Disconnect();
        if (UsesBrowser) Bridge.Stop();
        Game = null;
        UpdateConnectionText();
    }

    /// <summary>Auto-connect, retry the missing half (PINE or process), and keep game info fresh.</summary>
    private async Task MaintainConnectionAsync()
    {
        if (Connecting) return;
        var az = await Task.Run(AzimuthStatus.Check);
        Azimuth = az;
        if (SwitchEmulator(az) || !AdapterUsable) return;
        if (!Bridge.IsListening && (Settings.AutoConnect || !UsesBrowser)) StartBridge();
        if (UsesAzimuth || UsesBrowser)
        {
            UpdateConnectionText();
            return;
        }
        if (UsesRpcs3)
        {
            if (Rpcs3.IsConnected) Rpcs3.CheckAlive();
            if (Settings.AutoConnect && !Rpcs3.IsConnected) await Task.Run(TryConnectRpcs3);
            RefreshRpcs3Game();
            UpdateConnectionText();
            return;
        }
        bool wantPine = !Connection.Pine.IsConnected;
        bool wantProcess = Settings.FastSnapshots && !Connection.Process.IsConnected;
        if (Settings.AutoConnect && (wantPine || wantProcess))
        {
            var c = Connection;
            await Task.Run(() =>
            {
                try { if (wantPine) c.Pine.Connect(); } catch (MemorySourceException) { }
                try { if (wantProcess) c.Process.Connect(); } catch (MemorySourceException) { }
            });
        }
        await RefreshGameAsync();
        UpdateConnectionText();
    }

    private void TryConnectRpcs3()
    {
        try
        {
            Rpcs3.Connect();
            rpcs3Error = null;
        }
        catch (MemorySourceException ex) { rpcs3Error = ex.Message; }
    }

    private void StartBridge()
    {
        try { Bridge.Start(); }
        catch (BrowserBridgeException) { /* the reason is on Bridge.ListenError */ }
    }

    /// <summary>The extension connected, said something new, or went away: follow it to NFSU2 when the game's page holds
    /// its memory and the selected game isn't running.</summary>
    private void BrowserChanged()
    {
        if (Bridge.HasMemory && !UsesBrowser && !IsConnected && AdapterCatalog.ForGameId("nfsu2.xyz") is { } web)
        {
            Adapter = web;
            return;
        }
        if (UsesBrowser) UpdateConnectionText();
    }

    private void RefreshRpcs3Game()
    {
        var (title, id) = Rpcs3.IsConnected ? Rpcs3.Game() : (null, null);
        Game = id == null ? null : new GameInfo(title, id, null, null, "RPCS3", EmulatorStatus.Running);
    }

    /// <summary>Follows the emulator that's running a known game: FH2 or FH3 in Azimuth, GT5 or GT6 in RPCS3, GT4 in
    /// PCSX2, or NFSU2 in Chrome, when the selected game's emulator isn't running it. Returns true if the adapter
    /// changed.</summary>
    private bool SwitchEmulator(AzimuthStatus az)
    {
        if (az.Game is { } fh && AdapterCatalog.ForGameId(fh.Id) is { } forza && forza != Adapter
            && (UsesAzimuth || !IsConnected))
        {
            Adapter = forza;
            return true;
        }
        if (Bridge.HasMemory && !UsesBrowser && !IsConnected && AdapterCatalog.ForGameId("nfsu2.xyz") is { } web)
        {
            Adapter = web;
            return true;
        }
        if (UsesAzimuth || UsesBrowser)
        {
            if (UsesAzimuth ? az.Running : Bridge.IsPageConnected) return false;
            if (AdapterCatalog.ForGameId(Rpcs3ProcessMemory.RunningGameId()) is { } running && running.Source == MemorySourceKind.Rpcs3Process)
            {
                Adapter = running;
                return true;
            }
            if (System.Diagnostics.Process.GetProcesses().Any(p => p.ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase)))
            {
                Adapter = AdapterCatalog.Gt4SpecII;
                return true;
            }
            return false;
        }
        if (!UsesRpcs3 && !Connection.Pine.IsConnected
            && AdapterCatalog.ForGameId(Rpcs3ProcessMemory.RunningGameId()) is { } ps3 && ps3.Source == MemorySourceKind.Rpcs3Process)
        {
            Adapter = ps3;
            return true;
        }
        if (UsesRpcs3 && !Rpcs3.IsConnected && Rpcs3ProcessMemory.RunningGameId() == null
            && System.Diagnostics.Process.GetProcesses().Any(p => p.ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase)))
        {
            Adapter = AdapterCatalog.Gt4SpecII;
            return true;
        }
        return false;
    }

    private async Task RefreshGameAsync()
    {
        var c = Connection;
        Game = c.Pine.IsConnected ? await Task.Run(c.GetGameInfo) : null;
        if (Game?.Id is string id && AdapterCatalog.ForGameId(id) is { } match && match != Adapter)
            Adapter = match;
    }

    private void UpdateConnectionText()
    {
        if (UsesBrowser)
        {
            var b = Bridge;
            var page = b.State;
            bool mem = b.HasMemory;
            ConnectionSummary = mem ? "Connected" : b.IsPageConnected ? "Page open" : "Not connected";
            var browserLines = new List<string>
            {
                b.IsListening ? $"Extension bridge: listening on 127.0.0.1:{b.Port}" : "Extension bridge: off",
            };
            if (b.IsPageConnected)
            {
                browserLines.Add($"SimEffects extension {page.ExtensionVersion} on {(Uri.TryCreate(page.Page, UriKind.Absolute, out var u) ? u.Host : page.Page)}");
                browserLines.Add(mem ? $"Game memory: {page.MemoryBytes / (1024 * 1024)} MB" : "Game memory: not yet");
            }
            ConnectionDetail = string.Join("\n", browserLines);
            ConnectionError = !b.IsListening ? (Settings.AutoConnect ? b.ListenError : null)
                : !b.IsPageConnected ? "Open nfsu2.xyz in Chrome with the SimEffects Telemetry extension turned on."
                : page.Protocol != BrowserBridge.Protocol ? $"The SimEffects extension speaks a different version ({page.Protocol}) from this app ({BrowserBridge.Protocol}). Load the extension that came with this app."
                : !mem ? "The page is open but the game hasn't started. Press Play."
                : null;
            Raise(nameof(IsConnected));
            return;
        }
        if (UsesAzimuth)
        {
            var az = Azimuth;
            ConnectionSummary = az.Game != null ? "Connected" : az.Running ? "Azimuth running" : "Not connected";
            ConnectionDetail = !az.Running ? "azimuth.exe: not running"
                : $"azimuth.exe: found (PID {az.Pid})" + (az.Game is { } fh ? $"\n{fh.Name} · {fh.BuildDescription}" : "");
            ConnectionError = az.Game != null ? null : az.Running ? az.Why : "Start the game in Azimuth.";
            Raise(nameof(IsConnected));
            return;
        }
        if (UsesRpcs3)
        {
            bool on = Rpcs3.IsConnected;
            ConnectionSummary = on ? "Connected" : "Not connected";
            ConnectionDetail = on
                ? $"RPCS3 process memory: attached (PID {Rpcs3.ProcessId})" + (Game is { } ps3 ? $"\n{ps3.Title} · {ps3.Id}" : "")
                : "RPCS3 process memory: not attached";
            ConnectionError = on || !Settings.AutoConnect ? null : rpcs3Error ?? "Start RPCS3 and the game.";
            Raise(nameof(IsConnected));
            return;
        }
        var c = Connection;
        bool pine = c.Pine.IsConnected, proc = c.Process.IsConnected;
        PineMissing = !pine && System.Diagnostics.Process.GetProcesses().Any(p => p.ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase));
        ConnectionSummary = pine && proc ? "Connected" : pine ? "Connected (PINE only)" : proc ? "Connected (process memory only)" : "Not connected";

        var lines = new List<string>
        {
            pine ? $"PINE on slot {c.Pine.Slot}: connected" : $"PINE on slot {c.Pine.Slot}: not connected",
            Settings.FastSnapshots ? (proc ? $"Process memory: attached (PID {c.Process.ProcessId})" : "Process memory: not attached" + (c.ProcessError is { } pe ? $" — {pe}" : "")) : "Process memory: off",
        };
        if (Game is { } g)
        {
            lines.Add($"{g.Title} · {g.Id} · {g.Status}");
            if (g.EmulatorVersion != null) lines.Add(g.EmulatorVersion);
        }
        else if (proc)
        {
            lines.Add("Game details need PINE.");
        }
        ConnectionDetail = string.Join("\n", lines);

        // Errors explain the fix. Only show the PINE one when PINE is the missing piece.
        ConnectionError = !pine && Settings.AutoConnect
            ? (c.PineError ?? $"Couldn't reach PINE on slot {c.Pine.Slot}. In PCSX2, turn on Settings → Advanced → PINE Settings → Enable, and start the game.")
            : !pine ? null
            : Game?.Status == EmulatorStatus.Shutdown
                ? (System.Diagnostics.Process.GetProcessesByName("pcsx2-qt").Length > 1
                    ? $"This PCSX2 window has no game running, and another PCSX2 window does. Only the first PCSX2 opened gets PINE slot {c.Pine.Slot}. Close the extra PCSX2 window, then restart the game (or close both and start the game again)."
                    : "PCSX2 is open but no game is running. Start the game.")
            : Settings.FastSnapshots && !proc ? "Fast snapshots unavailable: " + (c.ProcessError ?? "PCSX2 process not found.") + " Scans will use PINE (slower)."
            : null;
        Raise(nameof(IsConnected));
    }

    // ---- watch list & address map ----

    public WatchItem AddWatch(uint address, ValueKind kind, string? name = null)
    {
        var existing = Watch.FirstOrDefault(w => w.Address == address && w.Kind == kind);
        if (existing != null) return existing;
        var item = new WatchItem(address, kind, name ?? NameFor(address, kind) ?? "");
        Watch.Add(item);
        SaveWatch();
        return item;
    }

    public void SaveWatch()
    {
        Settings.Watch = Watch.Select(w => new WatchSetting { Name = w.Name, Address = w.AddressText, Type = w.KindText }).ToList();
        Settings.Save();
    }

    public string? NameFor(uint address, ValueKind kind) =>
        AddressBook.Entries.FirstOrDefault(e => e.AddressValue == address && e.Kind == kind)?.Name;

    public void LoadAddressBook()
    {
        try { AddressBook = AddressBook.Load(AddressBookPath); }
        catch (Exception) { AddressBook = new AddressBook(); }
        AddressBook.Game = Adapter.Name;
        Raise(nameof(AddressBook));
    }

    public void SaveAddress(string name, uint address, ValueKind kind, AddressStatus status, string? source)
    {
        AddressBook.Upsert(name, address, kind, status, source: source);
        AddressBook.Save(AddressBookPath);
        Raise(nameof(AddressBook));
    }
}

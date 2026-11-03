using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;

namespace GtTelemetry.App.Views;

public sealed class ScanRow(uint address, ValueKind kind, double value, double previous) : Observable, ILiveRow
{
    private string liveText = "—";

    public uint Address { get; } = address;

    public ValueKind Kind { get; } = kind;

    public string AddressText => ValueKinds.FormatAddress(Address);

    public string ValueText { get; } = kind.Format(value);

    public string PreviousText { get; } = double.IsNaN(previous) ? "" : kind.Format(previous);

    public string LiveText { get => liveText; private set => Set(ref liveText, value); }

    public void SetLive(double value) => LiveText = Kind.Format(value);
}

public partial class ScannerView : UserControl, IAppView
{
    private const int MaxShown = 1000;
    private AppState? state;
    private ManualScanner? scanner;
    private readonly ObservableCollection<ScanRow> rows = [];
    private readonly LivePoller poller;
    private bool busy;

    public ScannerView()
    {
        InitializeComponent();
        KindBox.ItemsSource = ValueKinds.All.Select(k => k.Label()).ToList();
        KindBox.SelectedItem = "f32";
        ResultList.ItemsSource = rows;
        poller = new LivePoller(() => state, () => rows.Take(150).ToList<ILiveRow>(), hz: 8);
        UpdateCompareChoices();
    }

    public string Hint => "Enter runs the next scan (or the first). Double-click a result to watch it. Values: decimal, or hex with 0x.";

    public void Attach(AppState s)
    {
        state = s;
        scanner = new ManualScanner(s.Connection);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || busy) return;
            e.Handled = true;
            if (scanner?.HasScan == true) await RunScan(first: false);
            else await RunScan(first: true);
        };
        UpdateButtons();
    }

    public void SetActive(bool active) => poller.Running = active;

    public void ConnectionChanged()
    {
        // The connection object is replaced when slot/snapshot settings change; rebind the scanner to it.
        if (state != null && scanner != null && !scanner.HasScan) scanner = new ManualScanner(state.Connection);
        UpdateButtons();
    }

    private void UpdateCompareChoices()
    {
        bool hasScan = scanner?.HasScan == true;
        var ops = Enum.GetValues<ScanCompare>().Where(o => hasScan ? o != ScanCompare.Unknown : !o.NeedsPrevious()).ToList();
        var keep = CompareBox.SelectedValue as ScanCompare?;
        CompareBox.ItemsSource = ops.Select(o => new KeyValuePair<ScanCompare, string>(o, o.Describe())).ToList();
        CompareBox.DisplayMemberPath = "Value";
        CompareBox.SelectedValuePath = "Key";
        CompareBox.SelectedValue = keep is { } k && ops.Contains(k) ? k : hasScan ? ScanCompare.Changed : ScanCompare.Unknown;
    }

    private ScanCompare SelectedCompare => CompareBox.SelectedValue is ScanCompare c ? c : ScanCompare.Unknown;

    private void CompareBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = SelectedCompare.Operands();
        ValueAPanel.Visibility = n >= 1 ? Visibility.Visible : Visibility.Collapsed;
        ValueBPanel.Visibility = n >= 2 ? Visibility.Visible : Visibility.Collapsed;
        ValueALabel.Text = n == 2 ? "From" : "Value";
    }

    private void UpdateButtons()
    {
        bool connected = state?.IsConnected == true;
        bool hasScan = scanner?.HasScan == true;
        FirstButton.IsEnabled = connected && !busy;
        NextButton.IsEnabled = connected && hasScan && !busy;
        ResetButton.IsEnabled = hasScan && !busy;
        KindBox.IsEnabled = !hasScan;
        WatchButton.IsEnabled = ResultList.SelectedItems.Count > 0;
        PointerButton.IsEnabled = connected && !busy;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = hasScan ? $"{scanner!.Count:N0} candidates after {scanner.Scans} scan{(scanner.Scans == 1 ? "" : "s")}" +
                                   (scanner.Count > MaxShown ? $" · showing the first {MaxShown:N0}" : "")
                                 : "No scan yet";
    }

    private static bool TryNumber(string s, out double v)
    {
        s = s.Trim().Replace('−', '-');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h))
        {
            v = h;
            return true;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private async void First_Click(object sender, RoutedEventArgs e) => await RunScan(first: true);

    private async void Next_Click(object sender, RoutedEventArgs e) => await RunScan(first: false);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        scanner = state == null ? null : new ManualScanner(state.Connection);
        rows.Clear();
        UpdateCompareChoices();
        UpdateButtons();
        state?.Status("Scan cleared.", StatusKind.Info);
    }

    private async Task RunScan(bool first)
    {
        if (state == null || scanner == null) return;
        var op = SelectedCompare;
        double a = 0, b = 0, tol = 0;
        if (op.Operands() >= 1 && !TryNumber(ValueA.Text, out a)) { state.Status("Enter a number to compare with.", StatusKind.Error); ValueA.Focus(); return; }
        if (op.Operands() >= 2 && !TryNumber(ValueB.Text, out b)) { state.Status("Enter the upper end of the range.", StatusKind.Error); ValueB.Focus(); return; }
        if (!TryNumber(TolBox.Text, out tol) || tol < 0) { state.Status("Tolerance must be zero or a positive number.", StatusKind.Error); TolBox.Focus(); return; }
        if (first && op.NeedsPrevious()) op = ScanCompare.Unknown;

        if (first) scanner = new ManualScanner(state.Connection);
        var kind = ValueKinds.Parse((string)KindBox.SelectedItem);
        busy = true;
        UpdateButtons();
        state.Status(first ? "Scanning all 32 MB…" : "Narrowing…", StatusKind.Progress);
        try
        {
            var sc = scanner;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long count = await Task.Run(() => first ? sc.FirstScan(kind, op, a, b, tol) : sc.NextScan(op, a, b, tol));
            var list = await Task.Run(() => sc.Results(MaxShown));
            rows.Clear();
            foreach (var (addr, v, p) in list) rows.Add(new ScanRow(addr, kind, v, p));
            state.Status($"{op.Describe()}: {count:N0} candidates ({sw.ElapsedMilliseconds} ms via {state.Connection.SnapshotPath}).", StatusKind.Info);
        }
        catch (MemorySourceException ex)
        {
            state.Status(ex.Message, StatusKind.Error);
        }
        finally
        {
            busy = false;
            UpdateCompareChoices();
            UpdateButtons();
        }
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Watch_Click(sender, e);

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if (state == null) return;
        var sel = ResultList.SelectedItems.Cast<ScanRow>().ToList();
        foreach (var r in sel) state.AddWatch(r.Address, r.Kind);
        if (sel.Count > 0) state.Status($"Added {sel.Count} to the watch list.", StatusKind.Info);
    }

    private async void Pointers_Click(object sender, RoutedEventArgs e)
    {
        if (state == null) return;
        uint target, maxOff;
        try
        {
            target = ValueKinds.ParseAddress(PointerTarget.Text);
            maxOff = ValueKinds.ParseAddress(MaxOffsetBox.Text);
        }
        catch (FormatException)
        {
            state.Status("Enter the target address and maximum offset in hex, e.g. 0x0099C3C8 and 0x1000.", StatusKind.Error);
            return;
        }
        busy = true;
        UpdateButtons();
        try
        {
            var conn = state.Connection;
            var hits = await Task.Run(() =>
            {
                var ram = new byte[Pcsx2Connection.RamSize];
                conn.ReadBulk(0, ram);
                return ManualScanner.FindPointers(ram, 0, target, maxOff, 200);
            });
            PointerResults.Text = hits.Count == 0
                ? "No pointers found. Try a larger offset; the value may live in a statically allocated struct."
                : string.Join("   ", hits.Select(h => $"{ValueKinds.FormatAddress(h.Address)}→{h.Pointer:X8}+0x{h.Offset:X}"));
            state.Status($"{hits.Count} pointer{(hits.Count == 1 ? "" : "s")} to {ValueKinds.FormatAddress(target)} within 0x{maxOff:X}.", StatusKind.Info);
        }
        catch (MemorySourceException ex)
        {
            state.Status(ex.Message, StatusKind.Error);
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }
}

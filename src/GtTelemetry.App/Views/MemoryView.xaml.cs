using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;

namespace GtTelemetry.App.Views;

public sealed class MemCell : Observable
{
    private string text = "";
    private bool changed, zero;
    private DateTime changedAt;

    public string Text { get => text; private set => Set(ref text, value); }

    public bool Changed { get => changed; private set => Set(ref changed, value); }

    public bool Zero { get => zero; private set => Set(ref zero, value); }

    public void Update(string t, bool isZero, bool force)
    {
        if (!force && t != text) changedAt = DateTime.UtcNow;
        Text = t;
        Zero = isZero;
        // Hold the highlight for a second so brief changes are still visible.
        Changed = !force && (DateTime.UtcNow - changedAt).TotalSeconds < 1;
    }
}

public sealed class MemRow : Observable
{
    private string ascii = "";

    public string AddressText { get; set; } = "";

    public ObservableCollection<MemCell> Cells { get; } = [];

    public string Ascii { get => ascii; set => Set(ref ascii, value); }
}

public partial class MemoryView : UserControl, IAppView
{
    private const int RowsShown = 16, BytesPerRow = 16, PageSize = RowsShown * BytesPerRow;
    private AppState? state;
    private readonly ObservableCollection<MemRow> rows = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private uint address = 0x00100000;
    private bool busy, active, relayout = true;

    public MemoryView()
    {
        InitializeComponent();
        ModeBox.ItemsSource = new[] { "hex", "u8", "u16", "u32", "f32" };
        ModeBox.SelectedItem = "hex";
        Rows.ItemsSource = rows;
        timer.Tick += async (_, _) => { if (LiveChip.IsChecked == true) await RefreshAsync(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.PageDown) { Move(PageSize); e.Handled = true; }
            else if (e.Key == Key.PageUp) { Move(-PageSize); e.Handled = true; }
        };
    }

    public string Hint => "Page Up / Page Down move by 256 bytes. Changed values light up for a second. Faded values are zero.";

    public void Attach(AppState s) => state = s;

    public void SetActive(bool isActive)
    {
        active = isActive;
        timer.IsEnabled = isActive;
        if (isActive) _ = RefreshAsync();
    }

    public void ConnectionChanged()
    {
        if (active) _ = RefreshAsync();
    }

    private string Mode => ModeBox.SelectedItem as string ?? "hex";

    private void Go_Click(object sender, RoutedEventArgs e) => GoTo();

    private void AddrBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GoTo(); e.Handled = true; }
    }

    private void GoTo()
    {
        try
        {
            address = ValueKinds.ParseAddress(AddrBox.Text) & 0x1FFFFF0;
            relayout = true;
            _ = RefreshAsync();
        }
        catch (FormatException)
        {
            state?.Status("Enter a hex address, e.g. 0x0099C3C0.", StatusKind.Error);
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-PageSize);

    private void Next_Click(object sender, RoutedEventArgs e) => Move(PageSize);

    private void Move(int delta)
    {
        long a = Math.Clamp((long)address + delta, 0, Pcsx2Connection.RamSize - PageSize);
        address = (uint)a;
        AddrBox.Text = ValueKinds.FormatAddress(address);
        relayout = true;
        _ = RefreshAsync();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        relayout = true;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (busy || state == null || !state.IsConnected) return;
        busy = true;
        try
        {
            var conn = state.Connection;
            uint at = address;
            var buf = new byte[PageSize];
            await Task.Run(() => conn.Read(at, buf));
            if (at != address) return;
            Render(buf);
        }
        catch (MemorySourceException ex)
        {
            state.Status(ex.Message, StatusKind.Error);
        }
        finally
        {
            busy = false;
        }
    }

    private void Render(byte[] buf)
    {
        string mode = Mode;
        ValueKind? kind = mode == "hex" ? null : ValueKinds.Parse(mode);
        int cellSize = kind?.Size() ?? 1;
        int cellsPerRow = BytesPerRow / cellSize;
        bool force = relayout;
        if (relayout)
        {
            rows.Clear();
            for (int r = 0; r < RowsShown; r++)
            {
                var row = new MemRow { AddressText = ValueKinds.FormatAddress(address + (uint)(r * BytesPerRow)) };
                for (int c = 0; c < cellsPerRow; c++) row.Cells.Add(new MemCell());
                rows.Add(row);
            }
            relayout = false;
        }
        for (int r = 0; r < RowsShown; r++)
        {
            var row = rows[r];
            int rowOff = r * BytesPerRow;
            for (int c = 0; c < cellsPerRow; c++)
            {
                int off = rowOff + c * cellSize;
                var span = buf.AsSpan(off, cellSize);
                bool zero = !span.ContainsAnyExcept((byte)0);
                string text = kind is { } k ? k.Format(k.Read(span)) : buf[off].ToString("X2");
                row.Cells[c].Update(text, zero, force);
            }
            row.Ascii = new string(buf.AsSpan(rowOff, BytesPerRow).ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '·').ToArray());
        }
    }
}

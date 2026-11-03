using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GtTelemetry.Core.Addresses;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;

namespace GtTelemetry.App.Views;

public partial class WatchView : UserControl, IAppView
{
    private AppState? state;
    private readonly LivePoller poller;

    public WatchView()
    {
        InitializeComponent();
        KindBox.ItemsSource = ValueKinds.All.Select(k => k.Label()).ToList();
        KindBox.SelectedItem = "f32";
        StatusBox.ItemsSource = new[] { AddressStatus.Candidate, AddressStatus.Confirmed, AddressStatus.Rejected };
        StatusBox.SelectedIndex = 0;
        // 20 Hz: fast enough to see pedal and steering movement without hammering PINE.
        poller = new LivePoller(() => state, () => state?.Watch.ToList<ILiveRow>() ?? [], hz: 20);
    }

    public string Hint => "Values turn blue as they change. Delete removes the selection. Writes go through PINE — use with care.";

    public void Attach(AppState s)
    {
        state = s;
        List.ItemsSource = s.Watch;
        s.Watch.CollectionChanged += (_, _) => UpdateButtons();
        // Watched values keep updating in the background so min/max stay meaningful while other pages are open.
        poller.Running = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && List.IsKeyboardFocusWithin) { Remove_Click(this, e); e.Handled = true; }
        };
        UpdateButtons();
    }

    public void SetActive(bool active) { }

    public void ConnectionChanged() => UpdateButtons();

    private IReadOnlyList<WatchItem> Selected => List.SelectedItems.Cast<WatchItem>().ToList();

    private void UpdateButtons()
    {
        int n = List.SelectedItems.Count;
        RemoveButton.IsEnabled = ResetButton.IsEnabled = n > 0;
        RenameBox.IsEnabled = StatusBox.IsEnabled = SaveButton.IsEnabled = n == 1;
        WriteBox.IsEnabled = WriteButton.IsEnabled = n == 1 && state?.IsConnected == true;
        EmptyText.Visibility = state?.Watch.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is WatchItem w && List.SelectedItems.Count == 1)
        {
            RenameBox.Text = w.Name.TrimEnd('?');
            var entry = state?.AddressBook.Entries.FirstOrDefault(x => x.AddressValue == w.Address && x.Kind == w.Kind);
            StatusBox.SelectedItem = entry?.Status ?? AddressStatus.Candidate;
            WriteBox.Text = double.IsNaN(w.Value) ? "" : w.Value.ToString(CultureInfo.InvariantCulture);
        }
        UpdateButtons();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (state == null) return;
        try
        {
            uint addr = ValueKinds.ParseAddress(AddrBox.Text);
            // EE RAM or one of its mirrors (0x00/0x20/0x30/0x80/0xA0 xxxxxx).
            if ((addr >> 25) is not (0 or 0x10 or 0x18 or 0x40 or 0x50))
                throw new FormatException();
            var kind = ValueKinds.Parse((string)KindBox.SelectedItem);
            if (addr % (uint)kind.Size() != 0)
                state.Status($"Note: {ValueKinds.FormatAddress(addr)} isn't {kind.Size()}-byte aligned; the game probably doesn't store a {kind.Label()} there.", StatusKind.Info);
            var item = state.AddWatch(addr, kind, NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : null);
            List.SelectedItem = item;
            AddrBox.Clear();
            NameBox.Clear();
        }
        catch (FormatException)
        {
            state.Status("Enter the address in hex, e.g. 0x0099C3C8 (EE RAM is 0x00000000–0x01FFFFFF).", StatusKind.Error);
            AddrBox.Focus();
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (state == null) return;
        foreach (var w in Selected) state.Watch.Remove(w);
        state.SaveWatch();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var w in Selected) w.ResetRange();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (state == null || List.SelectedItem is not WatchItem w) return;
        string name = RenameBox.Text.Trim();
        if (name.Length == 0)
        {
            state.Status("Type a name first (e.g. throttle), then save.", StatusKind.Error);
            RenameBox.Focus();
            return;
        }
        var status = StatusBox.SelectedItem is AddressStatus s ? s : AddressStatus.Candidate;
        state.SaveAddress(name, w.Address, w.Kind, status, null);
        w.Name = name;
        state.SaveWatch();
        state.Status($"Saved {name} = {w.AddressText} ({w.KindText}) as {status.ToString().ToLowerInvariant()}.", StatusKind.Info);
    }

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (state == null || List.SelectedItem is not WatchItem w) return;
        if (!double.TryParse(WriteBox.Text.Trim().Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            state.Status("Enter a number to write.", StatusKind.Error);
            return;
        }
        try
        {
            var conn = state.Connection;
            var bytes = w.Kind.Encode(v);
            await Task.Run(() => conn.Write(w.Address, bytes));
            state.Status($"Wrote {w.Kind.Format(v)} to {w.AddressText}. The game may overwrite it on the next frame.", StatusKind.Info);
        }
        catch (MemorySourceException ex)
        {
            state.Status(ex.Message, StatusKind.Error);
        }
    }
}

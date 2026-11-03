using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GtTelemetry.Core;
using GtTelemetry.Core.Addresses;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Scanning;
using GtTelemetry.Core.Sessions;

namespace GtTelemetry.App.Views;

public enum StepState { Pending, Active, Sampling, Done }

public sealed class StepRow : Observable
{
    private StepState state;
    private string detail;

    public StepRow(int number, SessionStep step)
    {
        Number = number;
        Say = step.Say;
        detail = Describe(step);
        Base = detail;
    }

    public int Number { get; }

    public string Say { get; }

    private string Base { get; }

    public StepState State { get => state; set => Set(ref state, value); }

    public string Detail { get => detail; private set => Set(ref detail, value); }

    public void SetResult(string text) => Detail = $"{Base} · {text}";

    public void Reset()
    {
        State = StepState.Pending;
        Detail = Base;
    }

    private static string Describe(SessionStep s)
    {
        var parts = new List<string>();
        if (s.Label is double l) parts.Add($"label {ValueKind.F32.Format(l)}");
        parts.Add(s.Expect.ToString().ToLowerInvariant());
        parts.Add($"{s.Sample:0.#} s");
        return string.Join(" · ", parts);
    }
}

public sealed class CandidateRow : Observable, ILiveRow
{
    private string liveText = "—";

    public CandidateRow(Candidate c)
    {
        Candidate = c;
        ValuesText = string.Join("  |  ", c.StepValues.Select(v => c.Kind.Format(v)));
    }

    public Candidate Candidate { get; }

    public uint Address => Candidate.Address;

    public ValueKind Kind => Candidate.Kind;

    public string AddressText => Candidate.AddressText;

    public string KindText => Kind.Label();

    public string ScoreText => Candidate.Score.ToString("0.00");

    public string Notes => Candidate.Notes;

    public string ValuesText { get; }

    public string LiveText { get => liveText; private set => Set(ref liveText, value); }

    public void SetLive(double value) => LiveText = Kind.Format(value);
}

public partial class CaptureView : UserControl, IAppView
{
    private AppState? state;
    private readonly ObservableCollection<StepRow> steps = [];
    private readonly ObservableCollection<CandidateRow> results = [];
    private readonly LivePoller poller;
    private CancellationTokenSource? running;
    private bool loading;

    public CaptureView()
    {
        InitializeComponent();
        StepList.ItemsSource = steps;
        ResultList.ItemsSource = results;
        // Live values for the top of the list; scrolled-away rows don't need to update.
        poller = new LivePoller(() => state, () => results.Take(150).ToList<ILiveRow>(), hz: 8);
    }

    public string Hint => "Esc cancels a running session. Double-click a candidate to watch it. Session scripts live in the sessions folder; edit and press reload.";

    public void Attach(AppState s)
    {
        state = s;
        loading = true;
        SaveSnapshotsBox.IsChecked = s.Settings.SaveSnapshots;
        loading = false;
        LoadSessions();
        UpdateButtons();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && running != null) { running.Cancel(); e.Handled = true; }
        };
    }

    public void SetActive(bool active) => poller.Running = active;

    public void ConnectionChanged() => UpdateButtons();

    public void Shutdown() => running?.Cancel();

    private void LoadSessions()
    {
        var (scripts, errors) = SessionScript.LoadAll(ProjectPaths.Sessions);
        loading = true;
        var keep = (SessionBox.SelectedItem as SessionScript)?.Id ?? state?.Settings.LastSession;
        SessionBox.ItemsSource = scripts;
        SessionBox.SelectedItem = scripts.FirstOrDefault(x => x.Id == keep) ?? scripts.FirstOrDefault();
        loading = false;
        ShowSession();
        if (errors.Count > 0) state?.Status("Some session scripts couldn't be read: " + string.Join("; ", errors), StatusKind.Error);
        else if (scripts.Count == 0) state?.Status($"No session scripts found in {ProjectPaths.Sessions}.", StatusKind.Error);
    }

    private void ShowSession()
    {
        steps.Clear();
        if (SessionBox.SelectedItem is not SessionScript s)
        {
            DescriptionText.Text = SetupText.Text = DurationText.Text = "";
            return;
        }
        DescriptionText.Text = s.Description;
        SetupText.Text = string.IsNullOrEmpty(s.Setup) ? "" : "Before you start: " + s.Setup;
        DurationText.Text = $"about {Math.Ceiling(s.EstimatedDuration().TotalSeconds / 5) * 5:0} s · {string.Join(" ", s.Kinds().Select(k => k.Label()))}";
        for (int i = 0; i < s.Steps.Count; i++) steps.Add(new StepRow(i + 1, s.Steps[i]));
    }

    private void SessionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        ShowSession();
        if (state != null && SessionBox.SelectedItem is SessionScript s)
        {
            state.Settings.LastSession = s.Id;
            state.Settings.Save();
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        LoadSessions();
        state?.Status($"Reloaded {SessionBox.Items.Count} session scripts.", StatusKind.Info);
    }

    private void SaveSnapshotsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || state == null) return;
        state.Settings.SaveSnapshots = SaveSnapshotsBox.IsChecked == true;
        state.Settings.Save();
    }

    private void UpdateButtons()
    {
        bool busy = running != null;
        StartButton.IsEnabled = busy || (state?.IsConnected == true && SessionBox.SelectedItem != null);
        SessionBox.IsEnabled = !busy;
        SaveSnapshotsBox.IsEnabled = !busy;
        bool selected = ResultList.SelectedItems.Count > 0;
        WatchButton.IsEnabled = selected;
        SaveButton.IsEnabled = NameBox.IsEnabled = ResultList.SelectedItems.Count == 1;
        StartButton.Style = (Style)FindResource(busy ? "DangerButton" : "AccentButton");
        StartIcon.Text = busy ? "" : "";
        StartLabel.Text = busy ? "Cancel" : "Start session";
        EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (running != null)
        {
            running.Cancel();
            return;
        }
        if (state == null || SessionBox.SelectedItem is not SessionScript script) return;

        running = new CancellationTokenSource();
        results.Clear();
        foreach (var st in steps) st.Reset();
        UpdateButtons();

        var options = new SessionOptions { CaptureRoot = ProjectPaths.Captures, SaveSnapshots = state.Settings.SaveSnapshots };
        var runner = new SessionRunner(state.Connection, state.Voice, options);
        runner.Progress += p => Dispatcher.BeginInvoke(() => OnProgress(p));
        state.Status($"{script.Name}: starting — snapshots via {state.Connection.SnapshotPath}.", StatusKind.Progress);
        try
        {
            var result = await runner.RunAsync(script, running.Token);
            foreach (var c in result.Candidates) results.Add(new CandidateRow(c));
            ResultSummary.Text = $"{result.TotalSurvivors:N0} survived · showing top {result.Candidates.Count}";
            state.Status(result.TotalSurvivors == 0
                ? $"{script.Name}: nothing survived. Try again, or loosen the tolerances in {script.SourcePath}."
                : $"{script.Name}: done in {result.Duration.TotalSeconds:0} s. Results saved to {result.CaptureDir}.",
                result.TotalSurvivors == 0 ? StatusKind.Error : StatusKind.Info);
        }
        catch (OperationCanceledException)
        {
            state.Status($"{script.Name}: cancelled.", StatusKind.Info);
        }
        catch (MemorySourceException ex)
        {
            state.Status(ex.Message, StatusKind.Error);
        }
        finally
        {
            running.Dispose();
            running = null;
            foreach (var st in steps.Where(x => x.State != StepState.Done)) st.State = StepState.Pending;
            UpdateButtons();
        }
    }

    private void OnProgress(SessionProgress p)
    {
        if (p.StepIndex >= 0 && p.StepIndex < steps.Count)
        {
            var row = steps[p.StepIndex];
            switch (p.Phase)
            {
                case SessionPhase.Speaking:
                case SessionPhase.Settling:
                    row.State = StepState.Active;
                    break;
                case SessionPhase.Sampling:
                    row.State = StepState.Sampling;
                    break;
                case SessionPhase.StepDone:
                    row.State = StepState.Done;
                    row.SetResult($"{p.Samples} samples · {p.Survivors.Values.Sum():N0} left");
                    break;
            }
        }
        string text = p.Phase switch
        {
            SessionPhase.Intro => "Intro…",
            SessionPhase.Speaking or SessionPhase.Settling => $"Step {p.StepIndex + 1} of {p.StepCount}: “{p.Message}”",
            SessionPhase.Sampling => $"Step {p.StepIndex + 1} of {p.StepCount}: watching ({p.Samples} samples)",
            SessionPhase.StepDone => $"Step {p.StepIndex + 1} of {p.StepCount}: {p.Message}",
            SessionPhase.Analyzing => "Ranking candidates…",
            _ => null,
        } ?? "";
        if (text.Length > 0) state?.Status(text, StatusKind.Progress);
    }

    // ---- results ----

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Watch_Click(sender, e);

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if (state == null) return;
        var rows = ResultList.SelectedItems.Cast<CandidateRow>().ToList();
        string prefix = (SessionBox.SelectedItem as SessionScript)?.Id ?? "candidate";
        foreach (var r in rows) state.AddWatch(r.Address, r.Kind, state.NameFor(r.Address, r.Kind) ?? $"{prefix}?");
        if (rows.Count > 0) state.Status($"Added {rows.Count} to the watch list.", StatusKind.Info);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (state == null || ResultList.SelectedItem is not CandidateRow r) return;
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            state.Status("Type a name first (e.g. throttle), then save.", StatusKind.Error);
            NameBox.Focus();
            return;
        }
        string session = (SessionBox.SelectedItem as SessionScript)?.Id ?? "capture";
        state.SaveAddress(name, r.Address, r.Kind, AddressStatus.Candidate, $"session {session}, {DateTime.Now:yyyy-MM-dd}");
        state.Status($"Saved {name} = {r.AddressText} ({r.KindText}) as a candidate. Confirm it from the watch list once verified.", StatusKind.Info);
    }
}

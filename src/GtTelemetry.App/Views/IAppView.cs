namespace GtTelemetry.App.Views;

/// <summary>A page in the main card.</summary>
public interface IAppView
{
    /// <summary>Shortcut/how-to line shown under the status line while this page is visible.</summary>
    string Hint { get; }

    void Attach(AppState state);

    void SetActive(bool active);

    void ConnectionChanged() { }

    void AdapterChanged() { }

    void Shutdown() { }
}

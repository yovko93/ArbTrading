using System.Collections.ObjectModel;
using Arbitrage.Contracts;

namespace Arbitrage.Desktop.Services;

public sealed record DesktopDiagnosticEvent(DateTimeOffset Timestamp, string Severity, string Description)
{
    public DateTimeOffset LocalTimestamp => Timestamp.ToLocalTime();
}

public sealed class DesktopDiagnostics
{
    public const int Limit = 200;
    public ObservableCollection<DesktopDiagnosticEvent> Events { get; } = [];
    public ObservableCollection<BackendDiagnosticEvent> BackendEvents { get; } = [];
    public string BackendNotice { get; private set; } = "No backend diagnostic history has been synchronized.";
    public event EventHandler? BackendChanged;
    private readonly HashSet<(Guid Instance, long Sequence)> seen = [];
    private Guid? scope;
    private Guid? instance;
    private Guid? authorizedInstance;
    private bool restarted;
    private bool historyGap;
    public Guid? BackendInstanceId => instance;
    public long LastBackendSequence { get; private set; }
    // Only a completed REST history response advances recovery. Live events are not a cursor.
    public long HistoryCursor { get; private set; }

    public void SetBackendScope(Guid workspaceId)
    {
        if (scope == workspaceId) return;
        scope = workspaceId; authorizedInstance = null; restarted = false;
        ResetBackend();
        BackendNotice = "Backend diagnostic history is synchronizing.";
        BackendChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetBackendIdentity(Guid workspaceId, Guid backendInstanceId)
    {
        if (scope != workspaceId) SetBackendScope(workspaceId);
        if (authorizedInstance == backendInstanceId && instance == backendInstanceId) return;
        restarted = instance is { } previous && previous != backendInstanceId;
        authorizedInstance = backendInstanceId;
        ResetBackend();
        instance = backendInstanceId;
        BackendNotice = restarted ? "Backend restarted. Diagnostic history is synchronizing." :
            "Backend diagnostic history is synchronizing.";
        BackendChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearBackend()
    {
        scope = null; authorizedInstance = null; restarted = false;
        ResetBackend();
        BackendNotice = "Backend diagnostics cleared after access was invalidated.";
        BackendChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetBackend()
    {
        instance = null; LastBackendSequence = 0; HistoryCursor = 0;
        BackendEvents.Clear(); seen.Clear(); historyGap = false;
    }

    public void AddBackend(BackendDiagnosticEvent entry)
    {
        if (entry.WorkspaceId != scope ||
            (authorizedInstance is { } expected && entry.BackendInstanceId != expected)) return;
        if (instance is { } old && old != entry.BackendInstanceId)
        {
            ResetBackend(); restarted = true;
            BackendNotice = "Backend restarted. Diagnostic history is synchronizing.";
        }
        instance = entry.BackendInstanceId;
        if (entry.Sequence <= HistoryCursor || !seen.Add((entry.BackendInstanceId, entry.Sequence))) return;
        LastBackendSequence = Math.Max(LastBackendSequence, entry.Sequence);
        var index = 0;
        while (index < BackendEvents.Count && BackendEvents[index].Sequence > entry.Sequence) index++;
        BackendEvents.Insert(index, entry);
        while (BackendEvents.Count > Limit)
        {
            var removed = BackendEvents[^1]; BackendEvents.RemoveAt(BackendEvents.Count - 1);
            seen.Remove((removed.BackendInstanceId, removed.Sequence));
        }
        BackendChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MergeBackend(RecentDiagnosticsResponse history)
    {
        if (history.WorkspaceId != scope ||
            (authorizedInstance is { } expected && history.BackendInstanceId != expected)) return;
        if (instance is { } old && old != history.BackendInstanceId)
        {
            ResetBackend(); restarted = true;
        }
        instance = history.BackendInstanceId;
        historyGap |= history.Gap || history.DroppedCount > 0;
        foreach (var entry in history.Events) AddBackend(entry);
        var recovered = history.Events.Where(e => e.WorkspaceId == scope && e.BackendInstanceId == instance)
            .Select(e => e.Sequence).DefaultIfEmpty(HistoryCursor).Max();
        HistoryCursor = Math.Max(HistoryCursor, recovered);
        BackendNotice = historyGap ? "Diagnostic gap: some backend events were not retained or delivered." :
            restarted ? "Backend restarted. Retained diagnostic history synchronized." :
            BackendEvents.Count == 0 ? "No backend diagnostic events are available for this workspace." :
            "Backend diagnostic history synchronized.";
        BackendChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Record(string severity, string description)
    {
        if (severity is not ("Information" or "Warning" or "Error")) throw new ArgumentException("Invalid severity.");
        if (description.Length > 160 || description.Any(char.IsControl)) throw new ArgumentException("Invalid diagnostic description.");
        Events.Insert(0, new(DateTimeOffset.UtcNow, severity, description));
        while (Events.Count > Limit) Events.RemoveAt(Events.Count - 1);
    }
}

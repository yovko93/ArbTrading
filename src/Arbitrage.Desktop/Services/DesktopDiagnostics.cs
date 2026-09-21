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
    public Guid? BackendInstanceId => instance;
    public long LastBackendSequence { get; private set; }

    public void SetBackendScope(Guid workspaceId)
    {
        if (scope == workspaceId) return;
        scope = workspaceId; instance = null; LastBackendSequence = 0;
        BackendEvents.Clear(); seen.Clear();
        BackendNotice = "Backend diagnostic history is synchronizing.";
        BackendChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddBackend(BackendDiagnosticEvent entry)
    {
        if (entry.WorkspaceId != scope || !seen.Add((entry.BackendInstanceId, entry.Sequence))) return;
        if (instance is { } old && old != entry.BackendInstanceId)
            BackendNotice = "Backend restarted. Events from the prior process may have a gap.";
        instance = entry.BackendInstanceId;
        LastBackendSequence = Math.Max(LastBackendSequence, entry.Sequence);
        var index = 0;
        while (index < BackendEvents.Count && BackendEvents[index].OccurredAtUtc > entry.OccurredAtUtc) index++;
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
        if (history.WorkspaceId != scope) return;
        if (instance is { } old && old != history.BackendInstanceId)
        {
            BackendNotice = "Backend restarted. Earlier diagnostic delivery may be incomplete.";
            LastBackendSequence = 0;
        }
        instance = history.BackendInstanceId;
        if (history.Gap || history.DroppedCount > 0)
            BackendNotice = "Diagnostic gap: some backend events were not retained or delivered.";
        foreach (var entry in history.Events) AddBackend(entry);
        if (BackendEvents.Count == 0 && !history.Gap)
            BackendNotice = "No backend diagnostic events are available for this workspace.";
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

using System.Collections.ObjectModel;

namespace Arbitrage.Desktop.Services;

public sealed record DesktopDiagnosticEvent(DateTimeOffset Timestamp, string Severity, string Description)
{
    public DateTimeOffset LocalTimestamp => Timestamp.ToLocalTime();
}

public sealed class DesktopDiagnostics
{
    public const int Limit = 200;
    public ObservableCollection<DesktopDiagnosticEvent> Events { get; } = [];

    public void Record(string severity, string description)
    {
        if (severity is not ("Information" or "Warning" or "Error")) throw new ArgumentException("Invalid severity.");
        if (description.Length > 160 || description.Any(char.IsControl)) throw new ArgumentException("Invalid diagnostic description.");
        Events.Insert(0, new(DateTimeOffset.UtcNow, severity, description));
        while (Events.Count > Limit) Events.RemoveAt(Events.Count - 1);
    }
}

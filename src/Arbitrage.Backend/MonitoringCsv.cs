using System.Globalization;
using System.Text;
using Arbitrage.Application;
using Arbitrage.LocalTransport;

namespace Arbitrage.Backend;

public sealed class MonitoringCsv(LocalOptions options)
{
    public const long MaximumBytes = 5_000_000;
    public const int RetainedFiles = 5;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Status { get; private set; } = "Disabled";
    public string? Error { get; private set; }
    public long Snapshots { get; private set; }
    public long Failures { get; private set; }
    public void ResetSession() { Status = "Disabled"; Error = null; Snapshots = Failures = 0; }
    // Text is quoted and neutralized even after whitespace/control prefixes. Native ID contents are never parsed as numbers.
    public static string Cell(string? text)
    {
        text ??= "";
        var first = text.TrimStart().FirstOrDefault();
        if (first is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r') || text.StartsWith('\n')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Identifier(string? value) => Cell(value is { Length: > 0 } && value.All(char.IsAsciiDigit) ? "'" + value : value);
    private const string Header = "timestamp,opportunityKey,lane,trust,strategy,relationshipId,title,marketA,instrumentA,marketB,instrumentB,quantity,grossProfit,grossEdge,feeAdjustedProfit,feeAdjustedEdge,feeStatus,inputQuality,skewMilliseconds,reason\r\n";
    public static string Row(MonitoredOpportunity item, DateTimeOffset at, string reason)
    {
        var r = item.Result; var a = r.Legs.ElementAtOrDefault(0); var b = r.Legs.ElementAtOrDefault(1);
        var title = r.SourceTitle ?? ""; if (title.Length > 256) title = title[..256];
        return string.Join(',', new[] { Cell(at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)), Cell(r.OpportunityKey), Cell(item.Lane.ToString()),
            Cell(r.RelationshipTrust.ToString()), Cell(r.Strategy.ToString()), Cell(r.RelationshipId.ToString()), Cell(title), Identifier(a?.Instrument.NativeMarketId),
            Identifier(a?.Instrument.NativeInstrumentId), Identifier(b?.Instrument.NativeMarketId), Identifier(b?.Instrument.NativeInstrumentId), Number(item.AvailableQuantity),
            Number(r.GrossProfit), Number(r.GrossEdgePerShare), Number(r.Fees?.FeeAdjustedGuaranteedProfit), Number(r.Fees?.FeeAdjustedEdgePerShare),
            Cell(r.FeeStatus), Cell(r.InputQuality.ToString()), Number(r.ObservedSkew is { } skew ? (decimal)skew.Ticks / TimeSpan.TicksPerMillisecond : null), Cell(reason) }) + "\r\n";
    }
    private string DirectoryFor(Guid workspace)
    {
        var directory = Path.Combine(options.DataDirectory, "monitoring", workspace.ToString("N"));
        ProtectedStorage.RejectLinks(directory); ProtectedStorage.CreatePrivateDirectory(directory); return directory;
    }
    public async Task SnapshotAsync(Guid workspace, IReadOnlyList<MonitoredOpportunity> rows, CancellationToken ct)
    {
        await WriteAsync(async () =>
        {
            var directory = DirectoryFor(workspace); var target = Path.Combine(directory, "current-opportunities.csv");
            var temp = Path.Combine(directory, "current-opportunities.pending"); ProtectedStorage.RejectLinks(target); ProtectedStorage.RejectLinks(temp);
            var contents = Header + string.Concat(rows.Take(100).Select(r => Row(r, r.Result.EvaluatedAt, "Current read-only diagnostic; not execution-ready")));
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { await file.WriteAsync(Encoding.UTF8.GetBytes(contents), ct); await file.FlushAsync(ct); file.Flush(true); }
            ProtectedStorage.RestrictFile(temp); ProtectedStorage.RejectLinks(directory); ProtectedStorage.RejectLinks(target);
            File.Move(temp, target, true); Snapshots++;
        }, ct);
    }
    public async Task AppendAlertAsync(MonitoringAlert alert, CancellationToken ct)
    {
        await WriteAsync(async () =>
        {
            var directory = DirectoryFor(alert.WorkspaceId); var path = Path.Combine(directory, "alerts.csv");
            ProtectedStorage.RejectLinks(path);
            var bytes = Encoding.UTF8.GetBytes(Row(alert.Opportunity, alert.TriggeredAt, alert.Reason));
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumBytes)
            {
                for (var i = RetainedFiles - 1; i >= 1; i--)
                {
                    var target = Path.Combine(directory, $"alerts.{i}.csv"); var source = i == 1 ? path : Path.Combine(directory, $"alerts.{i - 1}.csv");
                    ProtectedStorage.RejectLinks(target); ProtectedStorage.RejectLinks(source);
                    if (File.Exists(source)) File.Move(source, target, true);
                }
            }
            var needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            await using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            { if (needsHeader) await file.WriteAsync(Encoding.UTF8.GetBytes(Header), ct); await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); file.Flush(true); }
            ProtectedStorage.RestrictFile(path);
        }, ct);
    }
    private async Task WriteAsync(Func<Task> write, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await write(); Status = "Healthy"; Error = null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { Status = "Failed"; Error = "CsvWriteUnavailable"; Failures++; }
        finally { gate.Release(); }
    }
}

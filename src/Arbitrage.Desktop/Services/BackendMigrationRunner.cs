using System.Diagnostics;
using System.IO;

namespace Arbitrage.Desktop.Services;

public sealed record BackendMigrationResult(bool Success, string Status);
public interface IBackendMigrationRunner
{
    Task<BackendMigrationResult> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken);
}

public sealed class BackendMigrationRunner : IBackendMigrationRunner
{
    public async Task<BackendMigrationResult> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        start.ArgumentList.Add("--migrate");
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(false, "Migration process could not start.");
            // Drain pipes concurrently; retain only bounded, allowlisted diagnostic codes.
            var status = "DatabaseAlreadyCurrent";
            async Task Drain(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                    foreach (var code in new[] { "DatabaseCreated", "DatabaseAlreadyCurrent", "DatabaseMigrationApplied", "DatabaseBackupFailed", "UnsupportedNewerSchema", "DatabaseMigrationFailed" })
                        if (line == "DATABASE_STARTUP:" + code) status = code;
            }
            var output = Drain(process.StandardOutput); var error = Drain(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // Only this controlled migration child is terminated; no normal backend is started.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
                return new(false, "DatabaseMigrationFailed (migration cancelled or timed out)");
            }
            await Task.WhenAll(output, error);
            return new(process.ExitCode == 0, process.ExitCode == 0 ? status : "DatabaseMigrationFailed: " + status);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(false, "DatabaseMigrationFailed (process unavailable)"); }
    }
}

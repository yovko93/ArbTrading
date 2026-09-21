using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using Arbitrage.Contracts;
using Arbitrage.LocalTransport;

namespace Arbitrage.Desktop.Services;

public enum LocalProcessState { NotRunning, Starting, Running, Stopping, Unknown, Faulted }
public enum LocalManagementCapability { ManagedLocal, ExternalUnmanaged }
public sealed record LocalBackendObservation(LocalProcessState ProcessState, LocalManagementCapability Capability,
    string Explanation, ApplicationSnapshotResponse? Snapshot = null, int? ProcessId = null);
public sealed record ManagedRuntimeMetadata(Guid BackendInstanceId, Guid LocalProfileId, Guid WorkspaceId,
    int ProcessId, DateTimeOffset ProcessStartedAtUtc, string ArtifactPath, string DataDirectory, string BaseUrl);
public sealed record LocalBackendLaunchOptions(string DataDirectory, string RuntimeDirectory, string BaseUrl, string ArtifactPath, string? DotnetHost)
{
    public static LocalBackendLaunchOptions FromEnvironment()
    {
        var data = ResolveDirectory("Local__DataDirectory", Path.Combine(LocalPaths.Root, "backend"));
        var runtime = DesktopPaths.RuntimeDirectory;
        var url = LocalPaths.ValidateBaseUrl(Environment.GetEnvironmentVariable("Local__BaseUrl") ?? "http://127.0.0.1:5274")
            .GetLeftPart(UriPartial.Authority);
        var artifact = Environment.GetEnvironmentVariable("ARBITRAGE_BACKEND_ARTIFACT") ??
            Path.Combine(AppContext.BaseDirectory, "Arbitrage.Backend.exe");
        if (!Path.IsPathFullyQualified(artifact) || Path.GetExtension(artifact).ToLowerInvariant() is not (".exe" or ".dll"))
            throw new InvalidOperationException("Backend artifact must be an absolute built .exe or .dll path.");
        var dotnetHost = Environment.GetEnvironmentVariable("ARBITRAGE_DOTNET_HOST");
        if (Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(dotnetHost) || !Path.IsPathFullyQualified(dotnetHost)))
            throw new InvalidOperationException("ARBITRAGE_DOTNET_HOST must be an absolute dotnet executable path for a DLL artifact.");
        return new(data, runtime, url, Path.GetFullPath(artifact), dotnetHost);
    }
    private static string ResolveDirectory(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name) ?? fallback;
        if (!Path.IsPathFullyQualified(value)) throw new InvalidOperationException($"{name} must be absolute.");
        return Path.GetFullPath(value);
    }
}

public interface ILocalBackendController
{
    string ArtifactExplanation { get; }
    Task<LocalBackendObservation> ObserveAsync(CancellationToken cancellationToken);
    Task<LocalBackendObservation> StartAsync(CancellationToken cancellationToken);
    Task<LocalBackendObservation> StopAsync(LocalBackendObservation current, Action onAccepted, CancellationToken cancellationToken);
}

// Explicitly invoked only. This service never owns the backend lifetime after launch.
public sealed class LocalBackendController(BackendClient client, LocalBackendLaunchOptions options) : ILocalBackendController
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly string runtimeDirectory = options.RuntimeDirectory;
    private readonly string dataDirectory = options.DataDirectory;
    private readonly string baseUrl = options.BaseUrl;
    private readonly string artifact = options.ArtifactPath;
    private string MetadataPath => Path.Combine(runtimeDirectory, "managed-local.json");
    private string MetadataLockPath => Path.Combine(runtimeDirectory, "managed-local.lock");
    private string LaunchLockPath => Path.Combine(runtimeDirectory, "desktop-start.lock");

    public string ArtifactExplanation => !File.Exists(artifact)
        ? "Start requires a built backend executable. Set ARBITRAGE_BACKEND_ARTIFACT to its absolute path."
        : Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
          (string.IsNullOrWhiteSpace(options.DotnetHost) || !Path.IsPathFullyQualified(options.DotnetHost) || !File.Exists(options.DotnetHost))
            ? "Start requires an absolute ARBITRAGE_DOTNET_HOST path to an installed dotnet executable for a DLL artifact."
            : "Start uses the configured built backend artifact.";

    public async Task<LocalBackendObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = await client.GetSessionAsync(cancellationToken);
            var snapshot = await client.LoadSnapshotAsync(session.DefaultWorkspaceId, cancellationToken);
            if (snapshot.Session.UserId != session.UserId || snapshot.Workspace.WorkspaceId != session.DefaultWorkspaceId)
                return new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
                    "Backend identity changed during observation; process control is disabled.");
            var metadata = await ReadMetadataAsync(cancellationToken);
            if (metadata is not null && metadata.BackendInstanceId == snapshot.BackendInstanceId &&
                metadata.LocalProfileId == snapshot.LocalProfileId && metadata.WorkspaceId == snapshot.Workspace.WorkspaceId &&
                metadata.DataDirectory == dataDirectory && metadata.BaseUrl == baseUrl && metadata.ArtifactPath == artifact &&
                ProcessMatches(metadata))
                return new(LocalProcessState.Running, LocalManagementCapability.ManagedLocal,
                    "Verified application-managed backend is running.", snapshot, metadata.ProcessId);
            return new(LocalProcessState.Running, LocalManagementCapability.ExternalUnmanaged,
                "Backend is reachable, but local management ownership is unverified. Stop is unavailable.", snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (BackendFailure failure) when (failure.State is ConnectionState.AuthenticationFailed or ConnectionState.AuthorizationDenied)
        { return new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
            "Backend authentication or workspace access failed. Process control is unavailable."); }
        catch (Exception)
        {
            if (await PortIsOccupiedAsync(cancellationToken))
                return new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
                    "The configured loopback port is occupied or backend identity is uncertain. Nothing will be terminated.");
            return new(LocalProcessState.NotRunning, LocalManagementCapability.ExternalUnmanaged,
                "No backend was verified at the configured local endpoint.");
        }
    }

    public async Task<LocalBackendObservation> StartAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ProtectedStorage.CreatePrivateDirectory(runtimeDirectory);
            FileStream? launchLock = null;
            for (var retry = 0; retry < 100 && launchLock is null; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { launchLock = new FileStream(LaunchLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(200, cancellationToken); }
            }
            if (launchLock is null)
                return new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
                    "Another desktop may still be starting this backend profile. Refresh to inspect it.");
            using (launchLock)
            {
            var observed = await ObserveAsync(cancellationToken);
            if (observed.ProcessState != LocalProcessState.NotRunning) return observed;
            if (!File.Exists(artifact))
                return new(LocalProcessState.Faulted, LocalManagementCapability.ExternalUnmanaged, ArtifactExplanation);
            if (Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(options.DotnetHost) || !Path.IsPathFullyQualified(options.DotnetHost) || !File.Exists(options.DotnetHost)))
                return new(LocalProcessState.Faulted, LocalManagementCapability.ExternalUnmanaged,
                    "A DLL launch requires ARBITRAGE_DOTNET_HOST set to an absolute dotnet executable path.");
            var launch = BuildLaunch();
            using var process = Process.Start(launch);
            if (process is null)
                return new(LocalProcessState.Faulted, LocalManagementCapability.ExternalUnmanaged,
                    "The configured backend artifact could not start.");
            var started = process.StartTime.ToUniversalTime();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    return new(LocalProcessState.Faulted, LocalManagementCapability.ExternalUnmanaged,
                        "Backend exited before authenticated readiness. Check isolated backend logs and configuration.");
                await Task.Delay(200, cancellationToken);
                observed = await ObserveAsync(cancellationToken);
                if (observed.Snapshot is { } ready)
                {
                    var metadata = new ManagedRuntimeMetadata(ready.BackendInstanceId, ready.LocalProfileId,
                        ready.Workspace.WorkspaceId, process.Id, started, artifact, dataDirectory, baseUrl);
                    await WriteMetadataAsync(metadata, cancellationToken);
                    return new(LocalProcessState.Running, LocalManagementCapability.ManagedLocal,
                        "Managed backend passed authenticated readiness; synchronization is in progress.", ready, process.Id);
                }
                // A still-starting backend may briefly expose old connection metadata.
            }
            return new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
                "Backend start was attempted, but readiness was not confirmed. Inspect the process; no kill was issued.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        { return new(LocalProcessState.Faulted, LocalManagementCapability.ExternalUnmanaged,
            $"Backend start failed ({exception.GetType().Name}). Check artifact, profile, port, and logs."); }
        finally { operationGate.Release(); }
    }

    public async Task<LocalBackendObservation> StopAsync(LocalBackendObservation current, Action onAccepted, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var verified = await ObserveAsync(cancellationToken);
            if (verified.ProcessState != LocalProcessState.Running ||
                verified.Capability != LocalManagementCapability.ManagedLocal || verified.ProcessId is not { } pid ||
                verified.Snapshot is not { } snapshot || snapshot.BackendInstanceId != current.Snapshot?.BackendInstanceId)
                return new(LocalProcessState.Unknown, LocalManagementCapability.ExternalUnmanaged,
                    "Managed backend identity could not be reconfirmed. Stop was not sent.");
            var accepted = await client.StopLocalRuntimeAsync(snapshot.BackendInstanceId, cancellationToken);
            if (accepted.BackendInstanceId != snapshot.BackendInstanceId || accepted.Status != "StopRequested")
                return new(LocalProcessState.Unknown, LocalManagementCapability.ManagedLocal,
                    "Stop acknowledgment did not match this backend instance.", snapshot, pid);
            onAccepted();
            using var process = Process.GetProcessById(pid);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new(LocalProcessState.Unknown, LocalManagementCapability.ManagedLocal,
                "Stop was accepted, but process exit was not confirmed within 20 seconds. No force kill was used.", snapshot, pid); }
            await RemoveMatchingMetadataAsync(snapshot.BackendInstanceId, cancellationToken);
            return new(LocalProcessState.NotRunning, LocalManagementCapability.ExternalUnmanaged,
                "Managed backend process exit was confirmed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        { return new(LocalProcessState.Unknown, current.Capability,
            $"Stop outcome is unconfirmed ({exception.GetType().Name}). Inspect the managed process before retrying.",
            current.Snapshot, current.ProcessId); }
        finally { operationGate.Release(); }
    }

    private ProcessStartInfo BuildLaunch()
    {
        var isDll = Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var host = isDll ? options.DotnetHost! : artifact;
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(artifact)!,
            RedirectStandardOutput = false, RedirectStandardError = false
        };
        if (isDll) start.ArgumentList.Add(artifact);
        start.Environment["Local__DataDirectory"] = dataDirectory;
        start.Environment["Local__RuntimeDirectory"] = runtimeDirectory;
        start.Environment["Local__BaseUrl"] = baseUrl;
        start.Environment["Local__DeploymentMode"] = "Local";
        start.Environment["Local__TradingMode"] = "Paper";
        start.Environment["Local__ManagedLocal"] = "true";
        return start;
    }

    private async Task<ManagedRuntimeMetadata?> ReadMetadataAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProtectedStorage.VerifyPrivateFile(MetadataPath);
            await using var stream = File.OpenRead(MetadataPath);
            return await JsonSerializer.DeserializeAsync<ManagedRuntimeMetadata>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task WriteMetadataAsync(ManagedRuntimeMetadata value, CancellationToken cancellationToken)
    {
        using var metadataLock = new FileStream(MetadataLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var temporary = Path.Combine(runtimeDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
            ProtectedStorage.RestrictFile(temporary);
            File.Move(temporary, MetadataPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task RemoveMatchingMetadataAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        using var metadataLock = new FileStream(MetadataLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var metadata = await ReadMetadataAsync(cancellationToken);
        if (metadata?.BackendInstanceId == instanceId) File.Delete(MetadataPath);
    }

    private static bool ProcessMatches(ManagedRuntimeMetadata value)
    {
        try
        {
            using var process = Process.GetProcessById(value.ProcessId);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - value.ProcessStartedAtUtc.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private async Task<bool> PortIsOccupiedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = LocalPaths.ValidateBaseUrl(baseUrl);
            using var socket = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(300));
            await socket.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

}

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Arbitrage.LocalTransport;
using Arbitrage.Distribution;

namespace Arbitrage.Desktop.Services;

public sealed record DistributionManifest(int SchemaVersion, string Product, string RuntimeIdentifier,
    string TargetFrameworkDesktop, string TargetFrameworkBackend, string SourceCommit, bool SourceDirty,
    DateTimeOffset BuildUtc, string DesktopRelativePath, string BackendRelativePath, string BackendConfigRelativePath,
    string DesktopSha256, string BackendSha256, string BackendConfigSha256, string PackageMode,
    Dictionary<string, string> Files, bool SigningRequired = false, DistributionSigning? Signing = null);

public sealed record DistributionSigning(string Mode, string Status, string? PublisherSubject, string? CertificateThumbprint,
    string? SignatureAlgorithm, bool Timestamped, bool RequireTimestamp, string[] Files);

public sealed record DistributionValidation(bool IsPackage, bool Valid, string Status, DistributionManifest? Manifest = null, string SignatureSummary = "")
{
    public string Summary => !IsPackage ? "Distribution: Development · Manifest: Missing" :
        $"Distribution: PortablePackage · Manifest: {(Valid ? "Valid" : "Invalid")} · {Status}" +
        (Manifest is null ? "" : $"\nRID: {Manifest.RuntimeIdentifier} · Source: {Manifest.SourceCommit}{(Manifest.SourceDirty ? " (dirty local build)" : "")} · Built: {Manifest.BuildUtc:O}") + "\n" + SignatureSummary;
}

// The manifest detects package corruption; it is not a signature or an execution capability source.
public static class DistributionPackage
{
    public const string ManifestName = "distribution-manifest.json";
    public static DistributionValidation Validate(string root, string? selectedBackend = null, bool checkPlatform = true)
    {
        var descriptor = Path.Combine(root, ManifestName);
        if (!File.Exists(descriptor) && !Directory.Exists(descriptor))
            return Directory.Exists(Path.Combine(root, "backend")) ? new(true, false, "Packaged backend directory found but manifest is missing.") : new(false, true, "Development layout");
        try
        {
            if (checkPlatform && (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64))
                return new(true, false, "Unsupported architecture: this package requires Windows x64.");
            ProtectedStorage.RejectLinks(descriptor);
            if (new FileInfo(descriptor).Length > 1024 * 1024) throw new InvalidDataException();
            var m = JsonSerializer.Deserialize<DistributionManifest>(File.ReadAllText(descriptor)) ?? throw new InvalidDataException();
            if (m.SchemaVersion is not (1 or 2) || m.Product != "ArbitrageTrading" || m.RuntimeIdentifier != "win-x64" || m.PackageMode != "PortableZip" ||
                m.TargetFrameworkDesktop != "net10.0-windows" || m.TargetFrameworkBackend != "net10.0" ||
                m.SourceCommit is null || m.SourceCommit.Length != 40 || !m.SourceCommit.All(Uri.IsHexDigit) ||
                m.DesktopRelativePath != "Arbitrage.Desktop.exe" || m.BackendRelativePath != "backend/Arbitrage.Backend.exe" ||
                m.BackendConfigRelativePath != "backend/appsettings.json" || m.Files is null || m.Files.Count == 0)
                throw new InvalidDataException();
            var backend = Inside(root, m.BackendRelativePath);
            var actualFiles = SafeFiles(root)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(p => p != ManifestName).ToHashSet(StringComparer.Ordinal);
            if (!actualFiles.SetEquals(m.Files.Keys)) return new(true, false, "Package contains missing or unexpected files.");
            if (selectedBackend is not null && !string.Equals(Path.GetFullPath(selectedBackend), backend, StringComparison.OrdinalIgnoreCase))
                return new(true, false, "Backend override differs from the verified packaged executable.");
            foreach (var item in new[] { (m.DesktopRelativePath, m.DesktopSha256), (m.BackendRelativePath, m.BackendSha256), (m.BackendConfigRelativePath, m.BackendConfigSha256) })
                if (!m.Files.TryGetValue(item.Item1, out var hash) || hash != item.Item2) throw new InvalidDataException();
            foreach (var file in m.Files)
            {
                var path = Inside(root, file.Key);
                if (!File.Exists(path)) return new(true, false, "A required package file is missing (including backend/config dependencies).");
                ProtectedStorage.RejectLinks(path);
                using var stream = File.OpenRead(path);
                if (file.Value is null || file.Value.Length != 64 || !file.Value.All(Uri.IsHexDigit) ||
                    !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                    return new(true, false, "Package integrity mismatch. Extract a fresh verified package.");
            }
            using var config = JsonDocument.Parse(File.ReadAllText(Inside(root, m.BackendConfigRelativePath)));
            var local = config.RootElement.GetProperty("Local");
            if (local.GetProperty("DeploymentMode").GetString() != "Local" || local.GetProperty("TradingMode").GetString() != "Paper") throw new InvalidDataException();
            LocalPaths.ValidateBaseUrl(local.GetProperty("BaseUrl").GetString()!);
            var signingSummary = "Signing: LegacyUnsigned (D01 schema 1). Unsigned snapshot. Windows download/reputation warnings may occur.";
            if (m.SchemaVersion == 2)
            {
                var s = m.Signing;
                if (s is null || s.Mode is not ("Unsigned" or "Authenticode" or "TestEphemeral") ||
                    m.SigningRequired != (s.Mode != "Unsigned") || s.RequireTimestamp != (s.Mode == "Authenticode") ||
                    s.Files is null || !s.Files.ToHashSet(StringComparer.Ordinal).SetEquals(AuthenticodeVerifier.SigningFiles))
                    return new(true, false, "Invalid signing policy.", m);
                var evidence = s.Files.ToDictionary(p => p, p => AuthenticodeVerifier.Inspect(Inside(root, p)));
                var desktopSignature = evidence[m.DesktopRelativePath]; var backendSignature = evidence[m.BackendRelativePath];
                var wording = s.Mode == "Unsigned" ? "Unsigned snapshot. Windows download/reputation warnings may occur." :
                    s.Mode == "TestEphemeral" ? "Test signature only. Not a publicly trusted production publisher." : "Authenticode signature valid.";
                var valid = evidence.Values.All(e => AuthenticodeVerifier.MeetsPolicy(e, s.Mode, s.CertificateThumbprint, s.PublisherSubject, s.RequireTimestamp));
                signingSummary = $"Signing mode: {s.Mode} · Required: {m.SigningRequired}\nDesktop signature: {desktopSignature.State} · Backend signature: {backendSignature.State}\nPublisher: {backendSignature.Subject ?? "None"}\nTimestamp: {(backendSignature.Timestamped ? "Present" : "Absent")}\n" + (valid ? wording : "Signature policy failed. Managed Start is unavailable.");
                if (!valid) return new(true, false, "Signature: Invalid or does not match required policy.", m, signingSummary);
            }
            return new(true, true, "Package valid · Backend: Found · Self-contained executable", m, signingSummary);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return new(true, false, "Invalid package manifest, paths, configuration, or inaccessible files."); }
    }

    private static IEnumerable<string> SafeFiles(string root)
    {
        ProtectedStorage.RejectLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            ProtectedStorage.RejectLinks(entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in SafeFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    public static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or "")) throw new InvalidDataException();
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        ProtectedStorage.RejectLinks(path);
        return path;
    }
}


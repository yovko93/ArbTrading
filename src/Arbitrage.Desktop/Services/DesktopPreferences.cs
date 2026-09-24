using System.IO;
using System.Text.Json;
using Arbitrage.LocalTransport;

namespace Arbitrage.Desktop.Services;

public enum ThemePreference { Dark, Light, System }
public enum EffectiveTheme { Dark, Light }

public sealed record PreferencesLoadResult(ThemePreference Preference, string? Warning = null);

public interface IDesktopPreferencesStore
{
    Task<PreferencesLoadResult> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ThemePreference preference, CancellationToken cancellationToken);
}

public static class DesktopPaths
{
    public static string Directory => Resolve("ARBITRAGE_DESKTOP_DIRECTORY", Path.Combine(LocalPaths.Root, "desktop"));
    public static string RuntimeDirectory => Environment.GetEnvironmentVariable("Local__RuntimeDirectory") is not null
        ? Resolve("Local__RuntimeDirectory", LocalPaths.Runtime) : Resolve("ARBITRAGE_RUNTIME_DIRECTORY", LocalPaths.Runtime);

    private static string Resolve(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        var path = value is null ? fallback : value;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{variable} must be an absolute directory.");
        return Path.GetFullPath(path);
    }
}

public sealed class DesktopPreferencesStore(string directory) : IDesktopPreferencesStore
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly string path = Path.Combine(directory, "preferences.json");

    public async Task<PreferencesLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProtectedStorage.RejectLinks(path);
            if (!File.Exists(path)) return new(ThemePreference.System);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
                !root.TryGetProperty("themePreference", out var saved) || saved.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<ThemePreference>(saved.GetString(), false, out var preference) || !Enum.IsDefined(preference))
                return new(ThemePreference.System, "Desktop preferences are invalid; System appearance is in use. Choose a theme to save a new preference.");
            return new(preference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { return new(ThemePreference.System, "Desktop preferences could not be read; System appearance is in use. Choose a theme to try saving again."); }
    }

    public async Task SaveAsync(ThemePreference preference, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            ProtectedStorage.CreatePrivateDirectory(directory);
            ProtectedStorage.RejectLinks(path);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(stream, new { version = 1, themePreference = preference.ToString() }, cancellationToken: cancellationToken);
                for (var attempt = 0; ; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { File.Move(temporary, path, true); break; }
                    catch (Exception exception) when (OperatingSystem.IsWindows() && attempt < 20 && exception is IOException or UnauthorizedAccessException)
                    { await Task.Delay(50, cancellationToken); }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { writeGate.Release(); }
    }

}

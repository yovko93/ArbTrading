using Arbitrage.Desktop.Services;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class DesktopPreferencesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01B-tests", Guid.NewGuid().ToString("N"));
    private DesktopPreferencesStore Store => new(Path.Combine(root, "desktop"));

    [Fact]
    public async Task First_run_defaults_to_System_and_roundtrip_persists_requested_preference()
    {
        var store = Store;
        Assert.Equal(ThemePreference.System, (await store.LoadAsync(default)).Preference);
        await store.SaveAsync(ThemePreference.Dark, default);
        Assert.Equal(ThemePreference.Dark, (await Store.LoadAsync(default)).Preference);
        await store.SaveAsync(ThemePreference.System, default);
        Assert.Equal(ThemePreference.System, (await Store.LoadAsync(default)).Preference);
        var json = await File.ReadAllTextAsync(Path.Combine(root, "desktop", "preferences.json"));
        Assert.Contains("\"version\":1", json);
        Assert.Contains("\"themePreference\":\"System\"", json);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"version\":2,\"themePreference\":\"Dark\"}")]
    [InlineData("{\"version\":1,\"themePreference\":\"Purple\"}")]
    [InlineData("{\"version\":1,\"themePreference\":15}")]
    public async Task Malformed_or_unknown_documents_use_System_with_visible_warning(string json)
    {
        var directory = Path.Combine(root, "desktop"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "preferences.json"), json);
        var result = await Store.LoadAsync(default);
        Assert.Equal(ThemePreference.System, result.Preference);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
    }

    [Fact]
    public async Task Serialized_rapid_writes_leave_latest_preference()
    {
        var store = Store;
        var writes = Enumerable.Range(0, 50).Select(i => store.SaveAsync(i % 2 == 0 ? ThemePreference.Dark : ThemePreference.Light, default)).ToArray();
        await Task.WhenAll(writes);
        Assert.Equal(ThemePreference.Light, (await Store.LoadAsync(default)).Preference);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "desktop"), "*.tmp"));
    }

    [Fact]
    public async Task Unsafe_storage_failure_does_not_claim_a_successful_save()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "desktop"), "occupied");
        await Assert.ThrowsAnyAsync<Exception>(() => Store.SaveAsync(ThemePreference.Light, default));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01B-tests")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

namespace Arbitrage.Desktop.Services;

public interface ISystemThemeProvider
{
    // Null means detection failed. System must then fall back to Light.
    bool? ApplicationsUseLightTheme { get; }
    bool HighContrast { get; }
    event EventHandler? Changed;
}

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public interface IThemePaletteApplier
{
    void Apply(EffectiveTheme theme, bool highContrast);
}

public interface IThemeService
{
    ThemePreference Preference { get; }
    EffectiveTheme EffectiveTheme { get; }
    bool HighContrast { get; }
    bool IsSaved { get; }
    string? PersistenceNotice { get; }
    event EventHandler? Changed;
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SetPreferenceAsync(ThemePreference preference, CancellationToken cancellationToken = default);
}

public sealed class ThemeService(IDesktopPreferencesStore store, ISystemThemeProvider system,
    IThemePaletteApplier palettes, IUiDispatcher dispatcher) : IThemeService, IDisposable
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int revision;
    private bool initialized;
    private bool disposed;

    public ThemePreference Preference { get; private set; } = ThemePreference.System;
    public EffectiveTheme EffectiveTheme { get; private set; } = EffectiveTheme.Light;
    public bool HighContrast { get; private set; }
    public bool IsSaved { get; private set; } = true;
    public string? PersistenceNotice { get; private set; }
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        var result = await store.LoadAsync(cancellationToken);
        if (disposed) return;
        Preference = result.Preference;
        IsSaved = result.Warning is null;
        PersistenceNotice = result.Warning;
        system.Changed += OnSystemChanged;
        initialized = true;
        await dispatcher.InvokeAsync(Apply, cancellationToken);
    }

    public async Task SetPreferenceAsync(ThemePreference preference, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        ObjectDisposedException.ThrowIf(disposed, this);
        var request = Interlocked.Increment(ref revision);
        await dispatcher.InvokeAsync(() =>
        {
            if (request != Volatile.Read(ref revision) || disposed) return;
            Preference = preference;
            IsSaved = false;
            PersistenceNotice = "Saving appearance preference…";
            Apply();
        }, cancellationToken);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (request != Volatile.Read(ref revision) || disposed) return;
            try
            {
                await store.SaveAsync(preference, cancellationToken);
                await dispatcher.InvokeAsync(() =>
                {
                    if (request != Volatile.Read(ref revision) || disposed) return;
                    IsSaved = true;
                    PersistenceNotice = null;
                    Changed?.Invoke(this, EventArgs.Empty);
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (request != Volatile.Read(ref revision) || disposed) return;
                    IsSaved = false;
                    PersistenceNotice = "Appearance changed for this session, but could not be saved. It may not survive restart.";
                    Changed?.Invoke(this, EventArgs.Empty);
                }, cancellationToken);
            }
        }
        finally { writeGate.Release(); }
    }

    private void OnSystemChanged(object? sender, EventArgs args)
    {
        if (disposed) return;
        _ = dispatcher.InvokeAsync(() => { if (!disposed) Apply(); });
    }

    private void Apply()
    {
        HighContrast = system.HighContrast;
        EffectiveTheme = Preference switch
        {
            ThemePreference.Dark => EffectiveTheme.Dark,
            ThemePreference.Light => EffectiveTheme.Light,
            _ => system.ApplicationsUseLightTheme == false ? EffectiveTheme.Dark : EffectiveTheme.Light
        };
        palettes.Apply(EffectiveTheme, HighContrast);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (initialized) system.Changed -= OnSystemChanged;
        // In-flight preference writes may still release the semaphore during shutdown.
    }
}

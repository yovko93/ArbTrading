using Arbitrage.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arbitrage.Desktop.ViewModels;

public partial class ThemeSelectionViewModel : ObservableObject, IDisposable
{
    private readonly IThemeService theme;
    private readonly DesktopDiagnostics diagnostics;
    private bool synchronizing;
    private string? lastReportedNotice;
    public IReadOnlyList<ThemePreference> Choices { get; } =
        [ThemePreference.Dark, ThemePreference.Light, ThemePreference.System];
    public Task PendingChange { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private ThemePreference preference;
    [ObservableProperty] private string effectiveAppearance = "Light";
    [ObservableProperty] private string saveNotice = "";
    [ObservableProperty] private bool isSaved = true;

    public ThemeSelectionViewModel(IThemeService theme, DesktopDiagnostics diagnostics)
    {
        this.theme = theme; this.diagnostics = diagnostics;
        theme.Changed += OnThemeChanged;
        Synchronize();
    }

    partial void OnPreferenceChanged(ThemePreference value)
    {
        if (!synchronizing) PendingChange = ApplyAsync(value);
    }

    private async Task ApplyAsync(ThemePreference value)
    {
        try { await theme.SetPreferenceAsync(value); }
        catch (Exception)
        {
            SaveNotice = "Appearance could not be applied. Check desktop preferences and restart the app.";
            IsSaved = false;
            diagnostics.Record("Error", "Desktop appearance could not be applied.");
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Synchronize();
    private void Synchronize()
    {
        synchronizing = true;
        Preference = theme.Preference;
        synchronizing = false;
        EffectiveAppearance = theme.HighContrast ? "High Contrast (Windows override)" : theme.EffectiveTheme.ToString();
        IsSaved = theme.IsSaved;
        SaveNotice = theme.PersistenceNotice ?? "";
        if (!theme.IsSaved && theme.PersistenceNotice is { } notice &&
            (notice.Contains("could not", StringComparison.OrdinalIgnoreCase) || notice.Contains("invalid", StringComparison.OrdinalIgnoreCase)) &&
            lastReportedNotice != notice)
        {
            diagnostics.Record("Warning", "Desktop appearance preference could not be saved or loaded.");
            lastReportedNotice = notice;
        }
        else if (theme.IsSaved) lastReportedNotice = null;
    }

    public void Dispose() => theme.Changed -= OnThemeChanged;
}

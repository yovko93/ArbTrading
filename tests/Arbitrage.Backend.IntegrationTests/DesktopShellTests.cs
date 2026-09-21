using System.Net;
using System.Net.Http.Json;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Backend.IntegrationTests;

public sealed class DesktopShellTests
{
    private sealed class SystemTheme : ISystemThemeProvider
    {
        public bool? ApplicationsUseLightTheme { get; set; } = true;
        public bool HighContrast { get; set; }
        public event EventHandler? Changed;
        public void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    }
    private sealed class Dispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
    private sealed class Palette : IThemePaletteApplier
    {
        public List<(EffectiveTheme Theme, bool HighContrast)> Applied { get; } = [];
        public void Apply(EffectiveTheme theme, bool highContrast) => Applied.Add((theme, highContrast));
    }
    private sealed class MemoryPreferences : IDesktopPreferencesStore
    {
        public PreferencesLoadResult Loaded { get; set; } = new(ThemePreference.System);
        public ThemePreference? Saved { get; private set; }
        public bool FailSave { get; set; }
        public Task<PreferencesLoadResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Loaded);
        public Task SaveAsync(ThemePreference preference, CancellationToken cancellationToken)
        {
            if (FailSave) throw new IOException("fixture");
            Saved = preference; return Task.CompletedTask;
        }
    }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new LocalConnection("http://127.0.0.1:5274", new string('A', 64)));
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    [Fact]
    public async Task Explicit_themes_ignore_system_changes_while_System_follows_apps_preference()
    {
        var system = new SystemTheme(); var palette = new Palette(); var saved = new MemoryPreferences();
        using var theme = new ThemeService(saved, system, palette, new Dispatcher());
        await theme.InitializeAsync(default);
        Assert.Equal(ThemePreference.System, theme.Preference);
        Assert.Equal(EffectiveTheme.Light, theme.EffectiveTheme);
        system.ApplicationsUseLightTheme = false; system.Notify();
        Assert.Equal(EffectiveTheme.Dark, theme.EffectiveTheme);
        await theme.SetPreferenceAsync(ThemePreference.Light);
        Assert.Equal(EffectiveTheme.Light, theme.EffectiveTheme);
        system.ApplicationsUseLightTheme = false; system.Notify();
        Assert.Equal(EffectiveTheme.Light, theme.EffectiveTheme);
        await theme.SetPreferenceAsync(ThemePreference.Dark);
        system.ApplicationsUseLightTheme = true; system.Notify();
        Assert.Equal(EffectiveTheme.Dark, theme.EffectiveTheme);
        await theme.SetPreferenceAsync(ThemePreference.System);
        Assert.Equal(ThemePreference.System, saved.Saved);
        Assert.Equal(EffectiveTheme.Light, theme.EffectiveTheme);
        Assert.All(palette.Applied, item => Assert.True(Enum.IsDefined(item.Theme)));
    }

    [Fact]
    public async Task Failed_detection_falls_back_to_Light_and_high_contrast_does_not_change_saved_preference()
    {
        var system = new SystemTheme { ApplicationsUseLightTheme = null };
        var palette = new Palette(); var saved = new MemoryPreferences();
        using var theme = new ThemeService(saved, system, palette, new Dispatcher());
        await theme.InitializeAsync(default);
        Assert.Equal(EffectiveTheme.Light, theme.EffectiveTheme);
        system.HighContrast = true; system.Notify();
        Assert.True(theme.HighContrast); Assert.True(palette.Applied.Last().HighContrast);
        Assert.Equal(ThemePreference.System, theme.Preference);
        system.HighContrast = false; system.ApplicationsUseLightTheme = false; system.Notify();
        Assert.False(theme.HighContrast); Assert.Equal(EffectiveTheme.Dark, theme.EffectiveTheme);
    }

    [Fact]
    public async Task Save_failure_is_visible_without_undoing_applied_theme()
    {
        var saved = new MemoryPreferences { FailSave = true }; var system = new SystemTheme();
        using var theme = new ThemeService(saved, system, new Palette(), new Dispatcher());
        await theme.InitializeAsync(default);
        await theme.SetPreferenceAsync(ThemePreference.Dark);
        Assert.Equal(EffectiveTheme.Dark, theme.EffectiveTheme);
        Assert.False(theme.IsSaved); Assert.Contains("could not be saved", theme.PersistenceNotice);
    }

    [Fact]
    public async Task Header_and_settings_share_one_theme_selection_model()
    {
        var saved = new MemoryPreferences(); var system = new SystemTheme();
        using var theme = new ThemeService(saved, system, new Palette(), new Dispatcher());
        await theme.InitializeAsync(default);
        var diagnostics = new DesktopDiagnostics();
        using var selection = new ThemeSelectionViewModel(theme, diagnostics);
        using var state = StateModel(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var shell = new ShellViewModel(state, selection, diagnostics);
        shell.SelectedItem = shell.Navigation.Single(n => n.Destination == PageDestination.Settings);
        Assert.Same(selection, ((SettingsViewModel)shell.CurrentPage!).Theme);
        selection.Preference = ThemePreference.Dark;
        await selection.PendingChange;
        Assert.Equal(ThemePreference.Dark, shell.Theme.Preference);
        Assert.Equal(ThemePreference.Dark, ((SettingsViewModel)shell.CurrentPage).Theme.Preference);
        Assert.Equal(ThemePreference.Dark, saved.Saved);
    }

    [Fact]
    public void All_navigation_destinations_keep_page_models_and_unsaved_workspace_input()
    {
        var diagnostics = new DesktopDiagnostics(); var system = new SystemTheme();
        using var theme = new ThemeService(new MemoryPreferences(), system, new Palette(), new Dispatcher());
        using var selection = new ThemeSelectionViewModel(theme, diagnostics);
        using var state = StateModel(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var shell = new ShellViewModel(state, selection, diagnostics);
        Assert.Equal(10, shell.Navigation.Count);
        state.WorkspaceName = "Unsaved local edit";
        var firstDashboard = shell.CurrentPage;
        foreach (var item in shell.Navigation)
        {
            shell.SelectedItem = item;
            Assert.Equal(item.Label, shell.PageTitle);
            Assert.NotNull(shell.CurrentPage);
        }
        shell.SelectedItem = shell.Navigation[0];
        Assert.Same(firstDashboard, shell.CurrentPage);
        Assert.Equal("Unsaved local edit", state.WorkspaceName);
        Assert.Equal("Unavailable", state.ManualExecutionLabel);
        Assert.Equal("Unavailable", state.AutomaticExecutionLabel);
    }

    [Fact]
    public async Task Failed_refresh_keeps_last_known_state_stale_and_preserves_edit()
    {
        var disconnected = false; var user = Guid.NewGuid(); var workspace = Guid.NewGuid();
        using var state = StateModel(request =>
        {
            if (disconnected) throw new HttpRequestException();
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/session" => new SessionResponse(user, workspace, "Local", Capabilities.Phase01A),
                "/api/v1/system/status" => new SystemStatusResponse("1.0", 3, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase01A),
                "/api/v1/exchanges/status" => new[] { new ExchangeStatusResponse("Kalshi", "NotImplemented"), new ExchangeStatusResponse("Polymarket", "NotImplemented") },
                _ => new WorkspaceSettingsResponse(workspace, "Saved")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        });
        await state.InitializeAsync(default);
        state.WorkspaceName = "Unsaved";
        disconnected = true;
        await state.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Disconnected", state.ConnectionStatus);
        Assert.True(state.IsStale); Assert.True(state.HasSnapshot);
        Assert.Equal("1.0", state.BackendVersion); Assert.Equal("Unsaved", state.WorkspaceName);
        Assert.Equal("Last-known snapshot · stale", state.DataAge);
        disconnected = false;
        await state.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Connected", state.ConnectionStatus);
        Assert.Equal("Unsaved", state.WorkspaceName);
    }

    [Fact]
    public void Diagnostics_are_real_and_bounded()
    {
        var diagnostics = new DesktopDiagnostics();
        for (var i = 0; i < 250; i++) diagnostics.Record("Information", "Backend refresh requested.");
        Assert.Equal(DesktopDiagnostics.Limit, diagnostics.Events.Count);
        Assert.All(diagnostics.Events, entry => Assert.Equal("Backend refresh requested.", entry.Description));
        Assert.Throws<ArgumentException>(() => diagnostics.Record("Information", "unsafe\ntext"));
    }

    private static MainViewModel StateModel(Func<HttpRequestMessage, HttpResponseMessage> send)
    {
        var http = new HttpClient(new Handler(send));
        return new MainViewModel(new BackendClient(http, new Connection()), NullLogger<MainViewModel>.Instance);
    }
}

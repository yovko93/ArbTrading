using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui02BSettingsWpfTests(WpfFixture fixture)
{
    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Settings_preserves_safe_metadata_actions_and_capability_truth_in_both_themes(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new SettingsFixture(theme);
        using var errors = new BindingErrors();
        var view = new SettingsView { DataContext = new SettingsViewModel(data.State, data.Theme, data.LocalBackend, data.Credentials, data.Fees) };
        var window = Show(view);
        try
        {
            AssertCommands(view);
            foreach (var id in new[] { "SettingsAppearance", "SettingsWorkspace", "SettingsFeeProfile", "SettingsCredentials", "SettingsConnection", "SettingsApplication", "SettingsDistribution" })
                Assert.Equal(Visibility.Visible, ById<Border>(view, id).Visibility);
            Assert.Equal(Visibility.Collapsed, ById<Border>(view, "SettingsAccessDenied").Visibility);
            Assert.Same(data.State.SaveCommand, Button(view, "Save workspace name").Command);
            Assert.Same(data.State.RefreshCommand, Button(view, "Refresh backend state").Command);
            Assert.Same(data.Fees.LoadCommand, Button(view, "Load saved assumption").Command);
            Assert.Same(data.Fees.SaveCommand, Button(view, "Save diagnostic assumption").Command);
            Assert.Same(data.Credentials.SelectPrivateKeyFileCommand, Button(view, "Select Private Key File").Command);
            Assert.Same(data.Credentials.ImportCommand, Button(view, "Import / Replace").Command);
            Assert.Same(data.Credentials.RemoveCommand, Button(view, "Remove").Command);
            Assert.Same(data.Credentials.RefreshStatusCommand, Button(view, "Refresh status").Command);
            var workspaceName = Descendants<TextBox>(view).Single(t => AutomationProperties.GetName(t) == "Workspace display name");
            Assert.Equal(data.State.WorkspaceName, workspaceName.Text);
            Assert.False(Button(view, "Save workspace name").IsEnabled);
            Assert.Equal(new[] { ThemePreference.Dark, ThemePreference.Light, ThemePreference.System }, data.Theme.Choices);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == theme.ToString());
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Unknown remains distinct from zero.");
            var feeProfile = Descendants<ComboBox>(view).Single(c => AutomationProperties.GetName(c) == "Fee calculation profile (diagnostic assumption)");
            Assert.Equal("Unknown", feeProfile.SelectedItem);
            Capture(view, theme, "settings");

            var credentials = ById<Border>(view, "SettingsCredentials");
            var safeText = Descendants<TextBlock>(credentials).Select(t => t.Text).ToArray();
            Assert.Contains(safeText, t => t.Contains("Key: ****f123", StringComparison.Ordinal));
            Assert.Contains(safeText, t => t.Contains("Public fingerprint: SHA256:fixture-public-fingerprint", StringComparison.Ordinal));
            Assert.Contains(safeText, t => t.Contains("Storage: WindowsUserProtected", StringComparison.Ordinal));
            Assert.Contains(safeText, t => t.Contains("Last authentication: NotAttempted", StringComparison.Ordinal));
            Assert.DoesNotContain(safeText, t => t.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal));
            Assert.Equal("", Descendants<TextBox>(credentials).Single(t => AutomationProperties.GetName(t) == "Kalshi API key ID").Text);
            ScrollTo(view, credentials); Flush(window); Capture(view, theme, "settings-credentials");

            var distribution = ById<Border>(view, "SettingsDistribution");
            Assert.Contains(Descendants<TextBlock>(distribution), t => t.Text == data.LocalBackend.DistributionSummary);
            Assert.Contains(Descendants<TextBlock>(distribution), t => t.Text.Contains("Unsigned snapshot", StringComparison.Ordinal)
                && t.Text.Contains("Manifest: Valid", StringComparison.Ordinal) && t.Text.Contains("RID: win-x64", StringComparison.Ordinal));
            AssertCapability(view, "SettingsPaperCapability", "Available", "Good");
            foreach (var id in LiveCapabilityIds) AssertCapability(view, id, "Unavailable", "Neutral");
            ScrollTo(view, ById<Border>(view, "SettingsApplication")); Flush(window); Capture(view, theme, "settings-distribution");

            CheckMinimum(view, window, theme, "settings");
            ScrollTo(view, credentials); Flush(window); Capture(view, theme, "settings-credentials-minimum");
            foreach (var button in Descendants<Button>(credentials))
                Assert.True(button.ActualWidth <= credentials.ActualWidth, $"Credential action exceeds minimum-width section: {button.Content}");
            window.Width = 1160; window.Height = 1040; Flush(window);

            data.State.SetRealtimeStatus("Disconnected", "Fixture transport interruption"); Flush(window);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Last-known snapshot · stale");
            AssertCapability(view, "SettingsPaperCapability", "Available", "Good");
            Assert.Null(data.Credentials.Status);
            Assert.False(Button(view, "Save workspace name").IsEnabled);
            data.State.SetRealtimeStatus("AuthorizationDenied", "Fixture access denial"); Flush(window);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "SettingsAccessDenied").Visibility);
            foreach (var id in new[] { "SettingsWorkspace", "SettingsFeeProfile", "SettingsCredentials" })
                Assert.Equal(Visibility.Collapsed, ById<Border>(view, id).Visibility);
            foreach (var id in LiveCapabilityIds.Append("SettingsPaperCapability")) AssertCapability(view, id, "Unknown", "Warning");
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "No backend snapshot");
            Assert.Equal(0, data.Handler.Requests);
            Assert.Empty(errors.Messages);
        }
        finally { Close(window); }
    });

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Diagnostic_streams_keep_severity_filters_sources_and_empty_states(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var data = new SettingsFixture(theme);
        data.PopulateDiagnostics();
        using var errors = new BindingErrors();
        var view = new DiagnosticsView { DataContext = new DiagnosticsViewModel(data.State, data.Diagnostics) };
        var window = Show(view);
        try
        {
            var backendList = Descendants<ListView>(view).Single(l => AutomationProperties.GetName(l) == "Authorized backend diagnostic events");
            var desktopList = Descendants<ListView>(view).Single(l => AutomationProperties.GetName(l) == "Desktop diagnostic events");
            var severityFilter = Descendants<ComboBox>(view).Single(c => AutomationProperties.GetName(c) == "Backend severity filter");
            var sourceFilter = Descendants<ComboBox>(view).Single(c => AutomationProperties.GetName(c) == "Backend source filter");
            Assert.Equal(3, backendList.Items.Count); Assert.Equal(3, desktopList.Items.Count);
            Assert.Equal(new[] { "UTC time", "Severity", "Source", "Event" }, ((GridView)backendList.View).Columns.Select(c => c.Header.ToString()));
            Assert.Equal(new[] { "Local time", "Severity", "Event" }, ((GridView)desktopList.View).Columns.Select(c => c.Header.ToString()));
            foreach (var list in new[] { backendList, desktopList })
            {
                foreach (var (severity, tone) in new[] { ("Information", "Info"), ("Warning", "Warning"), ("Error", "Error") })
                    Assert.Contains(Descendants<StatusBadge>(list), b => b.Label == severity && b.Tone == tone);
            }
            Assert.Equal(Visibility.Collapsed, ById<Border>(view, "DiagnosticsBackendEmpty").Visibility);
            Assert.Equal(Visibility.Collapsed, ById<Border>(view, "DiagnosticsDesktopEmpty").Visibility);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Backend diagnostic history synchronized.");
            var synchronizationNotice = Descendants<Border>(view).Single(b => b.Child is TextBlock { Name: "BackendNoticeText" });
            Assert.Null(synchronizationNotice.Tag);
            Assert.Equal(Visibility.Visible, synchronizationNotice.Visibility);
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("SemanticBrush.NeutralBackground")).Color,
                ((SolidColorBrush)synchronizationNotice.Background).Color);
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("PrimaryText")).Color,
                ((SolidColorBrush)((TextBlock)synchronizationNotice.Child).Foreground).Color);
            Capture(view, theme, "diagnostics");

            severityFilter.SelectedIndex = 2; Flush(window);
            Assert.Single(backendList.Items.Cast<BackendDiagnosticEvent>());
            Assert.Equal("Warning", Assert.IsType<BackendDiagnosticEvent>(backendList.Items[0]).Severity);
            sourceFilter.SelectedIndex = 4; Flush(window);
            Assert.Empty(backendList.Items.Cast<BackendDiagnosticEvent>());
            Assert.Equal(Visibility.Visible, ById<Border>(view, "DiagnosticsBackendEmpty").Visibility);
            Assert.Equal(Visibility.Collapsed, backendList.Visibility);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Backend diagnostic history synchronized.");
            sourceFilter.SelectedIndex = 2; Flush(window);
            Assert.Equal("Workspace", Assert.IsType<BackendDiagnosticEvent>(Assert.Single(backendList.Items.Cast<object>())).Source);
            sourceFilter.SelectedIndex = 0; severityFilter.SelectedIndex = 3; Flush(window);
            Assert.Equal("Error", Assert.IsType<BackendDiagnosticEvent>(Assert.Single(backendList.Items.Cast<object>())).Severity);
            severityFilter.SelectedIndex = 1; Flush(window);
            Assert.Equal("Information", Assert.IsType<BackendDiagnosticEvent>(Assert.Single(backendList.Items.Cast<object>())).Severity);
            severityFilter.SelectedIndex = 0; Flush(window); Assert.Equal(3, backendList.Items.Count);
            CheckMinimum(view, window, theme, "diagnostics");
            Assert.True(severityFilter.ActualWidth <= view.ActualWidth);
            Assert.True(sourceFilter.ActualWidth <= view.ActualWidth);

            data.Diagnostics.ClearBackend(); data.Diagnostics.Events.Clear(); Flush(window);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "DiagnosticsBackendEmpty").Visibility);
            Assert.Equal(Visibility.Visible, ById<Border>(view, "DiagnosticsDesktopEmpty").Visibility);
            Assert.Equal(Visibility.Collapsed, backendList.Visibility); Assert.Equal(Visibility.Collapsed, desktopList.Visibility);
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Backend diagnostics cleared after access was invalidated.");
            window.Width = 1160; window.Height = 1040; Flush(window);
            ((ScrollViewer)view.Content).ScrollToTop(); Flush(window); Capture(view, theme, "diagnostics-empty");
            Assert.Equal(0, data.Handler.Requests);
            Assert.Empty(errors.Messages);
        }
        finally { Close(window); }
    });

    private static readonly string[] LiveCapabilityIds = ["SettingsLiveCapability", "SettingsManualLiveCapability", "SettingsAutomaticLiveCapability"];
    private static Button Button(DependencyObject root, string content) => Descendants<Button>(root).Single(b => Equals(b.Content, content));
    private static void AssertCapability(DependencyObject root, string id, string label, string tone)
    {
        var badge = ById<StatusBadge>(root, id);
        Assert.Equal(label, badge.Label); Assert.Equal(tone, badge.Tone);
    }

    internal sealed class SettingsFixture : IDisposable
    {
        private static readonly DateTimeOffset At = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
        private static readonly Guid Workspace = Guid.Parse("d58f4d82-556c-49c5-bdd1-36ba2a9ca100");
        private static readonly Guid Instance = Guid.Parse("a6f554da-7796-4894-a5dc-aa8e279cb100");
        public RejectHandler Handler { get; } = new();
        private readonly HttpClient http;
        public DesktopDiagnostics Diagnostics { get; } = new();
        public MainViewModel State { get; }
        public ThemeSelectionViewModel Theme { get; }
        public KalshiCredentialsViewModel Credentials { get; }
        public FeeProfileViewModel Fees { get; }
        public BackendFixture LocalBackend { get; } = new();
        public SettingsFixture(EffectiveTheme theme)
        {
            http = new HttpClient(Handler);
            var backend = new BackendClient(http, new MissingConnection());
            State = new MainViewModel(backend, NullLogger<MainViewModel>.Instance, Diagnostics);
            State.ApplyRealtimeSnapshot(new(1, Instance, Guid.Parse("ecf2c317-555b-479b-8fbd-02120b827100"), At,
                new(Guid.Parse("ecf2c317-555b-479b-8fbd-02120b827101"), Workspace, "Local", Capabilities.Phase04A),
                new("UI-02B fixture", 1800, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A),
                new(Workspace, "Paper research workspace"), []), "http://127.0.0.1:5274");
            State.LastSuccessfulRefresh = At; State.SetHeartbeatStatus("Heartbeat received at 2026-10-05 09:30:00 UTC");
            Theme = new ThemeSelectionViewModel(new StubTheme(theme), Diagnostics);
            Credentials = new KalshiCredentialsViewModel(State, backend)
            {
                Status = new(true, "****f123", "SHA256:fixture-public-fingerprint", At, At, "WindowsUserProtected", "NotAttempted", Guid.Parse("ecf2c317-555b-479b-8fbd-02120b827102")),
                Notice = "Fixture metadata: configured for market-data authentication; realtime has not been started."
            };
            Fees = new FeeProfileViewModel(State, backend);
        }
        public void PopulateDiagnostics()
        {
            Diagnostics.SetBackendIdentity(Workspace, Instance);
            Diagnostics.MergeBackend(new(Instance, Workspace, 1, 3, 0, false,
            [
                new(Instance, 1, At.AddMinutes(-2), "Information", "Backend", "SnapshotSynchronized", "Fixture backend snapshot synchronized.", Workspace, null),
                new(Instance, 2, At.AddMinutes(-1), "Warning", "Workspace", "SnapshotStale", "Fixture retained snapshot became stale.", Workspace, null),
                new(Instance, 3, At, "Error", "Runtime", "ObservationFault", "Fixture runtime observation failed; no order was submitted.", Workspace, null)
            ]));
            Diagnostics.Events.Add(new(At, "Error", "Fixture desktop reconnect attempt failed."));
            Diagnostics.Events.Add(new(At.AddMinutes(-1), "Warning", "Fixture desktop retained a stale snapshot."));
            Diagnostics.Events.Add(new(At.AddMinutes(-2), "Information", "Fixture desktop view opened."));
        }
        public void Dispose() { Credentials.Dispose(); Fees.Dispose(); Theme.Dispose(); State.Dispose(); http.Dispose(); }
    }

    internal sealed class BackendFixture
    {
        public string ProcessStatus => "Running";
        public string ManagementStatus => "Managed";
        public string Explanation => "Fixture local backend is running and owned by this desktop session.";
        public string StartExplanation => "Start is unavailable while the managed backend is running.";
        public string StopExplanation => "Use the shell controls for an explicit managed-backend stop.";
        public string DistributionSummary => "Distribution: Portable\nManifest: Valid · RID: win-x64\nSource commit: 159cd96ecbf09884e4d58e708c9a549c4ac38eb1\nSigning: Unsigned snapshot — signature not present\nBackend artifact: Fixture package backend\nData directory: C:\\FixtureData\\ArbitrageTrading\nRuntime directory: C:\\FixtureRuntime\\ArbitrageTrading\nDatabase startup: Ready (fixture only)";
    }
}

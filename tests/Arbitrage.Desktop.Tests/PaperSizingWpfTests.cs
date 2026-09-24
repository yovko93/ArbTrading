using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class PaperSizingWpfTests(WpfFixture fixture)
{
    [Fact] public Task Nine_sizing_states_render_light_dark_with_mode_visibility() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new Reject()), new Connection()); using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperTradingViewModel(state, backend); var at = DateTimeOffset.UtcNow; var workspace = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), workspace, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(workspace, "Fixture"), []), "http://127.0.0.1:5274");
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var scenario in new[] { "FixedQuantity", "AdaptiveEdit", "Selected", "RiskLimited", "DepthLimited", "NoAdmissible", "AdaptiveArmed", "AdaptiveHistory", "KillSwitchLatched" })
        {
            var adaptive = scenario != "FixedQuantity"; var armed = scenario == "AdaptiveArmed"; var killed = scenario == "KillSwitchLatched";
            vm.AutomationSizingMode = adaptive ? "LargestAdmissibleGridQuantity" : "FixedQuantity"; vm.AdaptiveMinimum = "5"; vm.AdaptiveMaximum = "25"; vm.AdaptiveStep = "5";
            vm.AutomationStatus = new(killed ? scenario : armed ? "Armed" : "Disarmed", "None",
                new(adaptive ? 2 : 1, Guid.NewGuid(), new(vm.AutomationSizingMode, 5, .005m, .05m, 10, 10, 2, 5, 60, .25m, 20, true, true,
                    adaptive ? 5 : null, adaptive ? 25 : null, adaptive ? 5 : null), at, at, Guid.NewGuid(), new string('A', 64)),
                Guid.NewGuid(), Guid.NewGuid(), new(null, killed, killed ? at : null, killed ? Guid.NewGuid() : null, killed ? "Owner emergency stop" : "", null, null),
                armed ? Guid.NewGuid() : null, armed ? at : null, armed ? Guid.NewGuid() : null, "Running", 0, 0, 0, 0, null, null, [], 0, new Dictionary<string, long>());
            vm.SizingPreview = scenario is "FixedQuantity" or "AdaptiveEdit" ? null : new(scenario == "NoAdmissible" ? "NoAdmissibleQuantity" : "Selected", vm.AutomationSizingMode,
                scenario == "NoAdmissible" ? null : scenario is "RiskLimited" or "DepthLimited" ? 10 : 25,
                scenario == "NoAdmissible" ? 5 : scenario is "RiskLimited" or "DepthLimited" ? 4 : 1, 25, 5, 5,
                scenario == "RiskLimited" ? [new("Risk:MarketCostBasisLimit", 3)] : scenario == "DepthLimited" ? [new("InsufficientDepth", 3)] : scenario == "NoAdmissible" ? [new("MinimumProfitRejected", 5)] : [],
                scenario == "NoAdmissible" ? null : scenario is "RiskLimited" or "DepthLimited" ? 9.1m : 23.35m,
                scenario == "NoAdmissible" ? null : scenario is "RiskLimited" or "DepthLimited" ? .26792m : .675035m,
                scenario == "NoAdmissible" ? null : scenario is "RiskLimited" or "DepthLimited" ? 9.36792m : 24.025035m,
                scenario == "NoAdmissible" ? null : scenario is "RiskLimited" or "DepthLimited" ? 10 : 25,
                scenario == "NoAdmissible" ? null : scenario is "RiskLimited" or "DepthLimited" ? .63208m : .974965m,
                scenario == "NoAdmissible" ? null : scenario is "RiskLimited" or "DepthLimited" ? .063208m : .0389986m, null, null, at);
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            var automation = new PaperAutomationView(); var panel = new StackPanel { DataContext = vm }; panel.Children.Add(automation);
            if (scenario == "AdaptiveHistory")
            {
                var history = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, ItemsSource = new[] { new { Sizing = "Adaptive Q=10 (grid 5..25 step 5)", Candidates = 4, State = "Committed", Proof = "A1B2C3D4E5F6…", RiskRevision = Guid.NewGuid() } } };
                foreach (var name in new[] { "Sizing", "Candidates", "State", "Proof", "RiskRevision" }) history.Columns.Add(new DataGridTextColumn { Header = name, Binding = new System.Windows.Data.Binding(name), Width = name == "Sizing" ? 350 : name == "RiskRevision" ? 350 : 160 });
                panel.Children.Add(history);
            }
            var view = new Border { Child = panel, Padding = new Thickness(16) }; view.SetResourceReference(Border.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1440, 1400)); view.Arrange(new Rect(0, 0, 1440, 1400)); view.UpdateLayout();
            var inputs = Descendants(automation).OfType<TextBox>().ToArray();
            var minimum = inputs.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Minimum quantity");
            var fixedInput = inputs.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Fixed quantity");
            Assert.Equal(adaptive ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)minimum.Parent).Parent is FrameworkElement wrap ? ((FrameworkElement)wrap.Parent).Visibility : Visibility.Visible);
            Assert.Equal(adaptive ? Visibility.Collapsed : Visibility.Visible, ((FrameworkElement)fixedInput.Parent).Visibility);
            Assert.True(panel.DesiredSize.Height < 1400, $"Clipped fixture: {scenario}");
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } directory)
            {
                Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap(1440, 1400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, $"sizing-{theme}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private sealed class Reject : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network."); }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime credentials.");
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
}

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
public sealed class PaperAutomationWpfTests(WpfFixture fixture)
{
    [Fact] public Task Nine_automation_states_render_in_both_themes() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new Reject()), new Connection()); using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperTradingViewModel(state, backend); var at = DateTimeOffset.UtcNow; var w = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var scenario in new[] { "NotConfigured", "DisarmedReady", "Armed", "MonitoringStopped", "RiskPolicyChanged", "SessionLimitReached", "KillSwitchLatched", "AutomaticExecutionHistory", "BestEffortWarning" })
        {
            var armed = scenario == "Armed"; var killed = scenario == "KillSwitchLatched";
            vm.AutomationStatus = new(scenario == "NotConfigured" ? scenario : killed ? scenario : armed ? "Armed" : "Disarmed", scenario,
                scenario == "NotConfigured" ? null : new(1, Guid.NewGuid(), new("FixedQuantity", 1, .005m, .05m, 10, 10, 1, 5, 60, .25m, 20, true, scenario == "BestEffortWarning"), at, at, Guid.NewGuid(), new string('A', 64)),
                Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), killed, killed ? at : null, killed ? Guid.NewGuid() : null, killed ? "Owner emergency stop" : "", null, null),
                armed ? Guid.NewGuid() : null, armed ? at : null, armed ? Guid.NewGuid() : null, scenario == "MonitoringStopped" ? "Stopped" : "Running",
                scenario == "SessionLimitReached" ? 10 : 1, 4, 2, 1, at, at, [new("Kalshi", "USD", .4m, .0168m, .4168m), new("Polymarket", "USD", .5m, .01m, .51m)], 0, new Dictionary<string, long>());
            vm.AutomationNotice = scenario == "BestEffortWarning" ? "Polymarket BestEffort is weaker continuity. Explicit acknowledgment is required." : "Backend ownership: closing this WPF view does not disarm.";
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            var panel = new StackPanel { DataContext = vm }; panel.Children.Add(new PaperAutomationView());
            if (scenario == "AutomaticExecutionHistory")
            {
                var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 30, ColumnHeaderHeight = 32, ItemsSource = new[] { new { Origin = "AutomaticPaper", Session = Guid.NewGuid(), Quantity = 1, State = "Committed", Cost = .9268m } } };
                foreach (var name in new[] { "Origin", "Session", "Quantity", "State", "Cost" }) grid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new System.Windows.Data.Binding(name), Width = name == "Session" ? 360 : 160 });
                panel.Children.Add(grid);
            }
            var view = new Border { Child = panel, Padding = new Thickness(16) }; view.SetResourceReference(Border.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1440, 1200)); view.Arrange(new Rect(0, 0, 1440, 1200)); view.UpdateLayout();
            Assert.Equal(!armed && !killed && scenario != "NotConfigured" && scenario != "MonitoringStopped", vm.ArmAutomationCommand.CanExecute(null));
            Assert.Equal(killed, vm.ResetAutomationKillCommand.CanExecute(null)); Assert.Equal(10, vm.AutomationInputs.Count);
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } directory)
            {
                Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap(1440, 1200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, $"automation-{theme}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });
    private sealed class Reject : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network in fixture."); }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime credentials in fixture.");
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
}

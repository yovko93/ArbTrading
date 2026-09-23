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
public sealed class PaperRiskWpfTests(WpfFixture fixture)
{
    [Fact] public Task Risk_policy_and_preview_fixtures_render_light_and_dark() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new Reject()), new Connection()); using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperTradingViewModel(state, backend); var at = DateTimeOffset.UtcNow; var generation = Guid.NewGuid(); var w = Guid.NewGuid();
        state.ApplyRealtimeSnapshot(new(1, Guid.NewGuid(), Guid.NewGuid(), at, new(Guid.NewGuid(), w, "Local", Capabilities.Phase04A),
            new("fixture", 1, "Healthy", "Local", "Paper", "Paper", Capabilities.Phase04A), new(w, "Fixture"), []), "http://127.0.0.1:5274");
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var scenario in new[] { "NotConfigured", "WithinLimits", "OverLimit", "CashReserveViolation", "MarketExposureViolation", "PositionCountViolation", "ApprovedPreview", "RejectedPreview", "PolicyEdit" })
        {
            var code = scenario switch { "CashReserveViolation" => "MinimumCashReserve", "PositionCountViolation" => "OpenPositionCountLimit", _ => "MarketCostBasisLimit" };
            var approved = scenario is "WithinLimits" or "ApprovedPreview" or "PolicyEdit";
            var decision = new PaperRiskDecisionResponse(scenario == "NotConfigured" ? "NotConfigured" : approved ? "Approved" : "Rejected", 1, Guid.NewGuid(), new string('A', 64), generation, 1,
                approved ? [] : [new(scenario == "NotConfigured" ? "RiskPolicyNotConfigured" : code, "Kalshi", "USD", "FIXTURE-MARKET", null, 18, 5, 23, 20, -3)],
                [new("Kalshi", "USD", 100, 90, 8, 82, 20, 62, 22, 30, 60, 30, 12, 20, 12, 20)],
                [new("Kalshi", "USD", "FIXTURE-MARKET", null, 4, 8, 12, 20, 8)], 1, 2, 1, 2, 0, 1, [], at);
            vm.RiskStatus = new(scenario == "NotConfigured" ? "NotConfigured" : approved ? "WithinLimits" : "OverLimit", scenario == "NotConfigured" ? null :
                new(1, decision.PolicyRevision!.Value, new(.2m, .1m, .6m, .2m, .2m, 20, 10, 2, 1000, .001m, 0), at, at, Guid.NewGuid(), new string('A', 64)), decision);
            vm.RiskNotice = scenario == "NotConfigured" ? "Suggested form values are NOT active until saved." : "Policy applies only to NEW paper entries.";
            vm.Preview = new(Guid.NewGuid(), generation, at, at.AddSeconds(5), approved, approved ? "None" : "RiskLimitExceeded", new string('A', 64), 10, 10, [], [], 9, 0, 9, 10, 1, null, [], decision);
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            var panel = new StackPanel { DataContext = vm }; panel.Children.Add(new PaperRiskView());
            panel.Children.Add(new TextBlock { Text = vm.PreviewText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
            var button = new Button { Content = "Confirm paper execution…", Command = vm.ExecuteCommand,
                Style = (Style)new PaperTradingView().Resources[typeof(Button)] }; panel.Children.Add(button);
            var view = new Border { Child = panel, Padding = new Thickness(16) }; view.SetResourceReference(Border.BackgroundProperty, "ApplicationBackground");
            view.Measure(new Size(1440, 1400)); view.Arrange(new Rect(0, 0, 1440, 1400)); view.UpdateLayout();
            Assert.Equal(approved, button.IsEnabled); Assert.Contains("Risk:", vm.PreviewText); Assert.Equal(11, vm.RiskInputs.Count);
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } directory)
            {
                Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap(1440, 1400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, $"risk-{theme}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });
    private sealed class Reject : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network in fixture."); }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No runtime credentials in fixture.");
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
}

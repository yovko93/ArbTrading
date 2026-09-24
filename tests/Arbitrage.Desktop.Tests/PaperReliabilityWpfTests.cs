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
public sealed class PaperReliabilityWpfTests(WpfFixture fixture)
{
    [Fact] public Task Nine_reliability_states_render_in_both_themes_without_network() => fixture.RunAsync(() =>
    {
        var backend = new BackendClient(new HttpClient(new Reject()), new Connection()); using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperReliabilityViewModel(state, backend); var at = DateTimeOffset.UtcNow;
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var scenario in new[] { "NoCampaign", "Collecting", "Paused", "InsufficientEvidence", "CriteriaMet", "CriteriaNotMet", "InvariantViolation", "EvidenceGap", "Completed" })
        {
            vm.Campaigns.Clear(); vm.Campaign = null;
            if (scenario != "NoCampaign")
            {
                var campaign = new PaperReliabilityCampaignResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Paper burn-in fixture", "Isolated simulation", at, null, null,
                    scenario is "Paused" or "Completed" ? scenario : "Collecting", 1, new string('A', 64), scenario == "EvidenceGap", scenario == "InvariantViolation", false, null);
                vm.Campaigns.Add(campaign); vm.Campaign = campaign;
                vm.Report = new(campaign.Id, campaign.WorkspaceId, campaign.Name, at, at, campaign.State, 1, campaign.PolicyFingerprint,
                    scenario is "CriteriaMet" or "CriteriaNotMet" or "InvariantViolation" ? scenario : "InsufficientEvidence", campaign.EvidenceGapDetected,
                    new Dictionary<string, long> { ["BackendObservedTicks"] = TimeSpan.FromHours(24).Ticks, ["CandidateInputsObserved"] = 100 },
                    [new("AutomationHealthyTicks", TimeSpan.FromHours(4).Ticks, TimeSpan.FromHours(scenario == "CriteriaMet" ? 4 : 2).Ticks, scenario == "CriteriaMet" ? "Satisfied" : "NotSatisfied", "MinimumInclusive")],
                    [new("LedgerReconciliationHealthy", 0, 0, scenario == "InvariantViolation" ? "Violated" : scenario == "EvidenceGap" ? "Unknown" : "Satisfied", "Fixture")],
                    [new(Guid.NewGuid(), "Kalshi", "USD", 9.36m, 10, .64m, 10, .64m, 0, .26m)], [], new Dictionary<string, decimal> { ["SelectedQuantityAverage"] = 10 },
                    "PAPER SIMULATION EVIDENCE ONLY", ["CriteriaMet never enables live trading."], new string('B', 64));
                Assert.Equal(scenario == "CriteriaMet" ? "04:00:00" : "02:00:00", vm.Criteria[0].ObservedValue);
            }
            new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme == "Light" ? EffectiveTheme.Light : EffectiveTheme.Dark, false);
            var view = new PaperReliabilityView { DataContext = vm }; view.Measure(new Size(1440, 1600)); view.Arrange(new Rect(0, 0, 1440, 1600)); view.UpdateLayout();
            Assert.True(view.ActualWidth > 0); if (scenario != "NoCampaign") Assert.Contains("Evidence", vm.Summary);
            if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is { } directory)
            {
                Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap(1440, 1600, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, $"reliability-{theme}-{scenario}.png")); encoder.Save(stream);
            }
        }
    });
    private sealed class Reject : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network."); }
    private sealed class Connection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("No credentials.");
        public Task WriteAsync(LocalConnection c, CancellationToken ct) => throw new NotSupportedException();
    }
}

using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Arbitrage.Contracts;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Microsoft.Extensions.Logging.Abstractions;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui02BReliabilityWpfTests(WpfFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 34, 0, TimeSpan.Zero);
    private static readonly Guid CampaignId = Guid.Parse("c02b0000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("c02b0000-0000-0000-0000-000000000002");
    private static readonly Guid GenerationId = Guid.Parse("c02b0000-0000-0000-0000-000000000003");

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Reliability_fixture_preserves_evidence_safety_and_commands_at_both_widths(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        using var handler = new RejectHandler();
        using var http = new HttpClient(handler);
        var backend = new BackendClient(http, new MissingConnection());
        using var state = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        using var vm = new PaperReliabilityViewModel(state, backend);
        using var errors = new BindingErrors();
        var view = new PaperReliabilityView { DataContext = vm };
        var window = Show(view);
        try
        {
            AssertCommands(view);
            Assert.Equal("NoActiveCampaign", ById<StatusBadge>(view, "ReliabilityCampaignState").Label);
            Assert.Equal("Neutral", ById<StatusBadge>(view, "ReliabilityCampaignState").Tone);
            Assert.Contains(Descendants<TextBlock>(view), t => t.IsVisible && t.Text == "Safety invariant evidence is unavailable. Unknown is not Satisfied.");
            Assert.Contains(Descendants<TextBlock>(view), t => t.IsVisible && t.Text == "No campaign history is available. Starting a campaign is an explicit action.");
            Capture(view, theme, "reliability-empty");

            var campaign = Campaign();
            vm.Campaigns.Add(campaign);
            vm.Campaign = campaign;
            vm.Report = Report(campaign);
            vm.Notice = "Fixture only: collection is isolated from the runtime.";
            Flush(window);
            Assert.Equal("Collecting", ById<StatusBadge>(view, "ReliabilityCampaignState").Label);
            Assert.Equal("Info", ById<StatusBadge>(view, "ReliabilityCampaignState").Tone);
            Assert.Equal("InsufficientEvidence", ById<StatusBadge>(view, "ReliabilityEvidenceState").Label);
            Assert.Equal("Warning", ById<StatusBadge>(view, "ReliabilityEvidenceState").Tone);
            AssertCommands(view);
            AssertActionBindings(view, vm);
            Capture(view, theme, "reliability-collecting");

            var invariants = ById<Border>(view, "ReliabilitySafetyInvariants");
            var criteria = ById<Border>(view, "ReliabilityEvidenceCriteria");
            Assert.True(invariants.TranslatePoint(new Point(), view).Y < criteria.TranslatePoint(new Point(), view).Y);
            Assert.True(invariants.BorderThickness.Left > 1);
            ScrollTo(view, invariants); Flush(window);
            Assert.Contains(Descendants<StatusBadge>(invariants), b => b.Label == "Satisfied" && b.Tone == "Good");
            Assert.Contains(Descendants<TextBlock>(invariants), t => t.Text == "FixtureLedgerReconciled");

            vm.Report = vm.Report with
            {
                State = "InvariantViolation",
                EvidenceGapDetected = true,
                Invariants =
                [
                    new("LedgerReconciliationHealthy", 0, 0, "Violated", "FixtureLedgerMismatch"),
                    new("ExecutionEvidenceComplete", 0, 0, "Unknown", "FixtureEvidenceUnavailable"),
                    new("NoDuplicateExecutionRequestIds", 0, 0, "Satisfied", "FixtureNoDuplicates")
                ]
            };
            Flush(window);
            Assert.Equal("Error", ById<StatusBadge>(view, "ReliabilityEvidenceState").Tone);
            Assert.Contains(Descendants<StatusBadge>(invariants), b => b.Label == "Violated" && b.Tone == "Error");
            Assert.Contains(Descendants<StatusBadge>(invariants), b => b.Label == "Unknown" && b.Tone == "Warning");
            Assert.Contains(Descendants<StatusBadge>(invariants), b => b.Label == "Satisfied" && b.Tone == "Good");
            Assert.Contains(Descendants<TextBlock>(invariants), t => t.Text == "FixtureEvidenceUnavailable");
            Assert.True(invariants.IsVisible);
            Capture(view, theme, "reliability-invariants");

            ScrollTo(view, criteria); Flush(window);
            var criteriaGrid = Descendants<DataGrid>(criteria).Single();
            Assert.Equal(vm.Criteria.ToArray(), criteriaGrid.Items.Cast<ReliabilityCriterionRow>().ToArray());
            var row = Assert.IsType<ReliabilityCriterionRow>(criteriaGrid.Items[0]);
            Assert.Equal("02:00:00", row.ObservedValue);
            Assert.Equal("04:00:00", row.RequiredValue);
            Assert.Contains(Descendants<TextBlock>(criteriaGrid), t => t.Text == "02:00:00");
            Assert.Contains(Descendants<TextBlock>(criteriaGrid), t => t.Text == "04:00:00");
            Assert.Contains(Descendants<StatusBadge>(criteriaGrid), b => b.Label == "NotSatisfied" && b.Tone == "Warning");
            Assert.Contains(Descendants<TextBlock>(criteriaGrid), t => t.Text == "MinimumInclusive");
            Capture(view, theme, "reliability-criteria");

            var funnel = ById<Border>(view, "ReliabilityOperationalFunnel");
            ScrollTo(view, funnel); Flush(window);
            var workerTile = Descendants<Border>(funnel).Single(b => Descendants<TextBlock>(b).Any(t => t.Text == "UNEXPECTED WORKER FAULTS"));
            Assert.Contains(Descendants<TextBlock>(workerTile), t => t.Text == "Unavailable");
            Assert.DoesNotContain(Descendants<TextBlock>(workerTile), t => t.Text == "0");
            var allCounters = Descendants<Expander>(funnel).Single();
            allCounters.IsExpanded = true; Flush(window);
            Assert.Contains(Descendants<TextBlock>(allCounters), t => t.Text == "CandidateInputsObserved");
            Assert.Contains(Descendants<TextBlock>(allCounters), t => t.Text == "SelectedQuantityAverage");
            allCounters.IsExpanded = false; Flush(window);

            var economics = ById<Border>(view, "ReliabilityPaperEconomics");
            ScrollTo(view, economics); Flush(window);
            var economicsGrid = Descendants<DataGrid>(economics).Single();
            Assert.Same(vm.Report.Economics, economicsGrid.ItemsSource);
            Assert.Equal(2, economicsGrid.Items.Count);
            Assert.Contains(Descendants<TextBlock>(economicsGrid), t => t.Text == "USD");
            Assert.Contains(Descendants<TextBlock>(economicsGrid), t => t.Text == "USDC");
            Assert.Contains(Descendants<TextBlock>(economics), t => t.IsVisible && t.Text.Contains("ManualScenario resolution is not exchange performance. No USD/USDC aggregation.", StringComparison.Ordinal));
            Capture(view, theme, "reliability-economics");

            vm.Report = Report(campaign) with { State = "CriteriaMet", Criteria = [new("AutomationHealthyTicks", TimeSpan.FromHours(4).Ticks, TimeSpan.FromHours(4).Ticks, "Satisfied", "MinimumInclusive")] };
            Descendants<ScrollViewer>(view).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto).ScrollToTop();
            Flush(window);
            Assert.Equal("CriteriaMet", ById<StatusBadge>(view, "ReliabilityEvidenceState").Label);
            Assert.Equal("Good", ById<StatusBadge>(view, "ReliabilityEvidenceState").Tone);
            Assert.Contains(Descendants<TextBlock>(view), t => t.IsVisible && t.Text.Contains("CriteriaMet never enables live trading.", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<TextBlock>(view), t => string.Equals(t.Text, "Live enabled", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Collecting", ById<StatusBadge>(view, "ReliabilityCampaignState").Label);
            Capture(view, theme, "reliability-criteria-met");

            CheckMinimum(view, window, theme, "reliability");
            ScrollTo(view, ById<Border>(view, "ReliabilityCampaignControls")); Flush(window);
            Capture(view, theme, "reliability-controls-minimum");
            foreach (var textBox in Descendants<TextBox>(view))
            {
                var point = textBox.TranslatePoint(new Point(), view);
                Assert.True(point.X >= -1 && point.X + textBox.ActualWidth <= view.ActualWidth + 1);
            }
            ScrollTo(view, invariants); Flush(window);
            Assert.True(Descendants<DataGrid>(invariants).Single().ActualWidth <= invariants.ActualWidth);
            Capture(view, theme, "reliability-invariants-minimum");
            AssertCommands(view);
            Assert.Equal(0, handler.Requests);
            Assert.Empty(errors.Messages);
        }
        finally { Close(window); }
    });

    private static void AssertActionBindings(PaperReliabilityView view, PaperReliabilityViewModel vm)
    {
        foreach (var action in new[] { "start", "pause", "resume", "evaluate", "complete", "cancel" })
        {
            var button = Assert.Single(Descendants<Button>(view), b => Equals(b.CommandParameter, action));
            Assert.Same(vm.ActionCommand, button.Command);
            Assert.Equal(BindingStatus.Active, BindingOperations.GetBindingExpression(button, Button.CommandProperty)!.Status);
        }
        foreach (var name in new[] { "Campaign name", "Campaign notes" })
        {
            var input = Assert.Single(Descendants<TextBox>(view), t => AutomationProperties.GetName(t) == name);
            Assert.Equal(BindingStatus.Active, BindingOperations.GetBindingExpression(input, TextBox.TextProperty)!.Status);
        }
        Assert.Same(vm.ExportCommand, Assert.Single(Descendants<Button>(view), b => Equals(b.Content, "Export Report")).Command);
        Assert.Same(vm.RefreshCommand, Assert.Single(Descendants<Button>(view), b => Equals(b.Content, "Refresh")).Command);
        Assert.Same(vm.ReadReportCommand, Assert.Single(Descendants<Button>(view), b => Equals(b.Content, "Read selected report")).Command);
    }

    private static PaperReliabilityCampaignResponse Campaign() => new(CampaignId, WorkspaceId,
        Guid.Parse("c02b0000-0000-0000-0000-000000000004"), Guid.Parse("c02b0000-0000-0000-0000-000000000005"),
        "UI-02B paper evidence fixture", "Deterministic isolated observations", At, null, null, "Collecting", 1,
        new string('A', 64), false, false, false, Guid.Parse("c02b0000-0000-0000-0000-000000000006"));

    private static PaperReliabilityReportResponse Report(PaperReliabilityCampaignResponse campaign) => new(
        campaign.Id, campaign.WorkspaceId, campaign.Name, At, At.AddHours(2), campaign.State, 1, campaign.PolicyFingerprint,
        "InsufficientEvidence", false,
        new Dictionary<string, long>
        {
            ["BackendObservedTicks"] = TimeSpan.FromHours(8).Ticks,
            ["MonitoringRunningTicks"] = TimeSpan.FromHours(6).Ticks,
            ["AutomationArmedTicks"] = TimeSpan.FromHours(3).Ticks,
            ["AutomationHealthyTicks"] = TimeSpan.FromHours(2).Ticks,
            ["CandidateInputsObserved"] = 120,
            ["DistinctTriggerInputs"] = 34,
            ["SizingAttempts"] = 27,
            ["AutomaticExecutionsCommitted"] = 3
        },
        [new("AutomationHealthyTicks", TimeSpan.FromHours(4).Ticks, TimeSpan.FromHours(2).Ticks, "NotSatisfied", "MinimumInclusive"), new("CandidateInputsObserved", 100, 120, "Satisfied", "MinimumInclusive")],
        [new("LedgerReconciliationHealthy", 0, 0, "Satisfied", "FixtureLedgerReconciled")],
        [new(GenerationId, "Kalshi", "USD", 9.36m, 10, .64m, 10, .64m, 0, .26m), new(GenerationId, "Polymarket", "USDC", 8.95m, 10, 1.05m, 0, 0, 8.95m, .12m)],
        [], new Dictionary<string, decimal> { ["SelectedQuantityAverage"] = 10.5m },
        "PAPER SIMULATION EVIDENCE ONLY", ["CriteriaMet never enables live trading.", "Fixture evidence excludes real exchange fills and latency."], new string('B', 64));
}

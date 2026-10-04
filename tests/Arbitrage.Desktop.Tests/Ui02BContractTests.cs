using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Arbitrage.Desktop.Controls;
using Arbitrage.Desktop.Services;
using static Arbitrage.Desktop.Tests.Ui02BTestSupport;

namespace Arbitrage.Desktop.Tests;

[Collection("WPF")]
public sealed class Ui02BContractTests(WpfFixture fixture)
{
    [Fact]
    public void Baseline_controls_commands_accessibility_and_column_data_remain_available()
    {
        var repo = RealtimeProcessTests.RepositoryRoot();
        // Captured independently from baseline 159cd96e before changing these views.
        var contract = JsonSerializer.Deserialize<Dictionary<string, Entry[]>>(File.ReadAllText(
            Path.Combine(repo, "tests", "Arbitrage.Desktop.Tests", "Ui02BBindingContract.json")))!;
        Assert.Equal(176, contract.Values.Sum(e => e.Length));
        foreach (var (view, entries) in contract)
        {
            var doc = XDocument.Load(Path.Combine(repo, "src", "Arbitrage.Desktop", "Views", view + ".xaml"));
            foreach (var entry in entries)
            {
                if (entry.Type == "Column")
                {
                    // A state column may become a badge template; its header and actual data path must survive.
                    var path = PathOf(entry.Attributes["ValueBinding"]);
                    Assert.True(doc.Descendants().Any(e => e.Name.LocalName is "DataGridTextColumn" or "DataGridTemplateColumn" or "GridViewColumn"
                        && (string?)e.Attribute("Header") == entry.Attributes["Header"]
                        && ColumnBindings(e, doc).Any(a => a.StartsWith("{Binding", StringComparison.Ordinal) && PathOf(a) == path)),
                        $"Lost {view} column {entry.Attributes["Header"]} / {path}");
                }
                else Assert.True(doc.Descendants().Any(e => e.Name.LocalName == entry.Type && entry.Attributes.All(a => (string?)e.Attribute(a.Key) == a.Value)),
                    $"Lost {view} {entry.Type}: {JsonSerializer.Serialize(entry.Attributes)}");
            }
        }
    }

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public Task Operational_semantic_states_and_disabled_warning_action_remain_truthful(EffectiveTheme theme) => fixture.RunAsync(() =>
    {
        ApplyTheme(theme);
        var resources = new ResourceDictionary { Source = new Uri("/Arbitrage.Desktop;component/Resources/Styles/MarketWorkflow.xaml", UriKind.Relative) };
        foreach (var (label, tone) in new[] { ("Open","Info"), ("PartiallySettled","Warning"), ("Settled","Good"), ("Collecting","Info"), ("Paused","Warning"), ("CriteriaMet","Good"), ("NotSatisfied","Warning"), ("InvariantViolation","Error"), ("Violated","Error"), ("Information","Info"), ("Warning","Warning"), ("Error","Error"), ("Unrecognized","Neutral") })
        {
            var badge = new StatusBadge { Label = label, Style = (Style)resources["WorkflowState"] };
            Assert.Equal(tone, badge.Tone);
        }
        var unknown = new StatusBadge { Label = "Unknown", Style = (Style)resources["WorkflowEvidenceState"] };
        Assert.Equal("Warning", unknown.Tone);
        unknown.Label = "Satisfied"; Assert.Equal("Good", unknown.Tone);
        unknown.Label = "Violated"; Assert.Equal("Error", unknown.Tone);
        var button = new Button { Content = "Pause", Style = (Style)Application.Current.FindResource("WarningButton"), IsEnabled = false };
        var window = Show(new UserControl { Content = button }, 600, 400);
        try
        {
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("DisabledBackground")).Color, ((SolidColorBrush)button.Background).Color);
            button.IsEnabled = true; Flush(window);
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("SemanticBrush.WarningBackground")).Color, ((SolidColorBrush)button.Background).Color);
            Assert.True(button.Focusable);
            Assert.NotNull(button.Template.FindName("KeyboardOutline", button));
        }
        finally { Close(window); }
    });
    private static IEnumerable<string> ColumnBindings(XElement column, XDocument document)
    {
        foreach (var attribute in column.DescendantsAndSelf().Attributes()) yield return attribute.Value;
        var template = (string?)column.Attribute("CellTemplate");
        var key = template is null ? "" : Regex.Match(template, @"^\{StaticResource\s+([^}]+)\}$").Groups[1].Value;
        if (key.Length == 0) yield break;
        // Shared cell templates preserve the same binding without duplicating it per column.
        var resource = document.Descendants().Single(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == key);
        foreach (var attribute in resource.DescendantsAndSelf().Attributes()) yield return attribute.Value;
    }
    private static string PathOf(string value) => Regex.Match(value, @"^\{Binding\s+(?:Path=)?([^,}]+)").Groups[1].Value.Trim();
    private sealed record Entry(string Type, Dictionary<string, string> Attributes);
}

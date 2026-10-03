using System.Windows;
using System.Windows.Controls;

namespace Arbitrage.Desktop.Controls;

public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatusBadge));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(string), typeof(StatusBadge),
        new PropertyMetadata("Neutral", (sender, _) => ((StatusBadge)sender).UpdateTone()));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public StatusBadge() { InitializeComponent(); UpdateTone(); }
    private void UpdateTone()
    {
        if (BadgeBorder is null) return;
        var family = Tone switch { "Good" => "Success", "Info" => "Info", "Warning" => "Warning", "Error" => "Error", _ => "Neutral" };
        BadgeBorder.SetResourceReference(Border.BackgroundProperty, $"SemanticBrush.{family}Background");
        BadgeBorder.SetResourceReference(Border.BorderBrushProperty, $"SemanticBrush.{family}Border");
        BadgeText.SetResourceReference(TextBlock.ForegroundProperty, $"SemanticBrush.{family}Foreground");
    }
}

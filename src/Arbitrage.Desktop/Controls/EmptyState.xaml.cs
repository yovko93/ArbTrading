using System.Windows;
using System.Windows.Controls;

namespace Arbitrage.Desktop.Controls;

public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty PurposeProperty = DependencyProperty.Register(nameof(Purpose), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty DependencyTextProperty = DependencyProperty.Register(nameof(Dependency), typeof(string), typeof(EmptyState));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Purpose { get => (string)GetValue(PurposeProperty); set => SetValue(PurposeProperty, value); }
    public string Dependency { get => (string)GetValue(DependencyTextProperty); set => SetValue(DependencyTextProperty, value); }
    public EmptyState() => InitializeComponent();
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Arbitrage.Desktop.Controls;

// Only bind public identifiers. This control has no backend or credential access.
public class CopyIdentifierButton : Button
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(CopyIdentifierButton), new PropertyMetadata(null, Reset));
    public static readonly DependencyProperty IdentifierNameProperty = DependencyProperty.Register(nameof(IdentifierName), typeof(string), typeof(CopyIdentifierButton), new PropertyMetadata("identifier", Reset));
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string IdentifierName { get => (string)GetValue(IdentifierNameProperty); set => SetValue(IdentifierNameProperty, value); }
    public CopyIdentifierButton()
    {
        SetResourceReference(StyleProperty, "SecondaryButton");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        Reset(this, default);
    }
    private static void Reset(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var button = (CopyIdentifierButton)value;
        button.Content = "Copy";
        button.IsEnabled = !string.IsNullOrWhiteSpace(button.Text);
        AutomationProperties.SetName(button, "Copy " + button.IdentifierName);
        AutomationProperties.SetHelpText(button, "Copies the exact displayed identifier.");
        button.ToolTip = "Copy " + button.IdentifierName;
    }
    protected virtual void WriteClipboard(string text) => Clipboard.SetText(text);
    protected override void OnClick()
    {
        base.OnClick();
        if (string.IsNullOrWhiteSpace(Text)) return;
        try { WriteClipboard(Text); Content = "Copied"; AutomationProperties.SetHelpText(this, "Copied"); }
        catch (ExternalException) { Content = "Retry copy"; AutomationProperties.SetHelpText(this, "Clipboard unavailable. Retry copy."); }
    }
}

using System.Globalization;
using System.Windows.Data;

namespace Arbitrage.Desktop.Controls;

// Presentation only: never changes availability, ownership or execution state.
public sealed class StatusToneConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Tone(value as string);
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var status = values.ElementAtOrDefault(0) as string;
        var management = values.ElementAtOrDefault(1) as string;
        var message = (values.ElementAtOrDefault(2) as string ?? "").ToLowerInvariant();
        var connection = values.ElementAtOrDefault(3) as string;
        if (Tone(status) == "Error" || message.Contains("failed") || message.Contains("failure") || message.Contains("fault")) return "Error";
        if (message.Contains("synchronization is in progress") || message.Contains("ownership is unverified") ||
            management == "ExternalUnmanaged" && status == "Running" || Tone(connection) == "Warning") return "Warning";
        if (message.StartsWith("refreshing")) return "Info";
        return Tone(status);
    }

    public static string Tone(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "running" or "runningmanagedlocal" or "connected" or "ready" or "available" or "healthy" or "withinlimits" or "armed" or "approved" or "satisfied" or "success" => "Good",
        "starting" or "stopping" or "stoprequested" or "synchronizing" or "reconnecting" or "stale" or "warning" or "limited" or "partial" or "runningexternal" or "unknown" or "pending" or "nearedge" or "paused" => "Warning",
        "faulted" or "error" or "denied" or "authorizationdenied" or "authenticationfailed" or "invalid" or "corrupt" or "corrupted" or "killswitchlatched" or "overlimit" or "failed" or "violated" => "Error",
        "local" or "paper" or "refresh" or "loading" or "information" => "Info",
        _ => "Neutral"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

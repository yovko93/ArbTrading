using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Arbitrage.Desktop.Services;

public sealed class WindowsSystemThemeProvider : ISystemThemeProvider, IDisposable
{
    public WindowsSystemThemeProvider()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public bool? ApplicationsUseLightTheme
    {
        get
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null);
                return value is int setting && (setting == 0 || setting == 1) ? setting == 1 : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            { return null; }
        }
    }

    public bool HighContrast => SystemParameters.HighContrast;
    public event EventHandler? Changed;
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SystemParameters.HighContrast)) Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        dispatcher.CheckAccess() ? InvokeNow(action, cancellationToken) : dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;

    private static Task InvokeNow(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}

public sealed class WpfThemePaletteApplier(ResourceDictionary resources) : IThemePaletteApplier
{
    public void Apply(EffectiveTheme theme, bool highContrast)
    {
        var dictionary = highContrast ? HighContrastPalette() : new ResourceDictionary
        {
            Source = new Uri($"/Arbitrage.Desktop;component/Resources/Themes/Colors.{theme}.xaml", UriKind.Relative)
        };
        var old = resources.MergedDictionaries.FirstOrDefault(item => item.Contains("ThemePaletteMarker"));
        var oldBrushes = resources.MergedDictionaries.FirstOrDefault(item => item.Contains("ThemeBrushMarker"));
        if (old is not null) resources.MergedDictionaries.Remove(old);
        if (oldBrushes is not null) resources.MergedDictionaries.Remove(oldBrushes);
        resources.MergedDictionaries.Insert(0, dictionary);
        // A brush's DynamicResource Color is resolved when its dictionary loads. Recreate
        // the semantic brush dictionary so existing controls receive new brush instances.
        resources.MergedDictionaries.Insert(1, new ResourceDictionary
        {
            Source = new Uri("/Arbitrage.Desktop;component/Resources/Themes/Brushes.xaml", UriKind.Relative)
        });
    }

    private static ResourceDictionary HighContrastPalette()
    {
        // Read OS colors on every notification so Windows High Contrast variants remain authoritative.
        return new ResourceDictionary
        {
            ["ThemePaletteMarker"] = "HighContrast",
            ["Color.ApplicationBackground"] = SystemColors.WindowColor,
            ["Color.SurfaceBackground"] = SystemColors.WindowColor,
            ["Color.ElevatedSurfaceBackground"] = SystemColors.ControlColor,
            ["Color.PrimaryText"] = SystemColors.WindowTextColor,
            ["Color.SecondaryText"] = SystemColors.WindowTextColor,
            ["Color.DefaultBorder"] = SystemColors.WindowTextColor,
            ["Color.Accent"] = SystemColors.HighlightColor,
            ["Color.AccentMuted"] = SystemColors.HighlightColor,
            ["Color.FocusIndicator"] = SystemColors.HighlightColor,
            ["Color.Success"] = SystemColors.WindowTextColor,
            ["Color.Warning"] = SystemColors.WindowTextColor,
            ["Color.Error"] = SystemColors.WindowTextColor,
            ["Color.SelectionBackground"] = SystemColors.HighlightColor,
            ["Color.SelectionText"] = SystemColors.HighlightTextColor,
            ["Color.DisabledBackground"] = SystemColors.ControlColor,
            ["Color.DisabledText"] = SystemColors.GrayTextColor
        };
    }
}

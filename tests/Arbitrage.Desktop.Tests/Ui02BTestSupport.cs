using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arbitrage.Desktop.Services;
using Arbitrage.LocalTransport;

namespace Arbitrage.Desktop.Tests;

internal static class Ui02BTestSupport
{
    public static void ApplyTheme(EffectiveTheme theme) => new WpfThemePaletteApplier(Application.Current.Resources).Apply(theme, false);
    public static Window Show(UserControl view, double width = 1160, double height = 1040)
    {
        view.SetResourceReference(Control.BackgroundProperty, "ApplicationBackground");
        var window = new Window { Content = view, Width = width, Height = height, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        window.Show(); Flush(window); return window;
    }
    public static void Close(Window window)
    {
        window.Close();
        // Unloaded handlers must finish before their fixture view models are disposed.
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    public static void Flush(Window window)
    {
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    public static T ById<T>(DependencyObject root, string id) where T : DependencyObject => Assert.Single(Descendants<T>(root), e => AutomationProperties.GetAutomationId(e) == id);
    public static void ScrollTo(DependencyObject view, FrameworkElement target)
    {
        var scroll = Descendants<ScrollViewer>(view).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + target.TranslatePoint(new Point(), scroll).Y);
    }
    public static void Capture(FrameworkElement view, EffectiveTheme theme, string name)
    {
        if (Environment.GetEnvironmentVariable("ARBITRAGE_UI_CAPTURE_DIRECTORY") is not { } path) return;
        Directory.CreateDirectory(path);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, $"UI02B-{theme}-{name}.png")); encoder.Save(output);
    }
    public static void AssertCommands(DependencyObject root)
    {
        foreach (var button in Descendants<Button>(root))
        {
            var binding = BindingOperations.GetBindingExpression(button, Button.CommandProperty);
            if (binding is null) continue;
            Assert.Equal(BindingStatus.Active, binding.Status);
            Assert.NotNull(button.Command);
            if (!button.Command.CanExecute(button.CommandParameter)) Assert.False(button.IsEnabled);
        }
    }
    public static void CheckMinimum(UserControl view, Window window, EffectiveTheme theme, string name)
    {
        window.Width = 520; window.Height = 430; Flush(window);
        foreach (var scroll in Descendants<ScrollViewer>(view).Where(s => s.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled))
            Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"Clipped {name}: {scroll.ExtentWidth} > {scroll.ViewportWidth}");
        foreach (var button in Descendants<Button>(view).Where(b => b.IsVisible && b.ActualWidth > 0))
        {
            var point = button.TranslatePoint(new Point(), view);
            Assert.True(point.X >= -1 && point.X + button.ActualWidth <= view.ActualWidth + 1, $"Action {button.Content} exceeds minimum width in {name}");
        }
        foreach (var selector in Descendants<ComboBox>(view).Where(c => c.IsVisible))
        {
            selector.IsDropDownOpen = true; Flush(window);
            Assert.NotNull(selector.Template.FindName("PART_Popup", selector));
            selector.IsDropDownOpen = false; Flush(window);
        }
        Descendants<ScrollViewer>(view).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto).ScrollToTop();
        Flush(window); Capture(view, theme, name + "-minimum");
    }
    internal sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public BindingErrors() => PresentationTraceSources.DataBindingSource.Listeners.Add(this);
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message?.Contains("Error:", StringComparison.Ordinal) == true) Messages.Add(message); }
        protected override void Dispose(bool disposing) { PresentationTraceSources.DataBindingSource.Listeners.Remove(this); base.Dispose(disposing); }
    }
    internal sealed class RejectHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new InvalidOperationException("No runtime or network access in UI-02B visual fixtures."); }
    }
    internal sealed class MissingConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException("Isolated UI-02B visual fixture.");
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    internal sealed class StubTheme(EffectiveTheme theme) : IThemeService
    {
        public ThemePreference Preference => theme == EffectiveTheme.Dark ? ThemePreference.Dark : ThemePreference.Light;
        public EffectiveTheme EffectiveTheme => theme;
        public bool HighContrast => false;
        public bool IsSaved => true;
        public string? PersistenceNotice => null;
        public event EventHandler? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SetPreferenceAsync(ThemePreference preference, CancellationToken ct = default) => Task.CompletedTask;
    }
}

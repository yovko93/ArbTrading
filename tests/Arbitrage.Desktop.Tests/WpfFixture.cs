using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Arbitrage.Desktop.Tests;

[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture> { }

public sealed class WpfFixture : IAsyncLifetime
{
    private sealed class TestApp : Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    private readonly string root = Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01B-WPF", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("WPF test profile already exists.");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("Local__DataDirectory", Path.Combine(root, "backend"));
        Environment.SetEnvironmentVariable("Local__RuntimeDirectory", Path.Combine(root, "runtime"));
        Environment.SetEnvironmentVariable("ARBITRAGE_RUNTIME_DIRECTORY", Path.Combine(root, "runtime"));
        Environment.SetEnvironmentVariable("ARBITRAGE_DESKTOP_DIRECTORY", Path.Combine(root, "desktop"));
        thread = new Thread(() =>
        {
            try
            {
                var application = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.Add("BooleanToVisibility", new BooleanToVisibilityConverter());
                foreach (var source in new[]
                {
                    "Resources/Themes/Colors.Light.xaml", "Resources/Themes/Brushes.xaml",
                    "Resources/Styles/Typography.xaml", "Resources/Styles/Layout.xaml", "Resources/Styles/Controls.xaml"
                })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/Arbitrage.Desktop;component/{source}", UriKind.Relative)
                    });
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true, Name = "Phase 01B WPF tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await ready.Task;
    }

    public async Task RunAsync(Action action)
    {
        var dispatcher = await ready.Task;
        await dispatcher.InvokeAsync(action).Task;
    }

    public async Task DisposeAsync()
    {
        if (ready.Task.IsCompletedSuccessfully)
        {
            var dispatcher = await ready.Task;
            await dispatcher.InvokeAsync(() =>
            {
                foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
                Application.Current.Shutdown();
            }).Task;
            thread?.Join(TimeSpan.FromSeconds(5));
        }
        var full = Path.GetFullPath(root);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ArbitrageTrading-01B-WPF")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected WPF test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

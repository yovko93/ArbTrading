using System.IO;
using System.Net.Http;
using System.Windows;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.Desktop.Views;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Arbitrage.Desktop;

public partial class App : System.Windows.Application
{
    private ServiceProvider? services;
    private readonly CancellationTokenSource lifetime = new();
    private Serilog.Core.Logger? logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var desktopDirectory = DesktopPaths.Directory;
            var runtimeDirectory = DesktopPaths.RuntimeDirectory;
            if (string.Equals(desktopDirectory, runtimeDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Desktop preferences and runtime metadata need separate directories.");
            ProtectedStorage.CreatePrivateDirectory(desktopDirectory);
            logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console()
                .WriteTo.File(Path.Combine(desktopDirectory, "logs", "desktop-.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true, shared: true).CreateLogger();
            var collection = new ServiceCollection();
            collection.AddLogging(b => b.ClearProviders().AddSerilog(logger));
            collection.AddSingleton(new DesktopPreferencesStore(desktopDirectory));
            collection.AddSingleton<IDesktopPreferencesStore>(s => s.GetRequiredService<DesktopPreferencesStore>());
            collection.AddSingleton<ISystemThemeProvider, WindowsSystemThemeProvider>();
            collection.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(Dispatcher));
            collection.AddSingleton<IThemePaletteApplier>(new WpfThemePaletteApplier(Resources));
            collection.AddSingleton<IThemeService, ThemeService>();
            collection.AddSingleton<DesktopDiagnostics>();
            collection.AddSingleton<ThemeSelectionViewModel>();
            collection.AddSingleton<IRealtimeDelay, RealtimeDelay>();
            collection.AddSingleton(RealtimeOptions.FromEnvironment());
            collection.AddSingleton<RealtimeSession>();
            collection.AddSingleton(LocalBackendLaunchOptions.FromEnvironment());
            collection.AddSingleton<ILocalBackendController, LocalBackendController>();
            collection.AddSingleton<BackendProcessViewModel>();
            collection.AddSingleton<MarketExplorerViewModel>();
            collection.AddSingleton<RelationshipsViewModel>(s => new(s.GetRequiredService<MainViewModel>(), s.GetRequiredService<BackendClient>())
            { Confirm = message => MessageBox.Show(message, "Relationship review", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes });
            collection.AddSingleton<KalshiCredentialsViewModel>(s => new(s.GetRequiredService<MainViewModel>(), s.GetRequiredService<BackendClient>())
            {
                SelectFile = () => { var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select Kalshi RSA private key", Filter = "Key files|*.pem;*.key|All files|*.*", CheckFileExists = true }; return dialog.ShowDialog() == true ? dialog.FileName : null; },
                Confirm = message => MessageBox.Show(message, "Kalshi credentials", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            });
            collection.AddSingleton<ShellViewModel>(s => new ShellViewModel(s.GetRequiredService<MainViewModel>(),
                s.GetRequiredService<ThemeSelectionViewModel>(), s.GetRequiredService<DesktopDiagnostics>(),
                s.GetRequiredService<BackendProcessViewModel>(), s.GetRequiredService<MarketExplorerViewModel>(), s.GetRequiredService<KalshiCredentialsViewModel>(), s.GetRequiredService<RelationshipsViewModel>()));
            collection.AddSingleton<ILocalConnectionFile>(new ProtectedLocalConnectionFile(runtimeDirectory));
            collection.AddHttpClient<BackendClient>(client => client.Timeout = TimeSpan.FromSeconds(10))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
                .RemoveAllLoggers();
            collection.AddSingleton<MainViewModel>();
            collection.AddSingleton<MainWindow>();
            services = collection.BuildServiceProvider();
            var diagnostics = services.GetRequiredService<DesktopDiagnostics>();
            await services.GetRequiredService<IThemeService>().InitializeAsync(lifetime.Token);
            diagnostics.Record("Information", "Desktop appearance preference loaded.");
            var window = services.GetRequiredService<MainWindow>();
            MainWindow = window;
            var state = services.GetRequiredService<MainViewModel>();
            var process = services.GetRequiredService<BackendProcessViewModel>();
            state.RefreshRequested = process.RefreshAsync;
            process.ConfirmStop = () => MessageBox.Show(window,
                "Stop this shared local backend? All connected desktop clients using this instance will lose backend access.",
                "Stop Local Backend", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            window.Show();
            services.GetRequiredService<RealtimeSession>().Start();
            _ = ObserveLocalBackendAsync(process);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger?.Error("Desktop startup failed: {ErrorType}", exception.GetType().Name);
            MessageBox.Show("Desktop storage or configuration is unavailable. Check your per-user application data permissions.", "Arbitrage Trading");
            Shutdown(1);
        }
    }

    private async Task ObserveLocalBackendAsync(BackendProcessViewModel process)
    {
        try { await process.InitializeAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { logger?.Warning("Local backend observation failed: {ErrorType}", exception.GetType().Name); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel();
        services?.GetService<RealtimeSession>()?.Dispose();
        services?.GetService<MainViewModel>()?.Dispose();
        services?.GetService<MarketExplorerViewModel>()?.Dispose();
        services?.GetService<RelationshipsViewModel>()?.Dispose();
        services?.GetService<KalshiCredentialsViewModel>()?.Dispose();
        services?.Dispose();
        logger?.Dispose();
        lifetime.Dispose();
        base.OnExit(e);
    }
}

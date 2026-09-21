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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var desktopDirectory = Path.Combine(LocalPaths.Root, "desktop");
            ProtectedStorage.CreatePrivateDirectory(desktopDirectory);
            logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console()
                .WriteTo.File(Path.Combine(desktopDirectory, "logs", "desktop-.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true, shared: true).CreateLogger();
            var collection = new ServiceCollection();
            collection.AddLogging(b => b.ClearProviders().AddSerilog(logger));
            collection.AddSingleton<ILocalConnectionFile>(new ProtectedLocalConnectionFile(
                Environment.GetEnvironmentVariable("ARBITRAGE_RUNTIME_DIRECTORY") ?? LocalPaths.Runtime));
            collection.AddHttpClient<BackendClient>(client => client.Timeout = TimeSpan.FromSeconds(10))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
                .RemoveAllLoggers();
            collection.AddSingleton<MainViewModel>();
            collection.AddSingleton<MainWindow>();
            services = collection.BuildServiceProvider();
            var window = services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            _ = services.GetRequiredService<MainViewModel>().InitializeAsync(lifetime.Token);
        }
        catch (Exception exception)
        {
            logger?.Error("Desktop startup failed: {ErrorType}", exception.GetType().Name);
            MessageBox.Show("Desktop storage or configuration is unavailable. Check your per-user application data permissions.", "Arbitrage Trading");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel();
        services?.GetService<MainViewModel>()?.Dispose();
        services?.Dispose();
        logger?.Dispose();
        lifetime.Dispose();
        base.OnExit(e);
    }
}

using System.IO;
using System.Net.Http;
using Arbitrage.Desktop.Services;
using Arbitrage.Desktop.ViewModels;
using Arbitrage.LocalTransport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Arbitrage.Desktop.Tests;

public sealed class DesktopLifetimeTests
{
    [Fact]
    public void Shutdown_disposes_provider_and_representative_services_once_then_external_logger()
    {
        var order = new List<string>();
        using var http = new HttpClient();
        var client = new BackendClient(http, new MissingConnection());
        var diagnostics = new DesktopDiagnostics();
        var sink = new CountingSink(order);
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var lifetime = new DesktopLifetime { Logger = logger };
        lifetime.Token.Register(() => order.Add("cancel"));
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.ClearProviders().AddSerilog(logger, dispose: false));
        collection.AddSingleton(_ => new Tracked<MainViewModel>(new(client, NullLogger<MainViewModel>.Instance), "main", order));
        collection.AddSingleton(_ => new ThemeService(new Preferences(), new SystemTheme(), new Palette(), new InlineDispatcher()));
        collection.AddSingleton(provider => new ThemeSelectionViewModel(provider.GetRequiredService<ThemeService>(), diagnostics));
        collection.AddSingleton(provider => new Tracked<RealtimeSession>(new(client,
            provider.GetRequiredService<Tracked<MainViewModel>>().Value, diagnostics, new InlineDispatcher(), new RealtimeDelay()), "realtime", order));
        collection.AddSingleton(provider => new Tracked<ShellViewModel>(new(provider.GetRequiredService<Tracked<MainViewModel>>().Value,
            provider.GetRequiredService<ThemeSelectionViewModel>(), diagnostics), "shell", order));
        var provider = collection.BuildServiceProvider();
        var ownedProvider = new Tracked<ServiceProvider>(provider, "provider", order);
        lifetime.ServiceProvider = ownedProvider;
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("shutdown-test").LogInformation("Isolated shutdown test.");
        var main = provider.GetRequiredService<Tracked<MainViewModel>>();
        var realtime = provider.GetRequiredService<Tracked<RealtimeSession>>();
        var shell = provider.GetRequiredService<Tracked<ShellViewModel>>();

        lifetime.Dispose(); lifetime.Dispose();

        Assert.Equal(1, ownedProvider.Disposals);
        Assert.Equal(1, shell.Disposals);
        Assert.Equal(1, realtime.Disposals);
        Assert.Equal(1, main.Disposals);
        Assert.Equal(1, sink.Disposals);
        Assert.Equal("cancel", order[0]);
        Assert.Equal("logger", order[^1]);
        Assert.Equal(new[] { "cancel", "provider", "shell", "realtime", "main", "logger" }, order);
    }

    [Fact]
    public void Logging_provider_does_not_own_the_external_logger()
    {
        var sink = new CountingSink([]);
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.AddSerilog(logger, dispose: false));
        var provider = collection.BuildServiceProvider();
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("ownership").LogInformation("Isolated ownership test.");
        provider.Dispose();
        Assert.Equal(0, sink.Disposals);
        logger.Dispose();
        Assert.Equal(1, sink.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_startup_cleans_constructed_resources_without_masking_original_failure(bool providerConstructed)
    {
        var sink = new CountingSink([]);
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var lifetime = new DesktopLifetime { Logger = logger };
        Tracked<EmptyDisposable>? singleton = null;
        if (providerConstructed)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton(_ => new Tracked<EmptyDisposable>(new(), "partial", []));
            var provider = collection.BuildServiceProvider();
            lifetime.ServiceProvider = provider;
            singleton = provider.GetRequiredService<Tracked<EmptyDisposable>>();
        }
        var error = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            try { throw new InvalidOperationException("Original startup failure."); }
            finally { lifetime.Dispose(); }
        }));
        lifetime.Dispose();
        Assert.Equal("Original startup failure.", error.Message);
        Assert.Equal(1, sink.Disposals);
        if (singleton is not null) Assert.Equal(1, singleton.Disposals);
    }

    [Fact]
    public void Early_exit_with_no_services_or_logger_is_safe_and_repeatable()
    {
        var lifetime = new DesktopLifetime();
        var token = lifetime.Token;
        lifetime.Dispose(); lifetime.Dispose();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Main_and_realtime_allow_defensive_duplicate_disposal()
    {
        using var http = new HttpClient();
        var backend = new BackendClient(http, new MissingConnection());
        var main = new MainViewModel(backend, NullLogger<MainViewModel>.Instance);
        var realtime = new RealtimeSession(backend, main, new(), new InlineDispatcher(), new RealtimeDelay());
        realtime.Start();
        realtime.Dispose(); realtime.Dispose();
        await realtime.StopAsync();
        main.Dispose(); main.Dispose();
    }

    private sealed class Tracked<T>(T value, string name, List<string> order) : IDisposable where T : IDisposable
    {
        public T Value => value;
        public int Disposals { get; private set; }
        public void Dispose() { Disposals++; order.Add(name); value.Dispose(); }
    }
    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    private sealed class CountingSink(List<string> order) : ILogEventSink, IDisposable
    {
        public int Disposals { get; private set; }
        public void Emit(LogEvent logEvent) { }
        public void Dispose() { Disposals++; order.Add("logger"); }
    }
    private sealed class MissingConnection : ILocalConnectionFile
    {
        public Task<LocalConnection> ReadAsync(CancellationToken cancellationToken) => throw new FileNotFoundException("No isolated backend.");
        public Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
    private sealed class Preferences : IDesktopPreferencesStore
    {
        public Task<PreferencesLoadResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new PreferencesLoadResult(ThemePreference.System));
        public Task SaveAsync(ThemePreference preference, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class SystemTheme : ISystemThemeProvider
    {
        public bool? ApplicationsUseLightTheme => true;
        public bool HighContrast => false;
        public event EventHandler? Changed { add { } remove { } }
    }
    private sealed class Palette : IThemePaletteApplier { public void Apply(EffectiveTheme theme, bool highContrast) { } }
}

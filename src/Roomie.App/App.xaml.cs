using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.App.ViewModels;
using Roomie.App.Views;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Storage;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Roomie.App;

public partial class App : Application
{
    private IHost? _host;
    private ILogger _logger = NullLogger.Instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseSerilog((_, logger) => logger
                .MinimumLevel.Information()
                .WriteTo.File(
                    paths.LogFile,
                    formatProvider: CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true))
            .ConfigureServices(services =>
            {
                services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
                services.AddSingleton(paths);
                services.AddSingleton(_ => new DeviceIdentityStore(paths.Identity));
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        await _host.StartAsync();

        LogStarted(_logger, paths.Root);

        if (!LoadIdentity(_host.Services.GetRequiredService<DeviceIdentityStore>()))
        {
            Shutdown(1);
            return;
        }

        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private bool LoadIdentity(DeviceIdentityStore store)
    {
        IdentityLoadResult result;
        try
        {
            result = store.LoadOrCreate(Environment.UserName);
        }
        catch (Exception ex) when (ex is IdentityStoreException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Never regenerate on these errors: that would silently break every pairing (architecture.md §1.1).
            LogIdentityLoadFailed(_logger, ex);
            MessageBox.Show(
                $"Roomie couldn't load this computer's identity.\n\n{ex.Message}\n\nNothing was changed. Check the log for details, then try again.",
                "Roomie",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        var identity = result.Identity;
        LogIdentityLoaded(_logger, result.Outcome, identity.DisplayId, identity.CertificateFingerprint);

        if (result.Outcome == IdentityLoadOutcome.RegeneratedForeignIdentity)
        {
            LogIdentityRegenerated(_logger, result.OrphanedDirectory);
            MessageBox.Show(
                "The saved identity was copied from another computer or Windows account, so Roomie created a new identity for this one.\n\n"
                + "Devices you paired with before will show an identity-change warning and must be paired again.",
                "Roomie",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return true;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandledUiException(_logger, e.Exception);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Roomie started. Data directory: {DataDirectory}")]
    private static partial void LogStarted(ILogger logger, string dataDirectory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device identity {Outcome}: {DisplayId} ({Fingerprint})")]
    private static partial void LogIdentityLoaded(ILogger logger, IdentityLoadOutcome outcome, string displayId, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored identity could not be decrypted and was moved to {OrphanedDirectory}; a new identity was generated")]
    private static partial void LogIdentityRegenerated(ILogger logger, string? orphanedDirectory);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Device identity could not be loaded")]
    private static partial void LogIdentityLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled UI exception")]
    private static partial void LogUnhandledUiException(ILogger logger, Exception exception);
}

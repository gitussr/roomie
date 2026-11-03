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
using Roomie.Core.Configuration;
using Roomie.Core.Discovery;
using Roomie.Core.Files;
using Roomie.Core.Groups;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Messaging;
using Roomie.Infrastructure.Storage;
using Roomie.Infrastructure.Trust;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Files;
using Roomie.Network.Groups;
using Roomie.Network.Messaging;
using Roomie.Network.Pairing;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Roomie.App;

public partial class App : Application
{
    internal static string AppVersion { get; } =
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private IHost? _host;
    private ILogger _logger = NullLogger.Instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();

        JsonTrustStore trust;
        try
        {
            trust = new JsonTrustStore(paths.TrustFile);
        }
        catch (Exception ex) when (ex is TrustStoreException or IOException or UnauthorizedAccessException)
        {
            // Never start with an empty trust list in place of one that couldn't be read: the next pairing
            // would overwrite the file and silently drop every paired device.
            MessageBox.Show(
                $"Roomie couldn't read the list of paired devices.\n\n{ex.Message}\n\nFile: {paths.TrustFile}\n\nNothing was changed.",
                "Roomie",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

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
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(_ => new DeviceIdentityStore(paths.Identity));
                services.AddSingleton<ILocalIdentityProvider>(sp => sp.GetRequiredService<DeviceIdentityStore>());

                services.AddSingleton(new DiscoveryOptions { AppVersion = AppVersion });
                services.AddSingleton(_ => new PeerRegistry(NetworkDefaults.OfflineTimeout));
                services.AddSingleton<ILanInterfaceProvider, SystemLanInterfaceProvider>();
                services.AddSingleton<IDiscoveryChannelFactory, UdpDiscoveryChannelFactory>();
                services.AddSingleton<DiscoveryService>();
                services.AddHostedService(sp => sp.GetRequiredService<DiscoveryService>());

                services.AddSingleton<ITlsCredentialProvider>(sp => sp.GetRequiredService<DeviceIdentityStore>());
                services.AddSingleton<IDeviceSigner>(sp => sp.GetRequiredService<DeviceIdentityStore>());
                services.AddSingleton<ITrustStore>(trust);
                services.AddSingleton(new ConnectionOptions { AppVersion = AppVersion, Capabilities = [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.FileTransfer] });
                services.AddSingleton<ConnectionManager>();
                services.AddHostedService(sp => sp.GetRequiredService<ConnectionManager>());

                services.AddSingleton(new PairingOptions());
                services.AddSingleton<PairingService>();

                services.AddSingleton(sp => new SqliteMessageStore(paths.DatabaseFile, sp.GetRequiredService<ILogger<SqliteMessageStore>>()));
                services.AddSingleton<IMessageStore>(sp => sp.GetRequiredService<SqliteMessageStore>());
                services.AddSingleton<IGroupStore>(sp => sp.GetRequiredService<SqliteMessageStore>());
                services.AddSingleton<GroupService>();
                services.AddSingleton(new MessagingOptions());
                services.AddSingleton<MessagingService>();
                services.AddHostedService(sp => sp.GetRequiredService<MessagingService>());

                services.AddSingleton<IFileTransferStore>(sp => sp.GetRequiredService<SqliteMessageStore>());
                services.AddSingleton(new FileTransferOptions { DownloadsDirectory = paths.Downloads, IncomingDirectory = paths.Incoming });
                services.AddSingleton<FileTransferService>();
                services.AddHostedService(sp => sp.GetRequiredService<FileTransferService>());

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        LogStarted(_logger, paths.Root);

        // The identity must be loaded before the host starts: discovery announces it.
        if (!LoadIdentity(_host.Services.GetRequiredService<DeviceIdentityStore>()))
        {
            Shutdown(1);
            return;
        }

        if (!OpenMessageStore(_host.Services, paths))
        {
            Shutdown(1);
            return;
        }

        // Services claim their message types when created, so create them before sessions can open.
        _host.Services.GetRequiredService<PairingService>();
        _host.Services.GetRequiredService<MessagingService>();
        _host.Services.GetRequiredService<FileTransferService>();

        await _host.StartAsync();

        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is { } host)
        {
            // This has to finish before returning, because the process ends soon after: nothing awaited here
            // would ever resume. It waits on a pool thread, so no service can need the thread that is blocked.
            // Disposing closes the database and removes the TLS key from the user's key store.
            try
            {
                Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
            {
                LogStopFailed(_logger, ex);
            }

            host.Dispose();
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

    private bool OpenMessageStore(IServiceProvider services, AppPaths paths)
    {
        try
        {
            services.GetRequiredService<SqliteMessageStore>();
            return true;
        }
        catch (MessageStoreException ex)
        {
            // Never start over with an empty database: the messages in this one may still be recoverable.
            LogMessageStoreFailed(_logger, ex);
            MessageBox.Show(
                $"Roomie couldn't open your saved messages.\n\n{ex.Message}\n\nFile: {paths.DatabaseFile}\n\nNothing was changed.",
                "Roomie",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
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

    [LoggerMessage(Level = LogLevel.Critical, Message = "The message database could not be opened")]
    private static partial void LogMessageStoreFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Roomie didn't stop cleanly")]
    private static partial void LogStopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled UI exception")]
    private static partial void LogUnhandledUiException(ILogger logger, Exception exception);
}

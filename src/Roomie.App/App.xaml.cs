using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Roomie.App.Services;
using Roomie.App.ViewModels;
using Roomie.App.Views;
using Roomie.Core.Configuration;
using Roomie.Core.Discovery;
using Roomie.Core.Files;
using Roomie.Core.Groups;
using Roomie.Core.Identity;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;
using Roomie.Core.Profiles;
using Roomie.Core.Trust;
using Roomie.Infrastructure.Identity;
using Roomie.Infrastructure.Messaging;
using Roomie.Infrastructure.Profiles;
using Roomie.Infrastructure.Storage;
using Roomie.Infrastructure.Trust;
using Roomie.Network.Connections;
using Roomie.Network.Discovery;
using Roomie.Network.Files;
using Roomie.Network.Groups;
using Roomie.Network.Messaging;
using Roomie.Network.Pairing;
using Roomie.Network.Profiles;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Roomie.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "An Application ends in OnExit, which disposes these.")]
public partial class App : Application
{
    internal static string AppVersion { get; } =
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private IHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private TrayIcon? _tray;
    private Mutex? _singleInstance;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showRequestWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.CreateDefault();
        if (!TakeSingleInstance(paths))
        {
            Shutdown(0);
            return;
        }

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

        var settings = new JsonSettingsStore(paths.SettingsFile);
        var theme = new ThemeService(this);
        theme.Apply(settings.Current.Theme);

        // The command line is Roomie's own, so nothing on it is host configuration.
        _host = Host.CreateDefaultBuilder([])
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
                services.AddSingleton(settings);
                services.AddSingleton(theme);
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
                services.AddSingleton(new ConnectionOptions { AppVersion = AppVersion, Capabilities = [RmpCapabilities.Text, RmpCapabilities.Groups, RmpCapabilities.FileTransfer, RmpCapabilities.Profile] });
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
                services.AddSingleton(new FileTransferOptions { DownloadsDirectory = paths.Downloads, IncomingDirectory = paths.Incoming, ReceiveFolder = settings.Current.ReceiveFolder ?? WindowsFolders.Downloads() });
                services.AddSingleton<FileTransferService>();
                services.AddHostedService(sp => sp.GetRequiredService<FileTransferService>());

                services.AddSingleton<IAvatarStore>(_ => new FileAvatarStore(paths.Avatars));
                services.AddSingleton<ProfileService>();
                services.AddSingleton<AvatarCatalog>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<SoundService>();
                services.AddSingleton<NotificationCenter>();
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
        _host.Services.GetRequiredService<ProfileService>();

        await _host.StartAsync();

        // A portable copy may have moved since this was turned on.
        if (settings.Current.StartWithWindows)
        {
            StartupRegistration.Apply(true);
        }

        Motion.WatchButtons();
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        StartTray(window, _host.Services);
        ListenForShowRequests(window);

        // Started by Windows at sign-in: stay in the notification area until wanted.
        if (!e.Args.Contains(StartupRegistration.BackgroundArgument))
        {
            window.PlaySplash();
            window.Show();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnSessionEnding(e);
        ExitRoomie();
    }

    private void StartTray(MainWindow window, IServiceProvider services)
    {
        var tray = new TrayIcon();
        _tray = tray;
        var main = services.GetRequiredService<MainViewModel>();

        tray.OpenRequested += (_, _) => window.ShowFromTray();
        tray.ExitRequested += (_, _) => ExitRoomie();
        tray.NotificationOpened += (_, target) =>
        {
            main.Open(target);
            window.ShowFromTray();
        };

        window.HiddenToTrayFirstTime += (_, _) => tray.Notify("Roomie is still running", "Messages and files will keep arriving. Use this icon to open or exit Roomie.", null);

        var notifications = services.GetRequiredService<NotificationCenter>();
        notifications.Notify += (_, shown) => tray.Notify(shown.Title, shown.Body, shown.Target);
        notifications.AttentionNeeded += (_, _) => window.Flash();

        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WindowTitle))
            {
                tray.SetTooltip(main.WindowTitle);
            }
        };
    }

    private void ExitRoomie()
    {
        if (MainWindow is MainWindow window)
        {
            window.IsExiting = true;
            window.Close();
        }

        // Also when the window was never shown, because Roomie started in the background.
        Shutdown();
    }

    /// <summary>
    /// Only one Roomie can own a data folder and its ports. A second launch asks the first to show itself and leaves,
    /// which is what someone who can't see the window because it's in the notification area wants anyway.
    /// </summary>
    private bool TakeSingleInstance(AppPaths paths)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..16];
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Roomie-show-{key}");
        _singleInstance = new Mutex(true, $@"Local\Roomie-{key}", out var isFirst);
        if (!isFirst)
        {
            // Showing the other copy would look as if this version had opened, without what's new in it.
            if (FindOtherRunningVersion() is { } other && other != AppVersion)
            {
                MessageBox.Show(
                    $"Roomie {other} is already running, so this copy ({AppVersion}) can't open.\n\nRight-click the Roomie icon in the notification area, choose Exit Roomie, then open this copy again.",
                    "Roomie",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                _showRequest.Set();
            }

            _showRequest.Dispose();
            _showRequest = null;
            _singleInstance.Dispose();
            _singleInstance = null;
        }

        return isFirst;
    }

    /// <summary>The version of the other Roomie running, or null if it can't be told, in which case it's treated as the same.</summary>
    private static string? FindOtherRunningVersion()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && process.MainModule?.FileVersionInfo.ProductVersion is { } version)
                    {
                        return version.Split('+')[0];
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // It may have just exited, or it can't be read. Neither is worth a message.
                }
            }
        }

        return null;
    }

    private void ListenForShowRequests(MainWindow window)
    {
        if (_showRequest is { } request)
        {
            _showRequestWait = ThreadPool.RegisterWaitForSingleObject(
                request,
                (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showRequestWait?.Unregister(null);
        _showRequest?.Dispose();

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

        // Last, so a new Roomie can't start while this one still has the database open.
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
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

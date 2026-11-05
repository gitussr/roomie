using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Roomie.Core.Notifications;
using Forms = System.Windows.Forms;

namespace Roomie.App.Services;

/// <summary>What opening a notification should show.</summary>
public sealed record NotificationTarget(Guid? PeerId = null, string? GroupId = null, bool ShowTransfers = false);

/// <summary>
/// The notification-area icon. On Windows 10 and 11 its balloon tips appear as ordinary Windows notifications, and
/// unlike toast APIs they work without the app being installed, so they work in the portable build too.
/// It has its own thread, because creating it once froze a PC's window for over a minute. Calls here return at once,
/// and take effect when the icon is ready. Its events are raised on the UI thread.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int NotificationMilliseconds = 5000;

    /// <summary>Windows refuses a longer tooltip.</summary>
    private const int MaxTooltipLength = 63;

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<SynchronizationContext> _ready = new();
    private NotificationTarget? _lastTarget;
    private Action? _removeIcon;

    /// <param name="dispatcher">The UI thread's, which is where the events are raised.</param>
    /// <param name="logger">Records how far the icon got, since nothing on screen would show it.</param>
    public TrayIcon(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;

        // Read here, because application resources are simplest to reach from the UI thread.
        var resource = System.Windows.Application.GetResourceStream(new Uri("/Assets/roomie.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("The icon is missing from the application.");
        byte[] icon;
        using (var stream = resource.Stream)
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            icon = copy.ToArray();
        }

        // Nothing named from Windows Forms runs before this thread does, so even loading it can't hold up the window.
        _thread = new Thread(() => Run(icon)) { IsBackground = true, Name = "Notification icon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    /// <summary>The user clicked a notification. Windows says only that the latest one was clicked.</summary>
    public event EventHandler<NotificationTarget?>? NotificationOpened;

    private event Action<string>? TooltipRequested;

    private event Action<string, string>? BalloonRequested;

    public void SetTooltip(string text)
    {
        var clean = NotificationText.Clean(text, MaxTooltipLength);
        OnIconThread(() => TooltipRequested?.Invoke(clean));
    }

    public void Notify(string title, string body, NotificationTarget? target)
    {
        _lastTarget = target;
        var cleanTitle = NotificationText.Clean(title, NotificationText.MaxTitleLength);

        // Windows refuses empty text.
        var cleanBody = NotificationText.Clean(body, NotificationText.MaxBodyLength) is { Length: > 0 } text ? text : " ";
        OnIconThread(() => BalloonRequested?.Invoke(cleanTitle, cleanBody));
    }

    public void Dispose()
    {
        OnIconThread(() => _removeIcon?.Invoke());

        // Long enough to take the icon away normally. If it's stuck, this mustn't hold up exiting too.
        _thread.Join(ExitWait);
    }

    /// <summary>In order, once the icon exists. If it never does, Roomie still runs without one.</summary>
    private void OnIconThread(Action action) =>
        _ready.Task.ContinueWith(
            ready => ready.Result.Post(_ => action(), null),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

    private void Run(byte[] iconBytes)
    {
        try
        {
            LogIconStep(_logger, "starting");
            using var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Roomie", null, (_, _) => OnUi(() => OpenRequested?.Invoke(this, EventArgs.Empty)));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit Roomie", null, (_, _) => OnUi(() => ExitRequested?.Invoke(this, EventArgs.Empty)));
            LogIconStep(_logger, "menu built");

            using var stream = new MemoryStream(iconBytes);
            using var image = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            using var icon = new Forms.NotifyIcon { Icon = image, ContextMenuStrip = menu, Text = "Roomie" };
            icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    OnUi(() => OpenRequested?.Invoke(this, EventArgs.Empty));
                }
            };
            icon.BalloonTipClicked += (_, _) => OnUi(() => NotificationOpened?.Invoke(this, _lastTarget));

            TooltipRequested += text => icon.Text = text;
            BalloonRequested += (title, body) => icon.ShowBalloonTip(NotificationMilliseconds, title, body, Forms.ToolTipIcon.None);
            _removeIcon = () =>
            {
                // Hidden first, or the icon lingers in the notification area until the mouse passes over it.
                icon.Visible = false;
                Forms.Application.ExitThread();
            };

            LogIconStep(_logger, "asking Windows to show it");
            icon.Visible = true;
            LogIconStep(_logger, "ready");

            // Needs to exist before anything is posted to it, and running the loop is what installs it for good.
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            _ready.TrySetResult(SynchronizationContext.Current!);
            Forms.Application.Run();
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException or ArgumentException or IOException)
        {
            // The window and everything else still work, and opening Roomie again shows the window.
            LogIconFailed(_logger, ex);
        }
    }

    private void OnUi(Action action) => _dispatcher.BeginInvoke(action);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification icon: {Step}")]
    private static partial void LogIconStep(ILogger logger, string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The notification icon couldn't be shown")]
    private static partial void LogIconFailed(ILogger logger, Exception exception);
}

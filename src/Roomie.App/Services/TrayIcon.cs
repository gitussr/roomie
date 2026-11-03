using System.Windows;
using System.Windows.Forms;
using Roomie.Core.Notifications;

namespace Roomie.App.Services;

/// <summary>What opening a notification should show.</summary>
public sealed record NotificationTarget(Guid? PeerId = null, string? GroupId = null, bool ShowTransfers = false);

/// <summary>
/// The notification-area icon. On Windows 10 and 11 its balloon tips appear as ordinary Windows notifications, and
/// unlike toast APIs they work without the app being installed, so they work in the portable build too.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int NotificationMilliseconds = 5000;

    /// <summary>Windows refuses a longer tooltip.</summary>
    private const int MaxTooltipLength = 63;

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private NotificationTarget? _lastTarget;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open Roomie", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit Roomie", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        var resource = System.Windows.Application.GetResourceStream(new Uri("/Assets/roomie.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("The icon is missing from the application.");
        using var stream = resource.Stream;
        _icon = new NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, SystemInformation.SmallIconSize),
            ContextMenuStrip = _menu,
            Text = "Roomie",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _icon.BalloonTipClicked += (_, _) => NotificationOpened?.Invoke(this, _lastTarget);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    /// <summary>The user clicked a notification. Windows says only that the latest one was clicked.</summary>
    public event EventHandler<NotificationTarget?>? NotificationOpened;

    public void SetTooltip(string text) => _icon.Text = NotificationText.Clean(text, MaxTooltipLength);

    public void Notify(string title, string body, NotificationTarget? target)
    {
        _lastTarget = target;
        _icon.ShowBalloonTip(
            NotificationMilliseconds,
            NotificationText.Clean(title, NotificationText.MaxTitleLength),
            // Windows refuses empty text.
            NotificationText.Clean(body, NotificationText.MaxBodyLength) is { Length: > 0 } text ? text : " ",
            ToolTipIcon.None);
    }

    public void Dispose()
    {
        // Hidden first, or the icon lingers in the notification area until the mouse passes over it.
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menu.Dispose();
    }
}

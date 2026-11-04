using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Roomie.App.Services;
using Roomie.App.ViewModels;
using Roomie.Infrastructure.Storage;

namespace Roomie.App.Views;

public partial class MainWindow : Window
{
    private const uint FlashTray = 2;
    private const uint FlashUntilForeground = 12;

    private readonly MainViewModel _viewModel;
    private readonly ThemeService _theme;
    private readonly JsonSettingsStore _settings;
    private int _shownBadge;

    public MainWindow(MainViewModel viewModel, ThemeService theme, JsonSettingsStore settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _theme = theme;
        _settings = settings;
        DataContext = viewModel;

        viewModel.AttentionRequested += (_, _) => RequestAttention();
        viewModel.PropertyChanged += OnViewModelChanged;
        Splash.Finished += (_, _) => FocusWelcome();
        viewModel.Pairing.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PairingViewModel.IsVisible))
            {
                FocusWelcome();
            }
        };
        viewModel.Welcome.PropertyChanged += (_, e) =>
        {
            // "Start chatting" should leave the cursor ready to type.
            if (e.PropertyName == nameof(WelcomeViewModel.IsVisible) && !viewModel.Welcome.IsVisible)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, Chats.FocusComposer);
            }
        };
        theme.Changed += (_, _) => theme.ApplyTitleBar(this);
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
        Activated += (_, _) => viewModel.OnWindowActiveChanged(true);
        Deactivated += (_, _) => viewModel.OnWindowActiveChanged(false);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                viewModel.OnWindowActiveChanged(false);
            }
        };
    }

    /// <summary>Set once Roomie is really exiting, so closing the window no longer just hides it.</summary>
    public bool IsExiting { get; set; }

    /// <summary>The window was closed to the notification area for the first time, so the user should be told.</summary>
    public event EventHandler? HiddenToTrayFirstTime;

    public void PlaySplash()
    {
        Splash.Prepare();
        ContentRendered += (_, _) => Splash.Play();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Flashes the taskbar button until the window is next in front.</summary>
    public void Flash()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || IsActive || !IsVisible)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        _ = NativeMethods.FlashWindowEx(ref info);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (IsExiting || !_settings.Current.CloseToTray)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _viewModel.OnWindowActiveChanged(false);
        if (!_settings.Current.HasSeenTrayHint)
        {
            _settings.Update(s => s with { HasSeenTrayHint = true });
            HiddenToTrayFirstTime?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    /// <summary>
    /// The welcome keeps Tab to itself, so the keyboard has to be moved on to pairing and back again. Onto the panel and
    /// not its buttons: a request can arrive mid-keystroke, and that key mustn't answer it.
    /// </summary>
    private void FocusWelcome()
    {
        if (!_viewModel.Welcome.IsVisible)
        {
            return;
        }

        if (_viewModel.Pairing.IsVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PairingCover.Focus());
        }
        else
        {
            Welcome.FocusStep();
        }
    }

    /// <summary>
    /// Comes forward for something that needs an answer, unless it was put away: then it stays away, because anyone
    /// on the network can ask to pair, and the notification and flashing button are enough.
    /// </summary>
    private void RequestAttention()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Activate();
        }

        Flash();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        var info = TaskbarItemInfo;
        if (e.PropertyName == nameof(MainViewModel.TaskbarProgress))
        {
            if (_viewModel.TaskbarProgress is { } progress)
            {
                info.ProgressState = TaskbarItemProgressState.Normal;
                info.ProgressValue = progress;
            }
            else
            {
                info.ProgressState = TaskbarItemProgressState.None;
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.TotalUnread) && _viewModel.TotalUnread != _shownBadge)
        {
            _shownBadge = _viewModel.TotalUnread;
            info.Overlay = _shownBadge > 0 ? DrawBadge(_shownBadge) : null;
        }
    }

    /// <summary>The count on the taskbar button.</summary>
    private RenderTargetBitmap DrawBadge(int count)
    {
        const int Size = 20;
        var scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            count > 9 ? "9+" : count.ToString(CultureInfo.CurrentCulture),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"),
            count > 9 ? 11 : 13,
            Brushes.White,
            scale);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xA8, 0x3A, 0x3A)), null, new Point(Size / 2d, Size / 2d), Size / 2d, Size / 2d);
            context.DrawText(text, new Point((Size - text.Width) / 2, (Size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool FlashWindowEx(ref FlashInfo info);
    }
}

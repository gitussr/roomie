using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Roomie.Core.Configuration;

namespace Roomie.App.Services;

/// <summary>Applies the light or dark colours, and follows Windows when the user chose that.</summary>
public sealed partial class ThemeService : IDisposable
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpFrameChanged = 0x20;

    private readonly Application _application;
    private ThemeChoice _choice;

    public ThemeService(Application application)
    {
        _application = application;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>The colours changed, so window title bars need to follow.</summary>
    public event EventHandler? Changed;

    public bool IsDark { get; private set; }

    public void Apply(ThemeChoice choice)
    {
        _choice = choice;
        var dark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => WindowsPrefersDark(),
        };
        IsDark = dark;

        _application.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "/Themes/Dark.xaml" : "/Themes/Light.xaml", UriKind.Relative),
        };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Windows draws the title bar, so it has to be told separately. Ignored by Windows versions without it.</summary>
    public void ApplyTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            var value = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));

            // Windows 10 doesn't repaint the title bar for this until the frame is told it changed.
            _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
        {
            _application.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
        }
    }

    private static bool WindowsPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

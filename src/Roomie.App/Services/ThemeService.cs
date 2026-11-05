using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    /// <summary>The colours changed, so window title bars need to follow.</summary>
    public event EventHandler? Changed;

    public bool IsDark { get; private set; }

    public void Apply(ThemeChoice choice)
    {
        _choice = choice;

        // Whichever colours were chosen: someone who turned this on in Windows needs it everywhere.
        if (IsHighContrast)
        {
            IsDark = Luminance(SystemColors.WindowColor) < 0.5;
            _application.Resources.MergedDictionaries[0] = CreateHighContrast();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

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

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }

    private static bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// Every colour comes from the scheme the user picked in Windows. Backgrounds are all one colour, as such schemes
    /// expect, so a state that was only a change of fill is drawn as a line instead (<c>RowLineBrush</c>).
    /// </summary>
    private static ResourceDictionary CreateHighContrast()
    {
        var background = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        var colors = new Dictionary<string, Color>
        {
            ["WindowBrush"] = background,
            ["RailBrush"] = background,
            ["PaneBrush"] = background,
            ["RaisedBrush"] = background,
            ["SubtleBrush"] = background,
            ["HoverBrush"] = background,
            ["SelectedBrush"] = background,
            ["MineBubbleBrush"] = background,
            ["TheirBubbleBrush"] = background,
            ["NoticeBrush"] = background,
            ["ScrimBrush"] = background,
            ["LineBrush"] = text,
            ["ControlLineBrush"] = text,
            ["TextBrush"] = text,
            ["MutedTextBrush"] = text,
            ["ProblemBrush"] = text,
            ["SuccessBrush"] = text,
            ["OnlineBrush"] = text,
            ["DisabledTextBrush"] = SystemColors.GrayTextColor,
            ["OfflineBrush"] = SystemColors.GrayTextColor,
            ["AccentBrush"] = highlight,
            ["AccentHoverBrush"] = highlight,
            ["OnAccentBrush"] = SystemColors.HighlightTextColor,
            ["RowLineBrush"] = highlight,
        };

        var dictionary = new ResourceDictionary { ["HoverLineThickness"] = new Thickness(2) };
        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary[key] = brush;
        }

        return dictionary;
    }

    private static double Luminance(Color color) => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255;

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            _application.Dispatcher.BeginInvoke(() => Apply(_choice));
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Going from one high-contrast scheme to another changes only the colours.
        var followsWindows = e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System;
        if (followsWindows || (e.Category == UserPreferenceCategory.Color && IsHighContrast))
        {
            _application.Dispatcher.BeginInvoke(() => Apply(_choice));
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

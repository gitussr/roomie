using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Roomie.App.Views;

/// <summary>
/// The logo reveal when Roomie opens. It covers the app, which is already running behind it, and any click or key
/// ends it. People who turned animations off in Windows don't see it.
/// </summary>
public partial class SplashView : UserControl
{
    private static readonly Duration RevealTime = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan ShownFor = TimeSpan.FromMilliseconds(1800);
    private static readonly Duration FadeInTime = TimeSpan.FromMilliseconds(350);

    private readonly DispatcherTimer _timer = new() { Interval = ShownFor };
    private bool _finishing;

    public SplashView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Finish();
        MouseDown += (_, _) => Finish();
        KeyDown += (_, _) => Finish();
    }

    /// <summary>It has gone, so whatever is behind it can take the keyboard.</summary>
    public event EventHandler? Finished;

    /// <summary>Covers the app, ready to start. Call before the window is shown.</summary>
    public void Prepare()
    {
        if (SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0)
        {
            Visibility = Visibility.Visible;
        }
    }

    /// <summary>Starts once the window is on screen, or a slow start would use up the reveal unseen.</summary>
    public void Play()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
        var overshoot = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        // The circle ends large enough to clear the logo's corners.
        var radius = new DoubleAnimation(0, 110, RevealTime) { EasingFunction = smooth };
        var circle = (EllipseGeometry)Logo.Clip;
        circle.BeginAnimation(EllipseGeometry.RadiusXProperty, radius);
        circle.BeginAnimation(EllipseGeometry.RadiusYProperty, radius);

        var grow = new DoubleAnimation(0.82, 1, RevealTime) { EasingFunction = overshoot };
        LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Logo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

        var nameStart = TimeSpan.FromMilliseconds(500);
        Wordmark.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeInTime) { BeginTime = nameStart });
        WordmarkShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, FadeInTime) { BeginTime = nameStart, EasingFunction = smooth });
        Tagline.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeInTime) { BeginTime = TimeSpan.FromMilliseconds(750) });

        _timer.Start();
        Focus();
    }

    private void Finish()
    {
        _timer.Stop();
        if (_finishing || Visibility != Visibility.Visible)
        {
            return;
        }

        _finishing = true;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;

            // Back to the app, for keyboard users.
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Finished?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(OpacityProperty, fade);
    }
}

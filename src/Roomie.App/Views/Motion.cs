using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Roomie.App.Views;

/// <summary>
/// The small animations. All of them are checked here against the Windows setting each time, so turning animations off
/// takes effect at once, and none of them moves anything in the layout.
/// </summary>
public static class Motion
{
    public static readonly DependencyProperty PopOnIncreaseProperty = DependencyProperty.RegisterAttached(
        "PopOnIncrease", typeof(int), typeof(Motion), new PropertyMetadata(0, OnPopOnIncreaseChanged));

    public static readonly DependencyProperty PopWhenTrueProperty = DependencyProperty.RegisterAttached(
        "PopWhenTrue", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPopWhenTrueChanged));

    private const double PressedShrinkPixels = 4;
    private const double SmallestPressedScale = 0.94;

    private static readonly CubicEase Smooth = new() { EasingMode = EasingMode.EaseOut };
    private static readonly BackEase Overshoot = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };

    private static bool IsEnabled => SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    public static int GetPopOnIncrease(DependencyObject element) => (int)element.GetValue(PopOnIncreaseProperty);

    public static void SetPopOnIncrease(DependencyObject element, int value) => element.SetValue(PopOnIncreaseProperty, value);

    public static bool GetPopWhenTrue(DependencyObject element) => (bool)element.GetValue(PopWhenTrueProperty);

    public static void SetPopWhenTrue(DependencyObject element, bool value) => element.SetValue(PopWhenTrueProperty, value);

    /// <summary>Makes every button respond to being pressed. Call once.</summary>
    public static void WatchButtons()
    {
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnButtonDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Button), Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnButtonUp), handledEventsToo: true);
    }

    /// <summary>A message just sent rises into place.</summary>
    public static void SlideUp(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!IsEnabled)
        {
            return;
        }

        var time = TimeSpan.FromMilliseconds(220);
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, time) { EasingFunction = Smooth });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>Something small, such as a count or a tick, appears with a little bounce.</summary>
    public static void Pop(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!IsEnabled || ScaleOf(element) is not { } scale)
        {
            return;
        }

        var grow = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Overshoot };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>A panel over the content fades in and settles.</summary>
    public static void Appear(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!IsEnabled || ScaleOf(element) is not { } scale)
        {
            return;
        }

        var time = TimeSpan.FromMilliseconds(160);
        var settle = new DoubleAnimation(0.97, 1, time) { EasingFunction = Smooth };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time));
    }

    private static void OnPopOnIncreaseChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is UIElement target && (int)e.NewValue > (int)e.OldValue)
        {
            Pop(target);
        }
    }

    // Only once on screen: an item that is already finished when its list is first drawn has nothing to announce.
    private static void OnPopWhenTrueChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is FrameworkElement { IsLoaded: true } target && (bool)e.NewValue)
        {
            Pop(target);
        }
    }

    private static void OnButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || !IsEnabled || ScaleOf(button) is not { } scale)
        {
            return;
        }

        // By a set distance, so a wide row moves about as much as a small button.
        var pressed = Math.Max(SmallestPressedScale, 1 - (PressedShrinkPixels / Math.Max(1, button.ActualWidth)));
        var press = new DoubleAnimation(pressed, TimeSpan.FromMilliseconds(70)) { EasingFunction = Smooth };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
    }

    // Always, even if animations were turned off while it was held, so it can't stay small.
    private static void OnButtonUp(object sender, MouseEventArgs e)
    {
        if (sender is not Button { RenderTransform: ScaleTransform { IsFrozen: false } scale })
        {
            return;
        }

        var release = new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { EasingFunction = Smooth };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, release);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, release);
    }

    private static ScaleTransform? ScaleOf(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform { IsFrozen: false } existing)
        {
            return existing;
        }

        // Anything else was put there for another reason and is left alone.
        if (element.RenderTransform != Transform.Identity)
        {
            return null;
        }

        var scale = new ScaleTransform();
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return scale;
    }
}

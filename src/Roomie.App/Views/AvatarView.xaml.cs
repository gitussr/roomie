using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Roomie.App.Services;

namespace Roomie.App.Views;

/// <summary>A round picture, built-in avatar or initial. It fills whatever size it's given.</summary>
public partial class AvatarView : UserControl
{
    public static readonly DependencyProperty AvatarProperty = DependencyProperty.Register(
        nameof(Avatar),
        typeof(AvatarVisual),
        typeof(AvatarView),
        new PropertyMetadata(null, (d, _) => ((AvatarView)d).Draw()));

    public AvatarView()
    {
        InitializeComponent();

        // Keeps the letter or emoji inside the circle at any size.
        SizeChanged += (_, e) => Label.Margin = new Thickness(e.NewSize.Width * 0.22);
        Draw();
    }

    public AvatarVisual? Avatar
    {
        get => (AvatarVisual?)GetValue(AvatarProperty);
        set => SetValue(AvatarProperty, value);
    }

    private void Draw()
    {
        var avatar = Avatar;
        if (avatar?.Background is { } background)
        {
            Fill.Fill = background;
        }
        else
        {
            Fill.SetResourceReference(Shape.FillProperty, "HoverBrush");
        }

        if (avatar?.Glyph is { } glyph)
        {
            LabelText.Text = glyph;
            LabelText.FontFamily = new FontFamily("Segoe UI Emoji");
            LabelText.FontWeight = FontWeights.Normal;
            LabelText.Foreground = Brushes.White;
        }
        else
        {
            LabelText.Text = avatar?.Initial ?? string.Empty;
            LabelText.FontFamily = SystemFonts.MessageFontFamily;
            LabelText.FontWeight = FontWeights.SemiBold;
            LabelText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }

        if (avatar?.Image is { } image)
        {
            Picture.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            Picture.Visibility = Visibility.Visible;
        }
        else
        {
            Picture.Fill = null;
            Picture.Visibility = Visibility.Collapsed;
        }
    }
}

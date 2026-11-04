using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Roomie.Core.Profiles;
using Roomie.Network.Profiles;

namespace Roomie.App.Services;

/// <summary>How to draw a device or group: a picture, a built-in avatar, or the first letter of its name.</summary>
public sealed record AvatarVisual(string Initial, ImageSource? Image = null, Brush? Background = null, string? Glyph = null);

/// <summary>A built-in avatar. Devices exchange only its number, so a name or colour here can change freely.</summary>
public sealed record AvatarPreset(int Id, string Name, Brush Background, string Glyph);

/// <summary>Turns saved choices into something to draw. Each is decoded once, and the same object means the same picture.</summary>
public sealed class AvatarCatalog
{
    private readonly IAvatarStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ImageSource?> _pictures = [];

    public AvatarCatalog(IAvatarStore store, ProfileService profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _store = store;
        Presets = [
            Preset(1, "Fox", 0xB8, 0x5A, 0x1E, "\U0001F98A"),
            Preset(2, "Panda", 0x3F, 0x4A, 0x57, "\U0001F43C"),
            Preset(3, "Cat", 0x7A, 0x3E, 0x9D, "\U0001F431"),
            Preset(4, "Dog", 0x8A, 0x5A, 0x2B, "\U0001F436"),
            Preset(5, "Frog", 0x2E, 0x7D, 0x4F, "\U0001F438"),
            Preset(6, "Owl", 0x5D, 0x4F, 0x8C, "\U0001F989"),
            Preset(7, "Penguin", 0x1F, 0x6F, 0x9E, "\U0001F427"),
            Preset(8, "Monkey", 0x9C, 0x4A, 0x3A, "\U0001F435"),
            Preset(9, "Rocket", 0x28, 0x4B, 0x8C, "\U0001F680"),
            Preset(10, "Flower", 0xB0, 0x3A, 0x6E, "\U0001F338"),
            Preset(11, "Football", 0x1F, 0x7A, 0x6E, "⚽"),
            Preset(12, "Headphones", 0x55, 0x55, 0x60, "\U0001F3A7"),
        ];

        // Subscribed here so that it comes before anyone asking for the new picture because of the same event.
        profiles.Changed += (_, deviceId) =>
        {
            lock (_gate)
            {
                _pictures.Remove(deviceId);
            }
        };
    }

    public IReadOnlyList<AvatarPreset> Presets { get; }

    /// <summary>For something with no picture of its own, such as a group or an unpaired device.</summary>
    public static AvatarVisual Letter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = new StringInfo(name);
        return new AvatarVisual(text.LengthInTextElements > 0 ? text.SubstringByTextElements(0, 1).ToUpper(CultureInfo.CurrentCulture) : "?");
    }

    public AvatarVisual For(Guid deviceId, string name)
    {
        var letter = Letter(name);
        var choice = _store.Load(deviceId);
        if (choice.Preset is { } number)
        {
            var preset = Presets[number - 1];
            return letter with { Background = preset.Background, Glyph = preset.Glyph };
        }

        if (choice.PictureBytes is not { } png)
        {
            return letter;
        }

        lock (_gate)
        {
            if (!_pictures.TryGetValue(deviceId, out var image))
            {
                image = Decode(png);
                _pictures[deviceId] = image;
            }

            return letter with { Image = image };
        }
    }

    public static AvatarVisual ForPreset(AvatarPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new AvatarVisual(string.Empty, Background: preset.Background, Glyph: preset.Glyph);
    }

    // A picture from another device has passed the format check, but only a decoder can say it's really an image.
    // One that isn't just shows the letter.
    private static BitmapImage? Decode(ReadOnlyMemory<byte> png)
    {
        try
        {
            using var stream = new MemoryStream(png.ToArray());
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = ProfileProtocol.PictureSize;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException or ExternalException or OverflowException)
        {
            return null;
        }
    }

    private static AvatarPreset Preset(int id, string name, byte r, byte g, byte b, string glyph)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return new AvatarPreset(id, name, brush, glyph);
    }
}

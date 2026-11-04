using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Roomie.Core.Profiles;

namespace Roomie.Infrastructure.Profiles;

/// <summary>The chosen file couldn't be turned into a picture. The message is for the user.</summary>
public sealed class AvatarImageException : Exception
{
    public AvatarImageException()
    {
    }

    public AvatarImageException(string message)
        : base(message)
    {
    }

    public AvatarImageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Turns a picture the user chose into what devices exchange: a centred square, upright, 96 by 96, as PNG. Only the
/// pixels are carried over, so whatever else the file held, such as where a photo was taken, stays behind.
/// </summary>
public static class AvatarImage
{
    private const long MaxFileBytes = 50L * 1024 * 1024;
    private const long MaxPixels = 100_000_000;

    /// <summary>Where Windows' imaging reads a JPEG's EXIF orientation.</summary>
    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    /// <exception cref="AvatarImageException">The file is too large or isn't a picture.</exception>
    public static byte[] Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                throw new AvatarImageException("That picture is too large. Choose one under 50 MB.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Nothing is decoded until the pixels are asked for, so the size can be checked first.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            {
                throw new AvatarImageException("That picture is too large. Choose one under 100 megapixels.");
            }

            var side = Math.Min(frame.PixelWidth, frame.PixelHeight);
            BitmapSource image = new CroppedBitmap(frame, new Int32Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side));
            image = Upright(image, OrientationOf(frame));
            if (side != ProfileProtocol.PictureSize)
            {
                var scale = (double)ProfileProtocol.PictureSize / side;
                image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)));
            using var output = new MemoryStream();
            encoder.Save(output);

            var png = output.ToArray();
            return ProfileProtocol.IsAcceptablePicture(png)
                ? png
                : throw new AvatarImageException("Roomie couldn't use that picture. Try another one.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or ExternalException or OverflowException)
        {
            throw new AvatarImageException("Roomie couldn't read that file as a picture. Try a JPG or PNG.", ex);
        }
    }

    private static int OrientationOf(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery(OrientationQuery) && metadata.GetQuery(OrientationQuery) is ushort value
                ? value
                : 1;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or ExternalException)
        {
            // Formats without this kind of metadata say so in different ways.
            return 1;
        }
    }

    // Phones store photos sideways with a note saying which way is up.
    private static BitmapSource Upright(BitmapSource image, int orientation)
    {
        var (mirror, turn) = orientation switch
        {
            2 => (true, 0),
            3 => (false, 180),
            4 => (true, 180),
            5 => (true, 270),
            6 => (false, 90),
            7 => (true, 90),
            8 => (false, 270),
            _ => (false, 0),
        };

        if (!mirror && turn == 0)
        {
            return image;
        }

        var steps = new TransformGroup();
        if (mirror)
        {
            steps.Children.Add(new ScaleTransform(-1, 1));
        }

        steps.Children.Add(new RotateTransform(turn));
        return new TransformedBitmap(image, steps);
    }
}

using System.Globalization;
using Roomie.Core.Profiles;

namespace Roomie.Infrastructure.Profiles;

/// <summary>
/// One file per device, named by its ID and never by anything a peer sent: <c>{id}.png</c> for a picture, or
/// <c>{id}.preset</c> holding a built-in avatar's number. Each is checked again when read.
/// </summary>
public sealed class FileAvatarStore : IAvatarStore
{
    private readonly string _folder;
    private readonly Lock _gate = new();

    public FileAvatarStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
    }

    public AvatarChoice Load(Guid deviceId)
    {
        lock (_gate)
        {
            try
            {
                var picture = PathOf(deviceId, "png");
                if (File.Exists(picture))
                {
                    if (new FileInfo(picture).Length > ProfileProtocol.MaxPictureBytes)
                    {
                        return AvatarChoice.None;
                    }

                    var bytes = File.ReadAllBytes(picture);
                    return ProfileProtocol.IsAcceptablePicture(bytes) ? AvatarChoice.FromPicture(bytes) : AvatarChoice.None;
                }

                var preset = PathOf(deviceId, "preset");
                return File.Exists(preset)
                    && int.TryParse(File.ReadAllText(preset), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    && ProfileProtocol.IsPreset(number)
                        ? AvatarChoice.FromPreset(number)
                        : AvatarChoice.None;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return AvatarChoice.None;
            }
        }
    }

    public void Save(Guid deviceId, AvatarChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        lock (_gate)
        {
            Directory.CreateDirectory(_folder);
            var picture = PathOf(deviceId, "png");
            var preset = PathOf(deviceId, "preset");
            if (choice.PictureBytes is { } png)
            {
                Write(picture, png.Span);
                File.Delete(preset);
            }
            else if (choice.Preset is { } number)
            {
                Write(preset, System.Text.Encoding.ASCII.GetBytes(number.ToString(CultureInfo.InvariantCulture)));
                File.Delete(picture);
            }
            else
            {
                File.Delete(picture);
                File.Delete(preset);
            }
        }
    }

    public void RemoveAllExcept(IReadOnlySet<Guid> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        lock (_gate)
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(_folder))
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && !keep.Contains(id))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // It's tried again at the next change, and unused until then.
                    }
                }
            }
        }
    }

    private string PathOf(Guid deviceId, string extension) => Path.Combine(_folder, $"{deviceId:N}.{extension}");

    private static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

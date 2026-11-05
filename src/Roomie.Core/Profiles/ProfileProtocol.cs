using System.Buffers.Binary;
using System.Text.Json;
using Roomie.Core.Identity;
using Roomie.Core.Protocol;

namespace Roomie.Core.Profiles;

/// <summary>What a device shows as its picture: one of the built-in avatars, its own picture, or neither.</summary>
public sealed class AvatarChoice
{
    private AvatarChoice(int? preset, byte[]? picture)
    {
        Preset = preset;
        Picture = picture;
    }

    /// <summary>No picture: the first letter of the name is shown.</summary>
    public static AvatarChoice None { get; } = new(null, null);

    public int? Preset { get; }

    /// <summary>A PNG that passed <see cref="ProfileProtocol.IsAcceptablePicture"/>.</summary>
    /// <remarks>Spelled out because a null array would otherwise convert to an empty memory block, which is not null.</remarks>
    public ReadOnlyMemory<byte>? PictureBytes
    {
        get
        {
            if (Picture is null)
            {
                return null;
            }

            return new ReadOnlyMemory<byte>(Picture);
        }
    }

    private byte[]? Picture { get; }

    public static AvatarChoice FromPreset(int preset)
    {
        if (!ProfileProtocol.IsPreset(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        return new AvatarChoice(preset, null);
    }

    public static AvatarChoice FromPicture(ReadOnlySpan<byte> png)
    {
        if (!ProfileProtocol.IsAcceptablePicture(png))
        {
            throw new ArgumentException("Not an acceptable picture.", nameof(png));
        }

        return new AvatarChoice(null, png.ToArray());
    }

    public bool IsSameAs(AvatarChoice other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Preset == other.Preset
            && (Picture ?? []).AsSpan().SequenceEqual(other.Picture ?? []);
    }
}

/// <summary>The payload of a <c>PROFILE_UPDATE</c>: at most one of a built-in avatar and a picture. Neither clears it.</summary>
public sealed record ProfileUpdatePayload(int? Preset = null, string? Picture = null);

/// <summary>The payload of a <c>PROFILE_NAME</c>: the name this device goes by.</summary>
public sealed record ProfileNamePayload(string Username);

/// <summary>
/// The rules for a device's picture (architecture.md §15). A picture comes from another device, so it is held to one
/// exact format: a small square PNG. Its size is known before anything decodes it, so it can't be a decompression bomb.
/// </summary>
public static class ProfileProtocol
{
    /// <summary>Built-in avatars are numbered from 1. Devices send only the number.</summary>
    public const int PresetCount = 12;

    public const int PictureSize = 96;

    /// <summary>Holds any 96 by 96 PNG with transparency, and stays within a control frame once in base64.</summary>
    public const int MaxPictureBytes = 40 * 1024;

    private static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    public static bool IsPreset(int value) => value is >= 1 and <= PresetCount;

    /// <summary>Whether bytes start as a PNG of exactly the agreed size, and are no larger than allowed.</summary>
    public static bool IsAcceptablePicture(ReadOnlySpan<byte> png) =>
        png.Length is > 33 and <= MaxPictureBytes
        && png[..PngStart.Length].SequenceEqual(PngStart)
        && BinaryPrimitives.ReadInt32BigEndian(png[16..]) == PictureSize
        && BinaryPrimitives.ReadInt32BigEndian(png[20..]) == PictureSize;

    public static ProfileUpdatePayload ToPayload(AvatarChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return new ProfileUpdatePayload(choice.Preset, choice.PictureBytes is { } png ? Convert.ToBase64String(png.Span) : null);
    }

    /// <summary>Only a name already in its normal form, the same rule as everywhere else names are saved.</summary>
    public static RmpPayloadCheck CheckName(JsonElement payload) =>
        SessionPayloads.TryDeserialize<ProfileNamePayload>(payload) is { Username: { } name }
        && Username.TryNormalize(name, out var normalized, out _) && normalized == name
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckUpdate(JsonElement payload)
    {
        if (SessionPayloads.TryDeserialize<ProfileUpdatePayload>(payload) is not { } update)
        {
            return RmpPayloadCheck.Malformed;
        }

        if (update.Picture is { Length: > (MaxPictureBytes + 2) / 3 * 4 })
        {
            return RmpPayloadCheck.TooLarge;
        }

        return TryRead(update, out _) ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;
    }

    public static bool TryRead(ProfileUpdatePayload update, out AvatarChoice choice)
    {
        ArgumentNullException.ThrowIfNull(update);
        choice = AvatarChoice.None;
        if (update.Preset is { } preset)
        {
            if (update.Picture is not null || !IsPreset(preset))
            {
                return false;
            }

            choice = AvatarChoice.FromPreset(preset);
            return true;
        }

        if (update.Picture is not { } text)
        {
            return true;
        }

        var buffer = new byte[MaxPictureBytes];
        if (text.Length > (MaxPictureBytes + 2) / 3 * 4
            || !Convert.TryFromBase64String(text, buffer, out var written)
            || !IsAcceptablePicture(buffer.AsSpan(0, written)))
        {
            return false;
        }

        choice = AvatarChoice.FromPicture(buffer.AsSpan(0, written));
        return true;
    }
}

/// <summary>Saved pictures, of this device and paired ones.</summary>
public interface IAvatarStore
{
    /// <summary>What's saved for a device. Anything missing or unusable counts as none.</summary>
    AvatarChoice Load(Guid deviceId);

    /// <exception cref="IOException">It couldn't be saved. What was there before is still there.</exception>
    /// <exception cref="UnauthorizedAccessException">As above.</exception>
    void Save(Guid deviceId, AvatarChoice choice);

    /// <summary>Removes pictures of every other device, so forgetting a device removes its picture.</summary>
    void RemoveAllExcept(IReadOnlySet<Guid> keep);
}

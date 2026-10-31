using System.Buffers.Binary;

namespace Roomie.Core.Protocol;

public enum RmpFrameType : byte
{
    Control = 0x01,
    FileChunk = 0x02,
}

public readonly record struct RmpFrame(RmpFrameType Type, ReadOnlyMemory<byte> Payload);

/// <summary>A peer broke the protocol. <see cref="Code"/> is the RMP error code to report before closing.</summary>
public sealed class RmpProtocolException : Exception
{
    public RmpProtocolException()
        : this(RmpErrorCodes.InvalidFrame, "The peer violated the protocol.")
    {
    }

    public RmpProtocolException(string message)
        : this(RmpErrorCodes.InvalidFrame, message)
    {
    }

    public RmpProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = RmpErrorCodes.InvalidFrame;
    }

    public RmpProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Length-prefixed RMP frames (architecture.md §5.1): <c>[length: u32 BE][frameType: u8][payload]</c>,
/// where the length counts the type byte plus the payload.
/// </summary>
public static class RmpFraming
{
    public const int MaxControlPayload = 64 * 1024;
    public const int MaxFileChunkPayload = (64 * 1024) + 24;

    private const int HeaderLength = 5;

    /// <summary>
    /// Reads one frame. Returns null when the stream ends cleanly between frames.
    /// </summary>
    /// <exception cref="RmpProtocolException">The length or frame type is invalid; checked before allocating.</exception>
    /// <exception cref="EndOfStreamException">The stream ended in the middle of a frame.</exception>
    public static async ValueTask<RmpFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderLength];
        var read = await stream.ReadAtLeastAsync(header, HeaderLength, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderLength)
        {
            throw new EndOfStreamException("The connection closed in the middle of a frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MaxFileChunkPayload + 1)
        {
            throw new RmpProtocolException(length == 0 ? RmpErrorCodes.InvalidFrame : RmpErrorCodes.MessageTooLarge, $"Invalid frame length {length}.");
        }

        var type = (RmpFrameType)header[4];
        var payloadLength = (int)length - 1;
        var max = type switch
        {
            RmpFrameType.Control => MaxControlPayload,
            RmpFrameType.FileChunk => MaxFileChunkPayload,
            _ => throw new RmpProtocolException($"Unknown frame type 0x{header[4]:X2}."),
        };

        if (payloadLength > max)
        {
            throw new RmpProtocolException(RmpErrorCodes.MessageTooLarge, $"A {type} frame of {payloadLength} bytes exceeds the {max}-byte limit.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new RmpFrame(type, payload);
    }

    /// <summary>Encodes a frame into one buffer, so a single write puts it on the wire.</summary>
    public static byte[] Encode(RmpFrameType type, ReadOnlySpan<byte> payload)
    {
        var max = type switch
        {
            RmpFrameType.Control => MaxControlPayload,
            RmpFrameType.FileChunk => MaxFileChunkPayload,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        if (payload.Length > max)
        {
            throw new ArgumentException($"A {type} payload can't exceed {max} bytes.", nameof(payload));
        }

        var frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length + 1);
        frame[4] = (byte)type;
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }
}

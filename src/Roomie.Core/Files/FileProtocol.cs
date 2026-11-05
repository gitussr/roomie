using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Roomie.Core.Messaging;
using Roomie.Core.Protocol;

namespace Roomie.Core.Files;

/// <summary><c>FILE_ACCEPT</c>: send the file.</summary>
public sealed record FileAcceptPayload(Guid TransferId);

/// <summary>
/// <c>FILE_RESUME</c>: from the receiver, after a connection or restart: send the rest, starting at this byte.
/// Chunk sequence numbers start again from 0.
/// </summary>
public sealed record FileResumePayload(Guid TransferId, long Offset);

/// <summary>
/// <c>FILE_REJECT</c>: this transfer is over, from either side, at any stage. The reason is one of
/// <see cref="FileRejectReasons"/>, so what the user reads is never text a peer chose.
/// </summary>
public sealed record FileRejectPayload(Guid TransferId, string Reason);

/// <summary><c>FILE_COMPLETE</c>: every byte was sent.</summary>
public sealed record FileCompletePayload(Guid TransferId);

/// <summary><c>FILE_ACK</c>: whether the received file matched the offered hash.</summary>
public sealed record FileAckPayload(Guid TransferId, bool Verified);

public static class FileRejectReasons
{
    public const string Declined = "declined";
    public const string Canceled = "canceled";
    public const string Failed = "failed";
    public const string Busy = "busy";
}

/// <summary>Stages of a transfer (architecture.md §8).</summary>
public enum FileTransferState
{
    /// <summary>Sender: saved, and the offer hasn't gone out yet.</summary>
    Waiting,

    /// <summary>Waiting for the receiver's user to answer.</summary>
    Offered,

    /// <summary>The receiver said yes, and no data has moved yet.</summary>
    Accepted,

    Transferring,

    /// <summary>All data moved, and the hash hasn't been confirmed yet.</summary>
    Verifying,

    Completed,

    /// <summary>The receiver declined.</summary>
    Rejected,

    Failed,

    /// <summary>One side stopped it.</summary>
    Canceled,
}

/// <param name="Filename">Sender: the source file's name. Receiver: already made safe by <see cref="SafeFilename"/>.</param>
/// <param name="LocalPath">Sender: the source file. Receiver: where it was saved, once verified.</param>
/// <param name="ReceivedBytes">Receiver: how much of the file is known to be safely on disk.</param>
/// <param name="SourceWriteTicks">Sender: when the source file last changed, as it was when hashed. 0 if unknown.</param>
/// <param name="PartialPath">Receiver: where the unfinished file is, if not in the default incoming folder.</param>
public sealed record StoredTransfer(
    Guid TransferId,
    Guid PeerId,
    MessageDirection Direction,
    string Filename,
    long FileSize,
    string Sha256,
    string ContentType,
    FileTransferState State,
    DateTimeOffset CreatedAt,
    string? LocalPath = null,
    string? Failure = null,
    long ReceivedBytes = 0,
    long SourceWriteTicks = 0,
    string? PartialPath = null);

/// <summary>Transfer records. A transfer belongs to one device and direction, so no peer can touch another's. Methods throw <see cref="MessageStoreException"/> if the store fails.</summary>
public interface IFileTransferStore
{
    void AddTransfer(StoredTransfer transfer);

    StoredTransfer? GetTransfer(Guid peerId, MessageDirection direction, Guid transferId);

    /// <summary>The latest transfers with a device, newest first.</summary>
    IReadOnlyList<StoredTransfer> GetTransfers(Guid peerId, int maxTransfers);

    /// <summary>The latest transfers with any device, newest first.</summary>
    IReadOnlyList<StoredTransfer> GetRecentTransfers(int maxTransfers);

    /// <summary>Every transfer that hasn't ended, with any device.</summary>
    IReadOnlyList<StoredTransfer> GetOpenTransfers();

    void UpdateTransfer(Guid peerId, MessageDirection direction, Guid transferId, FileTransferState state, string? localPath, string? failure);

    /// <summary>
    /// Moves a transfer only if it is in one of the given states, in one step. Network, disk and the user all
    /// act on transfers at once, so without this a late write could undo something newer, such as a cancel.
    /// </summary>
    /// <returns>False if the transfer has moved on, and nothing changed.</returns>
    bool TryMoveTransfer(Guid peerId, MessageDirection direction, Guid transferId, FileTransferState state, string? localPath, string? failure, params FileTransferState[] from);

    /// <summary>Records how much of an incoming file is safely on disk, so a transfer can continue from there.</summary>
    void SaveReceivedBytes(Guid peerId, Guid transferId, long receivedBytes);

    /// <summary>Records where an incoming file is being written, so it can be found again after a restart.</summary>
    void SavePartialPath(Guid peerId, Guid transferId, string partialPath);
}

/// <summary>One binary chunk (architecture.md §8): transfer ID, sequence number, data length, data.</summary>
public readonly record struct FileChunk(Guid TransferId, uint Sequence, ReadOnlyMemory<byte> Data)
{
    public const int HeaderLength = 24;
    public const int MaxDataLength = 64 * 1024;

    public static byte[] Encode(Guid transferId, uint sequence, ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxDataLength)
        {
            throw new ArgumentException($"A chunk holds 1 to {MaxDataLength} bytes.", nameof(data));
        }

        var payload = new byte[HeaderLength + data.Length];
        transferId.TryWriteBytes(payload.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20), (uint)data.Length);
        data.CopyTo(payload.AsSpan(HeaderLength));
        return RmpFraming.Encode(RmpFrameType.FileChunk, payload);
    }

    /// <returns>False if the header is short, the ID is empty, or the data length disagrees with the frame.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> payload, out FileChunk chunk)
    {
        chunk = default;
        if (payload.Length < HeaderLength + 1)
        {
            return false;
        }

        var span = payload.Span;
        var id = new Guid(span[..16], bigEndian: true);
        var length = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
        if (id == Guid.Empty || length is 0 or > MaxDataLength || length != payload.Length - HeaderLength)
        {
            return false;
        }

        chunk = new FileChunk(id, BinaryPrimitives.ReadUInt32BigEndian(span[16..]), payload[HeaderLength..]);
        return true;
    }
}

/// <summary>Strict checks for the file messages.</summary>
public static class FilePayloads
{
    public static RmpPayloadCheck CheckAccept(JsonElement payload) =>
        SessionPayloads.TryDeserialize<FileAcceptPayload>(payload) is { TransferId: var id } && id != Guid.Empty ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckResume(JsonElement payload) =>
        SessionPayloads.TryDeserialize<FileResumePayload>(payload) is { } resume && resume.TransferId != Guid.Empty && resume.Offset >= 0
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckReject(JsonElement payload) =>
        SessionPayloads.TryDeserialize<FileRejectPayload>(payload) is { } reject && reject.TransferId != Guid.Empty
        && reject.Reason is FileRejectReasons.Declined or FileRejectReasons.Canceled or FileRejectReasons.Failed or FileRejectReasons.Busy
            ? RmpPayloadCheck.Valid
            : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckComplete(JsonElement payload) =>
        SessionPayloads.TryDeserialize<FileCompletePayload>(payload) is { TransferId: var id } && id != Guid.Empty ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;

    public static RmpPayloadCheck CheckAck(JsonElement payload) =>
        SessionPayloads.TryDeserialize<FileAckPayload>(payload) is { TransferId: var id } && id != Guid.Empty ? RmpPayloadCheck.Valid : RmpPayloadCheck.Malformed;
}

/// <summary>
/// Turns a name a peer chose into one that is safe to create on Windows (architecture.md §10, rule 5). The
/// result never contains a path, and never means something special to Windows.
/// </summary>
public static class SafeFilename
{
    private const int MaxLength = 200;
    private const string Fallback = "file";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    // Windows treats these as programs, or as something that runs one, when opened.
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".msc", ".cpl", ".dll", ".sys", ".ocx", ".hta", ".lnk", ".url", ".reg", ".inf",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".jar", ".appx", ".msix", ".application", ".gadget", ".scf", ".chm",
        ".ws", ".wsc", ".vb", ".sct", ".shb", ".mst", ".hlp", ".mht", ".mhtml", ".ade", ".adp", ".mde", ".accde", ".wsb", ".rdp", ".py", ".pyw",
        ".appxbundle", ".msixbundle", ".appinstaller", ".diagcab", ".library-ms", ".search-ms", ".settingcontent-ms",
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".xll", ".pptm", ".potm", ".ppam",

        // Opening one of these shows what's inside it, and those files may not carry the warning from this one.
        ".iso", ".img", ".vhd", ".vhdx",
    };

    public static string Create(string offered)
    {
        ArgumentNullException.ThrowIfNull(offered);

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var builder = new StringBuilder(offered.Length);
        for (var i = 0; i < offered.Length; i++)
        {
            var ch = offered[i];
            if (char.IsSurrogatePair(offered, i))
            {
                builder.Append(ch).Append(offered[++i]);
            }
            else if (char.IsSurrogate(ch))
            {
                builder.Append('_');
            }
            else if (char.GetUnicodeCategory(ch) is UnicodeCategory.Format or UnicodeCategory.Control)
            {
                // Includes the right-to-left override, which can make "gpj.exe" display as "exe.jpg".
                continue;
            }
            else
            {
                builder.Append(invalid.Contains(ch) ? '_' : ch);
            }
        }

        // Windows silently drops trailing dots and spaces, so "evil.exe." would become "evil.exe".
        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            return Fallback;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? name : name[..dot];
        if (Reserved.Contains(stem.TrimEnd(' ')))
        {
            name = "_" + name;
        }

        return Shorten(name);
    }

    /// <summary>The name to try after <paramref name="name"/> is taken: <c>notes (1).pdf</c>, and so on.</summary>
    public static string WithCounter(string name, int counter)
    {
        ArgumentNullException.ThrowIfNull(name);
        var extension = Path.GetExtension(name);
        return string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(name)} ({counter}){extension}");
    }

    /// <summary>Whether opening this file would run something, so the user should be warned before accepting it.</summary>
    public static bool IsRunnable(string name) => RunnableExtensions.Contains(Path.GetExtension(name));

    private static string Shorten(string name)
    {
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length > 32)
        {
            extension = string.Empty;
        }

        var length = MaxLength - extension.Length;
        if (char.IsHighSurrogate(name[length - 1]))
        {
            length--;
        }

        return name[..length] + extension;
    }
}

/// <summary>Reads a file payload as the type it was checked to be.</summary>
public static class FilePayloadReader
{
    public static bool TryRead<T>(JsonElement payload, [NotNullWhen(true)] out T? value)
        where T : class
    {
        value = SessionPayloads.TryDeserialize<T>(payload);
        return value is not null;
    }
}

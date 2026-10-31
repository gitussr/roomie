using System.Net;

namespace Roomie.Core.Connections;

/// <summary>Where a device stands with this computer (architecture.md §4.1). Separate from discovery and trust.</summary>
public enum PeerConnectionState
{
    Disconnected,
    Connecting,

    /// <summary>TCP is up; TLS and the HELLO exchange are in progress.</summary>
    Authenticating,

    /// <summary>
    /// A mutually authenticated TLS session with a device that hasn't been paired. Only session and
    /// pairing messages are allowed on it.
    /// </summary>
    ConnectedUntrusted,

    /// <summary>The device presented the certificate pinned when it was paired.</summary>
    ConnectedTrusted,

    /// <summary>A trusted device's session was lost; Roomie will try again.</summary>
    Reconnecting,

    Failed,

    /// <summary>A paired device presented a different certificate, or two certificates claim one device ID.</summary>
    IdentityMismatch,

    Blocked,
}

public enum SessionDirection
{
    /// <summary>This computer opened the TCP connection.</summary>
    Outbound,

    /// <summary>The peer opened the TCP connection.</summary>
    Inbound,
}

/// <param name="RemoteFingerprint">The SPKI fingerprint the peer authenticated with, once known.</param>
/// <param name="Security">A short description of the TLS session, e.g. <c>TLS 1.2, ECDHE-ECDSA AES-256-GCM</c>.</param>
/// <param name="LastError">Why the last attempt or session ended, for display.</param>
public sealed record PeerConnectionInfo(
    Guid DeviceId,
    PeerConnectionState State,
    string? RemoteFingerprint = null,
    SessionDirection? Direction = null,
    IPEndPoint? RemoteEndPoint = null,
    string? Security = null,
    string? LastError = null)
{
    public bool IsConnected => State is PeerConnectionState.ConnectedTrusted or PeerConnectionState.ConnectedUntrusted;
}

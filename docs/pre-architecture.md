# Roomie — Architecture & Implementation Blueprint

Recommended architecture: Peer-to-peer

Platform: Windows

> Status: high-level blueprint. Decisions made after this document — identity model, network scope, protocol, and the revised roadmap — live in `architecture.md`, which takes precedence where the two differ.

I recommend building this as a native Windows desktop application using C# and .NET 10 + WPF, with direct computer-to-computer communication over the local network.

The application should work without:

- An internet connection.
- A cloud account.
- A centrally hosted messaging server.
- A third-party messaging service.
- A permanently running host computer.

Every computer runs its own instance of the application. Each instance discovers other computers, establishes secure connections, exchanges messages, and stores its own chat history.

There is one important architectural distinction:

Peer-to-peer does not mean every feature must use the same networking mechanism.

We'll use UDP for discovery and TCP for reliable communication, with a small protocol layer to manage messages, groups, file transfers, and peer status.

## 1. Recommended technology stack

# C# + .NET 10 + WPF

My recommendation

Native Windows desktop application

| Layer                | Technology                               |
| -------------------- | ---------------------------------------- |
| Programming language | C#                                       |
| Runtime              | .NET 10 LTS                              |
| Desktop UI           | WPF                                      |
| UI architecture      | MVVM                                     |
| Dependency injection | Microsoft.Extensions.DependencyInjection |
| LAN discovery        | UDP broadcast / multicast                |
| Messaging transport  | TCP sockets                              |
| Secure communication | TLS 1.3                                  |
| Data serialization   | System.Text.Json                         |
| Local database       | SQLite                                   |
| Database access      | Microsoft.Data.Sqlite                    |
| File transfer        | TCP streaming                            |
| Logging              | Microsoft.Extensions.Logging             |
| Installer            | MSIX                                     |
| IDE                  | Visual Studio 2026                       |

Why WPF rather than immediately choosing a web-based desktop framework?

- It provides genuine Windows desktop controls.
- It integrates naturally with the Windows notification area, clipboard, file dialogs, and notifications.
- It doesn't require shipping a separate browser runtime.
- It has a mature ecosystem and extensive documentation.
- It lets you learn Windows desktop development rather than simply packaging a website.

One caveat: WPF is Windows-only and its UI uses XAML. That's appropriate for this project's target.

## 2. Compare the desktop frameworks

Although WPF is my recommendation, these are the four serious candidates.

| Technology    | Windows-native experience   | Networking                  | Learning curve |
| ------------- | --------------------------- | --------------------------- | -------------- |
| C# + WPF      | Excellent                   | Excellent                   | Moderate       |
| C# + WinUI 3  | Excellent, modern Fluent UI | Excellent                   | Moderate–high  |
| C# + WinForms | Native, traditional UI      | Excellent                   | Low            |
| C++ + Qt      | Excellent                   | Excellent                   | High           |
| Tauri + Rust  | WebView-based UI            | Excellent with Rust backend | Moderate–high  |

My reasoning:

- WPF: Best balance of maturity, documentation, and Windows integration.
- WinUI 3: Attractive if Windows 11 visual design is your highest priority.
- WinForms: Excellent for a quick utility, but less suitable for the modern messenger interface I envision.
- Qt: Excellent if cross-platform support becomes a serious requirement.
- Tauri: Good if you want to reuse your HTML, CSS, and JavaScript knowledge.

I'd avoid starting with Electron. It introduces a bundled browser runtime and more overhead than this application needs.

## 3. High-level system architecture

Every installed computer is both a client and a network peer.

## Roomie architecture

Windows desktop application

WPF · MVVM · Native notifications · System tray

Application services

Peer discovery

Connection manager

Messaging service

Group manager

File transfer

Presence manager

Chat history

Notification service

Network layer

UDP

Discovery

TCP + TLS

Communication

SQLite

Users · Chats · Messages

File storage

Attachments · Downloads

Each peer contains the same components.

### Suggested project structure

Keep the UI separate from the networking and business logic. This makes the system easier to test and maintain.

```
Roomie/
│
├── src/
│   ├── Roomie.App/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   ├── Resources/
│   │   └── App.xaml
│   │
│   ├── Roomie.Core/
│   │   ├── Models/
│   │   ├── Interfaces/
│   │   ├── Events/
│   │   └── Protocol/
│   │
│   ├── Roomie.Network/
│   │   ├── Discovery/
│   │   ├── Connections/
│   │   ├── Messaging/
│   │   ├── Security/
│   │   └── FileTransfer/
│   │
│   ├── Roomie.Infrastructure/
│   │   ├── Persistence/
│   │   ├── Repositories/
│   │   ├── Storage/
│   │   └── Logging/
│   │
│   └── Roomie.Tests/
│
└── docs/
    ├── architecture.md
    ├── network-protocol.md
    └── security.md
```

This is a modular application, not microservices. All modules run within the same desktop process initially.

## 4. LAN peer discovery — how computers find each other

This is one of the most interesting parts of the project.

Imagine three computers connected to the same Wi-Fi router or Ethernet switch.

PC A should automatically discover PC B and PC C without requiring users to enter IP addresses.

### Recommended mechanism: UDP broadcast

UDP allows a computer to send a small packet to other computers on the local subnet without establishing a connection first.

Discovery process:

1. Application starts

   PC A starts its UDP discovery listener.
2. Broadcast announcement

   PC A broadcasts a discovery packet on the local network.
3. Other computers receive it

   PC B and PC C receive the packet and recognize the application.
4. Peer response

   PC B and PC C respond with their peer identifiers, display names, protocol versions, and listening ports.
5. Secure connection

   PC A attempts a TCP connection to the responding peers and performs authentication and TLS negotiation.
6. Presence updates

   Peers periodically announce their availability. Missing announcements eventually result in an offline status.

### Discovery packet example

```
{
  "type": "peer_announce",
  "protocolVersion": 1,
  "peerId": "unique-device-id",
  "displayName": "Office PC",
  "tcpPort": 45821,
  "appVersion": "1.0.0"
}
```

This is illustrative protocol data, not an authentication credential.

### Discovery settings

| Parameter                  | Suggested starting value |
| -------------------------- | ------------------------ |
| UDP discovery port         | 45820                    |
| TCP messaging port         | 45821                    |
| Announcement interval      | 5 seconds                |
| Offline timeout            | 20 seconds               |
| Discovery response timeout | 2 seconds                |

These are application-defined values; the ports should be configurable.

Important networking considerations:

- UDP broadcast generally works within the local subnet, not across routers or VLANs.
- Wi-Fi access points may have client isolation enabled, preventing devices from communicating.
- Windows Firewall must allow the application to receive the required LAN traffic.
- Multiple network adapters may exist; discovery should operate on suitable active network interfaces.
- You can add mDNS as an alternative discovery mechanism later.

UDP announcements are not proof of identity. Never trust a peer simply because it broadcasts a valid-looking packet.

## 5. Message transport — how chat actually works

Once two peers discover each other, they establish a TCP connection.

### Example: PC A sends a message to PC B

1. User types a message on PC A.
2. The application creates a message object.
3. It assigns a unique message ID.
4. It stores the outgoing message in SQLite.
5. It sends the message through the encrypted TCP connection.
6. PC B receives and validates it.
7. PC B stores it in its local SQLite database.
8. PC B sends an acknowledgement.
9. PC A updates the message delivery status.

Example protocol message:

```
{
  "type": "chat_message",
  "messageId": "msg-001",
  "senderId": "peer-a",
  "recipientId": "peer-b",
  "conversationId": "chat-001",
  "timestamp": "2026-10-30T12:00:00Z",
  "content": "Hello! Are you available?"
}
```

### Important: TCP is a stream, not a message protocol

TCP doesn't preserve the boundaries of individual application messages.

Your application needs message framing. A practical design is:

```
[4-byte message length][JSON or binary payload]
```

The receiver reads the length first, then reads exactly that many bytes.

Use a maximum frame size, strict validation, and separate streaming rules for large files.

For text messages, use JSON initially. For large binary transfers, use a dedicated binary frame format.

## 6. Group chat without a central server

This is where P2P requires some careful design.

Suppose four computers are participating in one group:

Group: Development Team

PC A · Alice

PC B · Bob

PC C · Charlie

PC D · David

Each participant maintains its own group state and local history.

When Alice sends a group message:

- Her application sends the message directly to each currently connected group member.
- Each recipient stores the message locally.
- Each recipient acknowledges receipt.
- Alice tracks acknowledgements per recipient.

For V1, use a full-mesh group model with one-to-one connections reused for group messages.

Each group should have:

- Unique group ID.
- Group name.
- Member IDs.
- Group creation timestamp.
- Message history.
- Group metadata version.

There is an important limitation: in a decentralized system, members may disagree about group membership if changes happen simultaneously. For V1, make group membership changes explicit, versioned, and accepted by the participating peers. Don't try to implement a sophisticated distributed consensus system.

Also, don't promise reliable offline delivery yet. If Bob is offline, Alice can retain the outgoing message and retry when Bob reconnects. But if no peer is online to hold a message for him, a purely P2P system cannot deliver it immediately.

## 7. File transfer architecture

File transfer should not be implemented by embedding file contents inside chat JSON.

Use a separate streaming process.

File transfer workflow

1. Sender selects a file.
2. Sender creates a transfer ID and sends file metadata.
3. Receiver accepts or rejects the transfer.
4. Receiver sends an acceptance acknowledgement.
5. Sender streams file data over the encrypted TCP connection.
6. Receiver writes chunks to a temporary file.
7. Receiver verifies the SHA-256 hash.
8. File is moved into the final download directory.

### Suggested transfer protocol

```
FILE_OFFER
    ↓
FILE_ACCEPT / FILE_REJECT
    ↓
FILE_TRANSFER_START
    ↓
FILE_CHUNK
    ↓
FILE_PROGRESS
    ↓
FILE_TRANSFER_COMPLETE
    ↓
HASH_VERIFICATION
    ↓
FILE_ACKNOWLEDGEMENT
```

Recommended initial constraints:

| Parameter            | V1 recommendation               |
| -------------------- | ------------------------------- |
| Maximum file size    | 100 MB                          |
| Chunk size           | 64 KB                           |
| Concurrent transfers | 2 per peer                      |
| Hash algorithm       | SHA-256                         |
| Storage              | Dedicated Downloads folder      |
| Interrupted transfer | Mark incomplete; retry manually |

Later, introduce resumable transfers using byte offsets and chunk verification.

Security: Never automatically execute received files. Sanitize filenames, prevent path traversal, enforce size limits, and require explicit acceptance before downloading.

## 8. Security architecture

Even though your messenger operates only on a LAN, you must not assume the LAN is trusted.

Someone connected to the same office Wi-Fi could potentially attempt to impersonate a peer, send malicious messages, or offer malicious files.

I'd build security into the foundation rather than adding it after the messenger works.

### Recommended security layers

| Area               | Protection                                         |
| ------------------ | -------------------------------------------------- |
| Peer identity      | Persistent cryptographic identity per installation |
| Connection         | TLS using `SslStream`                              |
| Peer verification  | Certificate fingerprint verification               |
| Message integrity  | TLS integrity protection                           |
| Message validation | Schema validation and size limits                  |
| File transfer      | Explicit acceptance + SHA-256 verification         |
| Local database     | Windows user-specific file permissions             |
| Sensitive settings | Windows DPAPI                                      |
| Network exposure   | Private LAN only, controlled firewall rules        |

### Peer pairing

For the first connection between two computers:

1. PC A discovers PC B.
2. PC A requests a secure connection.
3. PC B presents its identity certificate.
4. PC A displays a verification code or fingerprint.
5. The user confirms the peer.
6. Both devices save the trusted identity.

Subsequent connections can verify the saved identity automatically.

This prevents someone from simply changing their display name to impersonate another computer.

### TLS connection

Use .NET's `SslStream` for encrypted communication. Configure certificate validation explicitly; do not accept every self-signed certificate. Use modern TLS defaults and test the negotiated protocol on supported Windows versions.

### Windows Firewall

Your application needs inbound and outbound LAN communication.

Do not tell users to disable Windows Firewall.

Instead:

- Request a firewall exception during installation or first launch.
- Restrict the exception to Private networks.
- Allow only the executable and necessary protocols/ports.
- Explain why network access is required.

Microsoft specifically recommends allowing the application rather than broadly opening ports wherever possible.

One additional consideration: a Windows network being marked Private does not mean every device on it is trustworthy. Peer authentication is still necessary.

## 9. Local persistence — SQLite database

Each computer maintains its own database.

This is essential to the P2P design: there is no central database containing everyone's chat history.

Suggested storage:

```
%LOCALAPPDATA%/
    Roomie/
        database/
            messenger.db

        files/
            downloads/
            attachments/

        logs/
            application.log

        config/
            settings.json

        identity/
            peer-certificate.pfx
```

### Database design

Core entities

Peers

`peer_id`, `display_name`, `certificate_fingerprint`, `last_seen`, `trust_status`

Conversations

`conversation_id`, `type`, `title`, `created_at`

ConversationMembers

`conversation_id`, `peer_id`, `role`, `joined_at`

Messages

`message_id`, `conversation_id`, `sender_id`, `content`, `created_at`, `delivery_status`

FileTransfers

`transfer_id`, `message_id`, `filename`, `file_size`, `sha256`, `status`

Also consider:

- `MessageReceipts` for per-recipient acknowledgements.
- `PendingDeliveries` for retrying messages when peers reconnect.
- `Groups` and `GroupMembershipEvents` for versioned membership changes.
- `Settings` for local preferences.

### SQLite configuration

I'd use:

- Foreign key enforcement.
- WAL mode.
- Prepared statements.
- Database transactions for message persistence.
- Appropriate indexes on conversation IDs and timestamps.
- Periodic backups.

SQLite WAL allows readers and a writer to operate concurrently, but it is intended for processes accessing the database on the same machine, not sharing a WAL database over a network drive.

Important: Chat history on different peers is not automatically identical. Delivery acknowledgements, message synchronization, and conflict handling must be explicitly designed.

## 10. Windows application UI/UX

I would avoid making this look like a web dashboard wrapped in a desktop window.

The goal should be a proper Windows messaging application.

### Proposed layout

Roomie

Chats

Alice

Hello!

Online

Bob

Sending a file...

Development Team

Group chat

Available peers

Charlie · Online

David · Offline

Alice

Connected · Secure

Hey, are you available?

Yes! What's up?

Type a message...

Network: Connected

3 peers online

Conceptual interface layout, not a final visual design.

### Main screens

1. Chat: One-to-one conversations and group conversations.
2. Contacts: Discovered peers, trusted peers, online/offline status.
3. Groups: Create groups and manage membership.
4. Transfers: Incoming and outgoing file transfers.
5. Settings: Display name, network, downloads, notifications, and security.
6. About: Application version, peer ID, and network diagnostics.

### Windows-specific features

Use native Windows integration for:

- System tray icon and context menu.
- Toast notifications.
- File picker and folder picker.
- Clipboard copy/paste.
- Drag-and-drop file sharing.
- Windows startup option.
- Minimize to tray.
- Dark/light theme.
- Taskbar notifications and unread indicators.

## 11. Deployment and distribution

For the first release, I recommend MSIX packaging with a self-contained .NET deployment option.

Windows installation

Roomie.msix

Signed Windows installer package

PC A

Application installed

PC B

Application installed

No separate server installation required.

Deployment considerations:

| Area               | Recommendation                   |
| ------------------ | -------------------------------- |
| Package            | MSIX                             |
| Runtime            | .NET self-contained              |
| Architecture       | x64 initially                    |
| Installer signing  | Trusted code-signing certificate |
| Data storage       | LocalAppData                     |
| Updates            | Signed package updates           |
| Network permission | Private LAN                      |
| Logs               | Local rolling logs               |

MSIX packaging is convenient, but be aware that firewall configuration, certificate trust, and local network permissions need testing with the actual packaged application. A classic installer such as WiX is a reasonable alternative if you need more control over installation and firewall setup.

## 12. Phased implementation roadmap

> **Superseded.** The authoritative 12-phase roadmap (identity and pairing before messaging) is in `architecture.md` §14 (same folder). This list is kept for context only.

I strongly recommend building the networking engine before investing heavily in UI design.

A beautiful messenger that cannot reliably discover another computer isn't a messenger yet.

1. Phase 0 — Project foundation

   First task
   - Install Visual Studio 2026 and .NET 10 SDK.
   - Create WPF solution.
   - Configure MVVM and dependency injection.
   - Establish project structure.
   - Create initial application window.
   - Set up Git and logging.
   Deliverable: A clean, running Windows desktop application.
2. Phase 1 — LAN discovery engine
   - Implement UDP discovery.
   - Detect active network interfaces.
   - Broadcast discovery announcements.
   - Receive peer responses.
   - Display discovered computers.
   - Implement heartbeat and offline detection.
   Deliverable: Two computers can automatically discover each other.
3. Phase 2 — Peer connections and messaging
   - Implement TCP listener and connection manager.
   - Implement message framing.
   - Add TLS and peer verification.
   - Create message protocol.
   - Send and receive text messages.
   - Implement acknowledgements and duplicate detection.
   Deliverable: Secure one-to-one messaging between computers.
4. Phase 3 — SQLite and chat history
   - Create database schema.
   - Save messages.
   - Load conversation history.
   - Track message status.
   - Implement pending message retries.
   - Add conversation search.
   Deliverable: Messages survive application restarts.
5. Phase 4 — Group messaging
   - Create groups.
   - Add and remove members.
   - Implement group message fan-out.
   - Track delivery per recipient.
   - Store group conversations locally.
   - Handle group membership changes.
   Deliverable: Multiple computers can communicate in a group.
6. Phase 5 — File transfer
   - Implement file offer/acceptance.
   - Stream file chunks.
   - Display transfer progress.
   - Verify SHA-256.
   - Handle cancelled and interrupted transfers.
   - Add safe file storage.
   Deliverable: Computers can exchange files directly.
7. Phase 6 — Native Windows experience
   - Build final WPF interface.
   - Add system tray integration.
   - Add notifications.
   - Implement drag-and-drop.
   - Add dark/light themes.
   - Add startup and minimize-to-tray settings.
   Deliverable: A polished Windows messenger.
8. Phase 7 — Packaging and release
   - Configure MSIX.
   - Add application signing.
   - Test firewall behavior.
   - Test installation and upgrades.
   - Test multiple PCs and network configurations.
   - Prepare release documentation.
   Deliverable: An installable Roomie for Windows.

## 13. Testing strategy

Don't test only on localhost. A P2P application needs real computers.

| Test                       | Expected result                         |
| -------------------------- | --------------------------------------- |
| Two PCs on Ethernet        | Peer discovery works                    |
| Two PCs on same Wi-Fi      | Peer discovery works                    |
| Ethernet + Wi-Fi           | Correct interface selection             |
| Internet disconnected      | LAN messaging still works               |
| Peer closes application    | Offline status updates                  |
| Peer restarts              | Automatic rediscovery                   |
| Duplicate messages         | No duplicate chat entries               |
| Large file                 | Transfer completes correctly            |
| Interrupted file transfer  | Incomplete transfer is handled safely   |
| Firewall blocks connection | Clear connection error                  |
| Untrusted peer             | Connection requires verification        |
| Different subnet           | No false promise of automatic discovery |

Also test simultaneous connections, sleep/resume, IP address changes, network adapter changes, malformed packets, and a peer sending oversized payloads.

## 14. Important architectural decisions and limitations

There are four things I would deliberately avoid in V1.

1\. Don't build a distributed database.

Each computer stores its own conversation history. Add synchronization and conflict resolution incrementally.

2\. Don't build custom cryptography.

Use established TLS and cryptographic libraries. Certificate verification and pairing deserve careful implementation.

3\. Don't use UDP for chat messages.

UDP is appropriate for discovery, not reliable message delivery or large file transfers.

4\. Don't introduce a central server just to solve every problem.

A P2P application has limitations around offline delivery, membership coordination, and history synchronization. Accept those limitations in V1 instead of accidentally turning the project into a client-server application.

## 15. Estimated development effort

For one developer working part-time:

| Phase                 | Estimated effort |
| --------------------- | ---------------- |
| Foundation            | 2–3 days         |
| Discovery             | 3–5 days         |
| Secure messaging      | 5–8 days         |
| Persistence           | 3–5 days         |
| Group messaging       | 4–6 days         |
| File transfer         | 5–7 days         |
| UI polish             | 5–8 days         |
| Packaging and testing | 3–5 days         |

Rough planning estimates, not guaranteed delivery dates. Security testing and networking troubleshooting can add considerable time.

## 16. Final technology references

These are the official resources I'd use while building the application.

- [WPF documentation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)

- [.NET support lifecycle](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) — .NET 10 is an LTS release supported until November 2028.

- [.NET Sockets API](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets)

- [SslStream documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream)

- [Windows MSIX packaging](https://learn.microsoft.com/en-us/windows/msix/)

- [SQLite WAL documentation](https://sqlite.org/wal.html)

## My final recommendation

Approved starting direction

# Roomie

Native Windows · Decentralized · Offline-capable

C# + .NET 10 + WPF + UDP + TCP/TLS + SQLite

Build a secure, local-first, peer-to-peer messaging application with no central server dependency.

The first milestone should be very small:

Install the application on two Windows computers → launch both → automatically discover each other → establish a secure connection → exchange a text message.

## One decision before we start implementation

I would like to establish the product's identity model before writing the network protocol.

How should users identify themselves?

1\. User identity

Username + automatically generated device identity

Username + password

Display name only, no account system

Invite-based identity with trusted peer pairing

2\. Network scope

Same Wi-Fi / Ethernet subnet only

Multiple LAN subnets / VLANs

LAN initially, internet connectivity later

Finalize architecture decisions

I'd settle these decisions first, then build the project one phase at a time, testing each phase on two actual Windows computers before proceeding.
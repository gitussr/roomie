# Roomie

Roomie is a peer-to-peer LAN messenger for Windows. It needs no server, internet connection or cloud account. Built with C#, .NET 10 and WPF.

## Status

- **Phase 0 (foundation), done.** A WPF app on the .NET Generic Host with dependency injection, MVVM (CommunityToolkit.Mvvm), rolling file logs and the local data layout.
- **Phase 1 (device identity), done.** On first launch each install generates a random device ID, an ECDSA P-256 key pair and a self-signed X.509 certificate. Peers will pin the certificate's public-key fingerprint (`SHA256:…`). The private key is encrypted with DPAPI for the current Windows user, so an identity folder copied to another PC or account can't be decrypted there. The app moves it aside and creates a new identity instead. The username can be changed without affecting the identity.
- **Phase 2 (UDP discovery), done.** Roomie opens one UDP socket (port 45820) per active adapter with a private or link-local IPv4 address. It broadcasts a `DISCOVERY_REQUEST` at startup and after network changes, announces itself every 5 seconds, and answers requests by unicast after a random 0–500 ms delay, at most once per requester every 2 seconds. Packets are strictly validated (size, protocol, version, device ID, username, port, source subnet). Devices are keyed by device ID, so one heard over Wi-Fi and Ethernet appears once, and they drop off after 20 seconds of silence. Discovered devices are listed as *not paired*: discovery never connects or trusts anything. If another computer announces this computer's own device ID, a warning is logged.
- **Phase 3 (connections and TLS), done.** Roomie accepts TCP connections on port 45821 on each LAN interface and can connect to a discovered device. Every session is mutually authenticated TLS with both device certificates: TLS 1.3 where Windows supports it, otherwise TLS 1.2 restricted to ECDHE-ECDSA with AES-GCM (Windows 10 has no TLS 1.3; see architecture.md §10). Certificates are checked for Roomie's exact format (self-signed ECDSA P-256, valid signature and dates, device ID in the subject). A device's pinned fingerprint is enforced, and a mismatch is reported as an identity change. After TLS, both sides exchange `HELLO` over length-prefixed RMP frames with strictly validated JSON envelopes. Heartbeats every 5 seconds close a silent session after 15. When both sides connect at once, the session opened by the smaller device ID wins. Paired devices reconnect automatically with backoff. Unpaired devices may only send session and pairing messages, and their session count is limited. There's no pairing yet, so every session shows as *not paired*.
- **Phase 4 (pairing), done.** Choose **Pair…** next to a nearby device. The two computers run the commit-then-reveal exchange from architecture.md §3.1 over the TLS session and each shows a six-digit code derived from it. When both people confirm the codes match, each computer pins the other's certificate fingerprint and the session becomes trusted. Approval on only one side pairs neither. The other side can decline or block. An attempt expires after 2 minutes, and repeated failures are rate-limited and close the session. Paired devices are saved in `config\trusted-devices.json`, so they reconnect automatically after a restart. **Forget…** unpairs a device and **Unblock** reverses a block.
- **Phase 5 (RMP protocol), done.** Every message type now has central rules: whether it needs a paired device, which capability it belongs to, and how large it may be. Text bodies are limited to 16 KiB and file offers to 100 GB. Validated messages go to the one service that owns their type. An unknown or refused message gets an `ERROR` and the session stays open, up to 30 refusals a minute, and a failing handler no longer harms the session. `HELLO` now negotiates the protocol version and the capabilities both sides offer. The order of the checks is in architecture.md §5.4.
- **Phase 6 (messaging), done.** Choose **Chat** next to a paired device to send it text. Each message shows *Waiting to send*, *Sent*, *Delivered* (the other PC stored it) or *Read* (someone there had the conversation open). A message to an offline device waits and goes out when it reconnects. Messages that may have been lost are sent again, and duplicates are never stored twice. The rules are in architecture.md §6.
- **Phase 7 (saved messages), done.** Messages are saved in `database\messenger.db` (SQLite), so history, unread counts, messages still waiting and read receipts still owed all survive closing Roomie, a crash or a power cut. Every message is on disk before it is sent or acknowledged. A damaged database, or one from a newer Roomie, is refused with an explanation and never overwritten. The database isn't encrypted: it's protected by your Windows account, like other files in your profile. Forgetting a device keeps its messages. The rules are in architecture.md §6.
- **Phase 8 (groups), done.** Type a name under **Groups** and choose **New group**, then open **Members** to invite paired devices. An invited device joins only if its user chooses **Join**. Each member's copy of a message is delivered and tracked separately, so one offline member holds nobody else up. Only the owner can add or remove members, and every change is signed with the owner's device key, so any member can pass it on but none can forge it. Being in a group creates no trust: a member you haven't paired with gets your messages only once you pair. A removed device keeps its history but can't send. There is no leaving a group or changing its owner in this version. The rules are in architecture.md §7.
- **Phase 9 (file transfer), done.** Open **Chat** with a paired device and choose **Send file…**, up to 100 GB. The other side chooses **Accept** or **Decline**, and nothing is saved before that. Files arrive in `files\downloads` under Roomie's data folder, only after their SHA-256 matches, and never overwrite anything. Roomie never opens a received file: it offers **Show in folder**, and marks the file so that Windows warns before running it. An interrupted transfer pauses, and continues by itself from where it stopped when the devices next connect, even after closing Roomie or a power cut. Files go to one device at a time, not to a group. The rules are in architecture.md §8.
- **Next: Phase 10,** the finished WPF interface and native Windows integration.

### Testing file transfer on two PCs

Run the same 0.9.1 build on both PCs, because a device on 0.9.0 can't exchange files with it. Your paired devices, messages and groups carry over, and the database is upgraded on first run, after which older versions refuse it.

1. On PC A, open **Chat** with PC B and choose **Send file…**. B should come to the front, with the file under **Files** and a count on **Chat**. Nothing should be in B's `files\downloads` yet.
2. **Decline** on B: A should show *They declined*, with **Send again**. Send again and **Accept**: both should end on *verified*. **Show in folder** on B should open the folder with the file selected. Check the file opens and is intact.
3. Send a file of several GB, and chat in both directions while it moves. The button should say *Checking file…* until the offer appears. The progress bar should move, and messages should still arrive quickly.
4. Send a large file and choose **Cancel** on the receiver part-way, then on the sender. Both should say *Canceled*, nothing should be in `files\downloads`, and chat should keep working without reconnecting.
5. Send a large file and pull the network cable part-way. Both should show *Paused at…* within about 15 seconds. Plug it back in: it should continue from about the same point by itself, end on *verified*, and the file should open intact.
   - Do the same again, but close Roomie on the receiver part-way, then open it again. Then again, closing it on the sender.
   - Cancel while paused, once on each side. The other side should say *Canceled* after reconnecting, and `files\incoming` should be empty.
   - Change the file on the sender while paused. It should fail, saying the file changed, rather than continue.
6. With B closed, send a file from A: it should say it will be offered when the device is connected. Open B: the offer should appear.
7. Send a `.exe` or `.bat`. B should show a warning before accepting. After accepting, running it from the folder should bring up the Windows warning about files from elsewhere.

### Testing groups on two PCs

Run the same build on both PCs. Your paired devices and saved messages carry over.

1. On PC A, type a name under **Groups**, choose **New group**, open **Members** and choose **Invite** next to PC B.
2. PC B should come to the front and show the invitation. Choose **Decline**: nothing should change on A, and B shouldn't be asked again until A invites again. Invite again and choose **Join** on B. Both should show two members.
3. Send messages each way in the group. The sender should see *Delivered to 1 of 1*, and *read by 1* once the other side has it open. Messages should not appear in the one-to-one **Chat** with that device, and that chat should still work.
4. Keep the group closed on B and send from A: the **Open** button and window title on B should show an unread count.
5. Close Roomie on B and send from A: it should show *waiting for* B. Close and reopen A, then open B: it should arrive once.
6. On A, choose **Remove** next to B. B should say it was removed, keep its history and be unable to send, and A's next message should go to nobody. Invite B again, and it should be able to rejoin.

Two PCs can't exercise a third member, so groups of three or more, relaying between members and members who aren't paired with each other are covered only by automated tests.

### Testing saved messages on two PCs

Run the same build on both PCs. Devices paired on an earlier version stay paired.

1. Send a few messages each way, then close Roomie on both PCs and open it again. The history should still be there, with the same *Delivered* or *Read* states and in the same order.
2. On PC A, keep the conversation closed while PC B sends a message. Close Roomie on A and reopen it: the unread count should still show.
3. Close Roomie on PC B. On PC A, send a message: it should show *Waiting to send*. Close Roomie on A too. Open B, then open A: the message should reach *Delivered* within about 30 seconds and appear once.
4. With B closed, send another message from A and end Roomie on A in Task Manager (**End task**). Reopen both: it should still arrive, once.
5. Pull the cable on PC B just after A sends. When it comes back, the message should appear once on B, even if A sends it again.

### Testing messaging on two PCs

Run the same build on both PCs. Devices paired on an earlier version stay paired.

1. Choose **Chat** next to the other PC on both, and send a message each way. It should show *Delivered*, then *Read* within a moment because the conversation is open on the other side.
2. On the receiving PC, close the conversation or click another window, and send it a message. It should stop at *Delivered*, and the receiver's taskbar title and **Chat** button should show an unread count. Opening the conversation clears the count and the sender sees *Read*.
3. Try Hindi text, emoji and several lines (Shift+Enter).
4. Pull the network cable on one PC and send a message from the other. It should show *Waiting to send* or *Sent*. Reconnect the cable: it should become *Delivered* within about 30 seconds and appear only once.
5. Try pasting more than 16 KB of text: it should say the message is too long and keep your text.

### Testing pairing on two PCs

1. Run the same build on both PCs. Choose **Pair…** next to the other PC on one of them.
2. The other PC comes to the front, or its taskbar button flashes. Both show the same six-digit code.
3. Choose **The codes match** on both. Both should then say *Paired*, and the device line should say *Connected · paired*.
4. Close Roomie on one PC and open it again. The two should reconnect as *paired* by themselves within a few seconds.
5. To test the safeguards, try **The codes don’t match** on one side (neither PC is paired), and try **Forget…** (the session ends and the device goes back to *not paired*).

### Testing connections on two PCs

Run the same build on both PCs. Allow Roomie through Windows Firewall on private networks; it now accepts TCP 45821 as well as UDP 45820. Click **Connect** on a nearby device. Both PCs should show *Connected · not paired*, the TLS version, and the first groups of the other side's key fingerprint, which should match the *Security fingerprint* shown on the other PC. **Disconnect** clears it on both sides at once. Pulling the network cable clears it within about 15 seconds.

### Testing discovery on two PCs

Run Roomie on two Windows PCs on the same network. When Windows Firewall asks, allow Roomie on **private** networks, and make sure the network profile is Private. Each PC should list the other under *Nearby devices* within a second or two, and remove it about 20 seconds after it closes. Router "client isolation" (common on guest Wi-Fi) blocks discovery entirely.

## Build and run

Requires the .NET 10 SDK on Windows.

```
dotnet build
dotnet test
dotnet run --project src/Roomie.App
```

## Layout

| Project | Responsibility |
| ------- | -------------- |
| `Roomie.App` | WPF views, view models, composition root (`App.xaml.cs`) |
| `Roomie.Core` | Models, interfaces, protocol definitions; no dependencies |
| `Roomie.Network` | Discovery, connections, TLS, RMP messaging, file transfer |
| `Roomie.Infrastructure` | SQLite persistence, local storage, identity key storage |
| `Roomie.Tests` | xUnit tests |

`Network` and `Infrastructure` depend only on `Core`; `App` wires everything together.

Runtime data lives in `%LOCALAPPDATA%\Roomie\` (`database`, `files`, `logs`, `config`, `identity`). `identity\` holds `device.crt` (public certificate), `device.key` (DPAPI-encrypted private key) and `identity.json` (public metadata). Clearing chat data never touches it.

## Documentation

- [`docs/architecture.md`](docs/architecture.md): identity, pairing and the RMP protocol specification. This is authoritative.
- [`docs/pre-architecture.md`](docs/pre-architecture.md): the original high-level blueprint.

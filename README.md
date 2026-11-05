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
- **Phase 9 (file transfer), done.** Open a chat with a paired device and choose **Send file…**, up to 100 GB. The other side chooses **Accept** or **Decline**, and nothing is saved before that. Files arrive in the folder chosen in Settings, only after their SHA-256 matches, and never overwrite anything. Roomie never opens a received file: it offers **Show in folder**, and marks the file so that Windows warns before running it. An interrupted transfer pauses, and continues by itself from where it stopped when the devices next connect, even after closing Roomie or a power cut. Files go to one device at a time, not to a group. The rules are in architecture.md §8.
- **Phase 10 (Windows interface), done.** Roomie now has **Chats**, **Devices**, **Transfers** and **Settings** screens, light and dark themes that can follow Windows, and an icon in the notification area. Closing the window leaves Roomie running so messages and files keep arriving, and it can start when you sign in. Windows notifications announce messages, files and pairing requests, and the taskbar button flashes and shows a count and a transfer's progress. Files can be dragged onto a conversation or pasted into it. Received files now go to your Windows Downloads folder by default, or any folder you choose, and an unfinished file is kept on that same drive. Opening Roomie a second time shows the one already running. The rules are in architecture.md §8 and §15.
- **Profile pictures and logo reveal (0.11.0).** In **Settings**, choose one of 12 built-in avatars or upload your own picture. Paired devices see it in their chats and device list. Roomie opens with a short animated logo reveal, which any click or key skips. It doesn't play when Roomie starts by itself at sign-in or when the window comes back from the notification area, or if animations are off in Windows. Details are in architecture.md §15.
- **First-run welcome and emoji (0.12.0).** A new install walks you through naming the computer and pairing with another, and any step can be skipped. Someone updating with a paired device doesn't see it, and **Settings** > **About** can show it again. The **😀** button by the message box adds an emoji where the cursor is. Windows 10's `Win`+`.` panel works there too. Emoji are ordinary text, so older versions of Roomie receive them as usual.
- **Sounds and small animations (0.13.1).** Most Windows PCs can't vibrate, so these give Roomie its feel instead. Short, soft sounds announce a message in a conversation you aren't looking at, a file offered, a transfer finished or failed, and a pairing request. **Settings** > **Notifications** turns them off and can play a test sound, and they stay silent during Focus Assist and full-screen games or presentations. A message you send rises into place, an unread count pops, buttons respond to being pressed, a finished transfer shows a tick, and the drop area fades in. All the animations are off when animations are off in Windows.
- **Phase 11 (security, network testing and installer), 0.15.2, built and waiting for your test.** 0.15.2 keeps the notification icon from holding up the window, which took over a minute to open on one PC. 0.15.1 shows only the status under a name in a chat. 0.15.0 lets a group's owner delete it and a member leave it, and sends a device's new name to its paired devices. It also logs each step of startup, because on one PC the window never appeared after installing 0.14.0 and the cause is still unknown. It updates the message database, so an older Roomie can't open that data afterwards. A security review found and fixed eight problems, none of them in pairing or encryption: see [`docs/security-review.md`](docs/security-review.md). Roomie now follows the Windows high-contrast setting, and screen readers get sensible names for list rows. It also comes as an installable MSIX package, alongside the portable zip, which adds its own firewall rules and keeps your existing messages and pairings.

### Testing 0.15.2 on the office PCs

`packaging\build-msix.ps1` puts everything in `Desktop\Roomie-0.15.2`. Install it on every PC, because older versions can't take part in deleting, leaving or new names: they hear about them once they update. Copy that folder to each PC. Use as many of the PCs as you can, because three is the most that has been tested so far. A portable copy and the installed one share their data, so choose **Exit Roomie** in the old one first, and don't run both.

**Installing**

1. Double-click **Install Roomie.cmd** and allow the Windows prompt. Roomie should appear in the Start menu. If Windows refuses, your office's policy may block this kind of install: please send the message. The portable zip still works on that PC.
2. Open it on a PC that already used Roomie. **Settings** > **About** should show 0.15.2, with your messages, paired devices and security fingerprint unchanged. If paired devices now show an identity warning, stop and report it. If no window appears within 30 seconds, don't uninstall: send the end of the newest file in `%LOCALAPPDATA%\Roomie\logs`, which says how far Roomie got.
3. Windows Firewall shouldn't ask anything. Run `Get-NetFirewallRule | Where-Object DisplayName -like '*Roomie*'` in PowerShell: there should be rules for private and domain networks. Then run `Get-NetConnectionProfile`: `NetworkCategory` should be `Private` or `DomainAuthenticated`. The rules don't cover `Public`, so other PCs may not reach the installed Roomie there, and the install script warns about this but doesn't change it. If it says `Public` and this is your trusted office network, set it to **Private** under **Settings** > **Network & Internet** > **Properties**, or ask your IT team. Connect from another PC to confirm the rules work.
4. Turn on **Start Roomie when I sign in to Windows**, then sign out and in: it should be in the notification area with no window.
5. Run the installer again: it should finish without losing anything. Uninstall from **Settings** > **Apps**, and install again: your data should still be there.

**Network**

6. Pair and chat across all the PCs, including one group with everyone in it. Messages should arrive once, on every PC.
7. Run a transfer of several GB and pull the cable part-way on a real pair of PCs, then plug it back in. It should pause within about 15 seconds and continue by itself. Repeat by closing Roomie on the receiver.
8. Repeat 6 and 7 with one PC on Wi-Fi, moving it out of range if you can. Then send from Wi-Fi to cable, with chat running both ways. Note the speed.
9. If a PC doesn't see the others, note how it connects. Guest Wi-Fi and separate office networks (VLANs) block discovery, and Roomie has no way yet to enter an address by hand.

**The security fixes**

10. On a paired device, change its name in **Settings**. Within a few seconds the other PCs should show the new name in **Chats** and **Devices**, and in any group with it. Do it again with one PC off: that PC should show the new name soon after it reconnects.
11. Between two unpaired PCs, start pairing and choose **Cancel**, six times in ten minutes. The sixth should be refused with a message about too many attempts.

**Groups**

14. As a member, open a group and choose **Leave group**. It should say you left, and the other PCs should drop you from **Members** within a few seconds. Repeat with the owner's PC off: the owner should drop you after it reconnects. The owner can then invite you back.
15. As the owner, choose **Delete group**. Every member's copy should say it was deleted and refuse new messages, with old messages still there. Repeat with one member's PC off, and with the owner closing Roomie straight after: the member should still find out when it reconnects, from the owner or another member.
16. In an ended group, choose **Remove from this computer**. The group and its messages should go from that PC only.

**Accessibility**

12. Turn on a high-contrast theme (`Left Alt`+`Left Shift`+`Print Screen`, and the same keys to turn it off). Roomie should change straight away, with every screen readable and the open conversation outlined.
13. If anyone uses a screen reader, try Narrator (`Win`+`Ctrl`+`Enter`) on **Devices** and **Chats**: it should read names and status, not device IDs.

### Testing sounds and animations on two PCs

Choose **Exit Roomie** from the notification-area icon first. If a different version is still running, the new one says so and doesn't open. Run 0.13.1 on both PCs, with the speakers on, and check **Settings** > **About** shows 0.13.1. **Settings** > **Notifications** > **Play a test sound** should play one on each PC, also with sounds turned off. If it's silent, check the PC's volume first. Only your own ears can say whether these sounds are right, so please say if any is too loud, too quiet or annoying.

Your own picture now shows at the bottom of the left rail, above **Settings**. Change it in **Settings**: the rail should update straight away, and the other PC should show the same picture.

1. With the chat with PC B open and in front on PC A, send a message from B: no sound on A. Put another window in front, or open a different chat, and send again: a sound. Send several quickly: only one sound in about 5 seconds.
2. Offer a file from B: a different, rising sound on A. Accept it. When it finishes, both PCs should play a brighter sound and show a green tick that pops in on that transfer. Older finished transfers should show the tick without any movement.
3. Make a transfer fail, for example by receiving a file too large for the chosen drive. A low, falling sound should play. Canceling and declining are silent.
4. Start pairing from B: A should play a repeated two-note sound.
5. In **Settings** > **Notifications**, turn sounds off: none of the above should sound. Turn them on again: it should play one so you can hear it. Close and reopen Roomie: the choice should stay.
6. Turn on Focus Assist (the action centre, or **Settings** > **System** > **Focus assist**), in **Priority only** and then **Alarms only**, and send a message: no sound. Turn it off: the sound returns.
7. Send a message: its bubble should rise and fade in once. It shouldn't move again when its status changes to *Delivered*, or when you close and reopen the chat.
8. With the window behind another, send messages from B: the count on **Chats** and on that conversation should pop each time it goes up, and not when it goes down.
9. Press and hold any button with the mouse: it should shrink slightly, then return when released, also if you drag off it first. Drag a file over a conversation: the drop area should fade in, without flickering as you move around.
10. Turn off **Settings** > **Ease of Access** > **Display** > **Show animations in Windows**, and repeat 7 to 9: nothing should move, and everything should still work. Turn it back on if you want it.

### Testing the welcome and emoji on two PCs

Run 0.12.0 on both PCs. The welcome needs a computer with nothing paired: use a PC that has never run Roomie, or a new Windows account. On a PC that already has paired devices, use **Settings** > **About** > **Show the welcome again** instead, after choosing **Forget…** for the device you pair with in step 3.

1. Open Roomie on a new PC: after the logo, the welcome should ask for a name, with the cursor in the box. Clear the box and choose **Continue**: it should explain the problem and stay there.
2. Enter a name and press Enter. Step 2 should list the other computer within a few seconds, or say what's wrong if it can't find any. Allow Roomie if Windows Firewall asks.
3. Choose **Pair…** and compare the codes as usual. After **Close**, it should say you're paired, and **Start chatting** should open that conversation with the cursor ready to type.
4. Start again on a new PC, and this time start the pairing from the **other** computer, while the welcome is still on its first step. The code should appear over the welcome, Tab should reach its buttons, and pairing should finish the welcome.
5. Close Roomie part-way through the welcome and reopen it: it should start again. Then choose **Skip for now** and reopen: it shouldn't return.
6. Update a PC that already has paired devices: no welcome should appear, even after forgetting them all.
7. In a chat, choose the **😀** button and pick an emoji: it should go in at the cursor and the cursor should stay in the message box. Press Esc with the list open: it should close. Click the button again with the list open: it should close and stay closed.
8. Send emoji both ways, including to a group and with `Win`+`.`. They should show on both PCs, in the chat, the conversation list and a notification. WPF draws them in one colour.

### Testing profile pictures and the logo reveal on two PCs

Run 0.11.0 on both PCs. A PC still on 0.10.0 keeps working with it, but neither sees the other's picture.

1. Open Roomie: the logo should appear through a growing circle, then the name and a line of text, then fade into the app within about 2 seconds. Open it again and click at once: it should skip.
2. In **Settings** on PC A, choose a built-in avatar. PC B should show it within a moment, in **Chats**, at the top of the open chat and in **Devices**.
3. Choose **Upload picture…** and pick a phone photo, ideally one taken in portrait. It should be upright and cropped to the centre, on both PCs.
4. Try a file that isn't a picture, such as a text file renamed to `.jpg`: it should explain the problem and keep your current picture.
5. Choose **Remove picture**: both PCs should go back to the first letter of the name.
6. Change the picture with PC B closed, then open B: it should show the new one after connecting.
7. Choose **Forget…** for a device: its picture should go, and the file should be gone from `avatars` in Roomie's data folder.

### Testing the Windows interface on two PCs

Run the same 0.10.0 build on both PCs. It exchanges messages and files with 0.9.1, but the database is upgraded on first run, after which older versions refuse it. In the older steps below, the device buttons are now under **Devices**, and closing Roomie means **Exit Roomie** from the icon in the notification area.

1. Try each screen, then **Settings** > **Appearance** > **Dark**, **Light** and **Same as Windows**. Change the Windows colour setting while on **Same as Windows**: Roomie and its title bar should follow.
2. Close the window with **X**. The first time, a notification should say Roomie is still running. Send it a message from the other PC: a notification should show the sender and text, and clicking it should open that conversation.
3. Open Roomie's exe again: the running window should come back, with no second copy. Right-click the notification-area icon and choose **Exit Roomie**: the icon should go, and Task Manager should show no Roomie.
4. Turn on **Start Roomie when I sign in to Windows**, then sign out and in. Roomie should be in the notification area with no window. Turn it off again if you don't want it. If you move the portable folder, open it once from the new place so the setting follows.
5. Turn off **Include the message text**: the next notification should say only *New message*.
6. With the window in the background, send a message from the other PC. The taskbar button should flash and show a count, which clears when you read it.
7. Drag one file, then several, onto a conversation. Copy a file in Explorer and press Ctrl+V in the message box. Drag a folder: it should ask you to zip it first. Drag a file onto a group: it should say files go to one device at a time.
8. Send a large file and watch the speed and time left, and the green progress on the taskbar button. **Transfers** should show the same transfer, with working buttons.
9. In **Settings**, choose another drive under **Received files** and receive a large file. While it arrives, a hidden `.roomie-partial` folder should be on that drive, with almost nothing added to `C:`. When it ends, the file should be in the chosen folder and the hidden folder gone. Close and reopen Roomie part-way too: it should continue and finish in the same place.
10. Unplug a USB drive chosen under **Received files**, or rename the folder's parent, then **Accept** an offer: it should explain the problem and keep the offer open.

### Testing file transfer on two PCs

Run the same build on both PCs, because a device on 0.9.0 can't exchange files with later versions. Your paired devices, messages and groups carry over, and the database is upgraded on first run, after which older versions refuse it.

1. On PC A, open a chat with PC B and choose **Send file…**. B should come to the front if its window is open, with the file under **Files** and a count on its conversation. Nothing should be in B's received-files folder yet.
2. **Decline** on B: A should show *They declined*, with **Send again**. Send again and **Accept**: both should end on *verified*. **Show in folder** on B should open the folder with the file selected. Check the file opens and is intact.
3. Send a file of several GB, and chat in both directions while it moves. The button should say *Checking file…* until the offer appears. The progress bar should move, and messages should still arrive quickly.
4. Send a large file and choose **Cancel** on the receiver part-way, then on the sender. Both should say *Canceled*, nothing should be in the received-files folder, and chat should keep working without reconnecting.
5. Send a large file and pull the network cable part-way. Both should show *Paused at…* within about 15 seconds. Plug it back in: it should continue from about the same point by itself, end on *verified*, and the file should open intact.
   - Do the same again, but close Roomie on the receiver part-way, then open it again. Then again, closing it on the sender.
   - Cancel while paused, once on each side. The other side should say *Canceled* after reconnecting, and no unfinished file should remain.
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

To build the portable zip and the signed MSIX, run `packaging\build-msix.ps1`. It needs no administrator, and creates the signing certificate the first time, in your own certificate store. Updates only need the certificate's subject to stay the same, so if it's lost, a new one works once its `.cer` is on each PC, which the install script does.

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
- [`docs/security-review.md`](docs/security-review.md): the threat model, what the Phase 11 review found, and the risks that remain.
- [`docs/pre-architecture.md`](docs/pre-architecture.md): the original high-level blueprint.

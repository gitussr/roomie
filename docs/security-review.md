# Roomie security review (Phase 11, version 0.14.0, with later changes noted)

Scope: Roomie used inside one office, on about 7 PCs, not released publicly. This is a code review by the same AI assistant that wrote the code, plus tests. It is **not** the independent review that architecture.md §10 asks for before a production release. That requirement was set aside for office use on 2026-11-04, and it should be done before Roomie goes beyond the office.

Roomie doesn't claim OWASP ASVS compliance. ASVS is written for web apps and APIs, so it was used only as a list of questions, below.

## Threat model

**What is protected:** messages and files in transit, who a device is talking to, and the PC receiving files.

| Who | Can do | Stopped by |
| --- | ------ | ---------- |
| Anyone on the network, unpaired | Send discovery packets, connect, ask to pair | Discovery creates no trust. Unpaired sessions can only pair, with limits on sessions, messages and requests |
| Someone in the middle during pairing | Relay and alter traffic | Commit-then-reveal, signatures tied to the TLS certificates, and the six-digit code compared by people |
| Someone claiming a paired device's ID | Connect with its own key | The pinned fingerprint, on every connection |
| A paired device that misbehaves | Send anything allowed | Size and format limits, safe file names, explicit file acceptance, and no remote choice of paths |
| A former group member | Claim to still be a member | Owner-signed membership with version checks |

**Out of scope:** malware running as the same Windows user, which can read the database and use the keys as Roomie does; someone with physical access to an unlocked PC; flooding the network itself.

## Found and fixed in 0.14.0

Fixes 2 to 6, and the file types in fix 8, have tests that failed before the fix and pass after it. Exceptions are listed below the table.

| # | Severity | Problem | Fix |
| - | -------- | ------- | --- |
| 1 | High | Before any pairing, a peer's certificate could make Roomie contact a web address the peer chose, because Windows fetches missing issuers while checking a certificate. Besides reaching out to the internet, this could probe other machines from the victim's PC | Certificate downloads are off |
| 2 | Medium | A peer that stopped reading could hang a session forever: the heartbeat waited on its own send, so the check for silence never ran | Sending no longer holds up that check |
| 3 | Medium | One computer could fill all 8 unpaired session slots using made-up device IDs, so nobody could pair | At most 2 unpaired sessions per address |
| 4 | Medium | One computer could keep asking to pair, interrupting the user with a new device ID each time, which also got around **Block** | At most 5 requests per address in 10 minutes |
| 5 | Medium | One connection claiming a paired device's ID stopped automatic reconnection to the real device until someone pressed Connect | Roomie keeps dialling, and each dial checks the pin. The warning stays until the identity is right |
| 6 | Low | Unpaired peers could send unlimited messages, and any peer unlimited `ERROR` reports, each written to the log, pushing out useful entries. A report could also contain line breaks and imitate log lines | 120 messages a minute before pairing, a limit on error reports, and reports logged on one line |
| 7 | Low | The Devices screen showed a paired device's name from unauthenticated discovery packets, so anyone could relabel it | It shows the name saved for the device. Since 0.15.0 that follows what the paired device says over its secure connection, never discovery |
| 8 | Low | The warning before accepting a runnable file missed types such as `.iso`, `.msixbundle`, `.xll` and `.py`. A crash at the wrong moment could also leave a received file without its "from elsewhere" mark | More types are recognized, and the mark is added before the file gets its real name |

Fix 1 is tested on the certificate check itself, where the old setting contacts the address and the new one doesn't. A full handshake can't show it on one PC, because the side presenting the certificate triggers the same fetch. Fix 7 is a change to one screen with no automated test, and hasn't been seen working with a real spoofed packet. The earlier mark in fix 8 is covered only by the existing tests that a received file ends up marked.

## Reviewed, nothing found

- **Pairing:** commitments, transcript, signatures, code derivation, approval needed on both sides, and approval only after the code is shown.
- **TLS:** mutual authentication, allowed versions and suites, strict certificate format, and pin checks before any session exists.
- **Framing:** lengths checked before allocating, strict JSON, and the sender checked against TLS.
- **Files:** names never become paths, reserved names and hidden extensions, size enforced, hash checked, nothing opened automatically.
- **Groups:** owner signatures, separated signing domains, version checks, and no trust from membership.
- **Pictures:** exact format and size checked before decoding, and saved under the device ID only.
- **Database:** parameterized queries. The only values built into SQL are the code's own constants and numbers.
- **Keys:** DPAPI for the current user, not exportable from the TLS key store, and never logged.

## Risks that remain

- **The code only helps if people compare it.** Someone who chooses *The codes match* without checking can pair with anyone. This is the most likely real failure.
- **The code is six digits.** An attacker in the middle gets one guess per attempt, so about one in a million.
- **Names are claims.** Only the device ID and fingerprint are verified. The name saved at pairing comes from discovery, and since 0.15.0 a paired device can change its own name at any time, up to the limit of 10 changes a minute. So a paired device could take the name of another one. The Devices screen also shows each device's ID, and the pin doesn't depend on the name.
- **Deleting a group can't be taken back.** Anyone who gets the owner's device key can sign one, as they could any other change to its groups. Other members can't: their signatures are refused, and a `GROUP_LEAVE` only ever removes the device that sent it.
- **Blocking is by device ID,** which a determined attacker can change. The per-address limits slow that down, but addresses can be changed on a LAN too.
- **Discovery can make Roomie dial a chosen LAN address and port,** though only within its own subnets, and only with a TLS handshake.
- **The database and received files aren't encrypted by Roomie.** Windows account protection and BitLocker apply.
- **The self-signed package certificate** must be trusted on each PC, and its private key is on the PC that builds. It is trusted only for installing packages, not as a root.
- **A paired device can flood** with messages or offers up to the size limits. Pairing is treated as trust.

## ASVS questions considered

Using ASVS 4.0 chapter names, since the sections that apply are about the same in later versions.

| Area | Applies? | Where it stands |
| ---- | -------- | --------------- |
| V1 Architecture and threat modelling | Yes | This document, and architecture.md |
| V2 and V3 Authentication, sessions | Partly: devices, not passwords | Device keys, mutual TLS, pinning, heartbeat timeouts |
| V4 Access control | Yes | One central policy check for every message type, unpaired refused first |
| V5 Validation and encoding | Yes | Strict schemas and sizes. Shown as plain text, never markup |
| V6 Cryptography | Yes | .NET and Windows primitives only, no custom cryptography |
| V7 Errors and logging | Yes | Generic errors to peers, no secrets logged, limits on peer-caused logging (fix 6) |
| V8 Data protection | Partly | DPAPI for keys, no database encryption (accepted above) |
| V9 Communications | Yes | TLS 1.3, or restricted TLS 1.2 on Windows 10 |
| V10 Malicious code, dependencies | Partly | Few dependencies. No automated dependency scanning |
| V12 Files | Yes | Accepted explicitly, size limited, hash checked, marked as from elsewhere |
| V11, V13, V14 Business logic, APIs, configuration | Partly | Rate limits, and a package with narrow firewall rules |

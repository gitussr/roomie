# Roomie

Roomie is a peer-to-peer LAN messenger for Windows. It needs no server, internet connection or cloud account. Built with C#, .NET 10 and WPF.

## Status

- **Phase 0 (foundation), done.** A WPF app on the .NET Generic Host with dependency injection, MVVM (CommunityToolkit.Mvvm), rolling file logs and the local data layout.
- **Phase 1 (device identity), done.** On first launch each install generates a random device ID, an ECDSA P-256 key pair and a self-signed X.509 certificate. Peers will pin the certificate's public-key fingerprint (`SHA256:…`). The private key is encrypted with DPAPI for the current Windows user, so an identity folder copied to another PC or account can't be decrypted there. The app moves it aside and creates a new identity instead. The username can be changed without affecting the identity.
- **Phase 2 (UDP discovery), done.** Roomie opens one UDP socket (port 45820) per active adapter with a private or link-local IPv4 address. It broadcasts a `DISCOVERY_REQUEST` at startup and after network changes, announces itself every 5 seconds, and answers requests by unicast after a random 0–500 ms delay, at most once per requester every 2 seconds. Packets are strictly validated (size, protocol, version, device ID, username, port, source subnet). Devices are keyed by device ID, so one heard over Wi-Fi and Ethernet appears once, and they drop off after 20 seconds of silence. Discovered devices are listed as *not paired*: discovery never connects or trusts anything. If another computer announces this computer's own device ID, a warning is logged.
- **Next: Phase 3,** the TCP connection manager and TLS.

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

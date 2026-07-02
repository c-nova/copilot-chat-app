# Copilot Chat App

A minimal, chat-only client for the GitHub Copilot CLI, running on **Windows**, **macOS**, and **iOS**.

Since the GitHub Copilot CLI (`copilot`) is a Node.js tool, it can't run natively inside an iOS app.
This project uses a **server/client** architecture instead:

```
┌─────────────────────────┐        WebSocket (wss/ws + Bearer token)        ┌──────────────────────────┐
│  .NET MAUI client app    │  ───────────────────────────────────────────▶  │  Node.js chat server      │
│  (Windows / macOS / iOS) │  ◀───────────────────────────────────────────  │  (runs on your PC/Mac,     │
│  server/CopilotChatApp   │        streamed chat replies                   │  wraps the `copilot` CLI)  │
└─────────────────────────┘                                                 └──────────────────────────┘
```

- **`server/`** — a small Node.js/TypeScript WebSocket server that runs `copilot -p "<message>" --available-tools= --output-format json -s --session-id=<id>`
  per chat turn (tools disabled → pure chat, no file/shell/web access), and streams the reply back over WebSocket.
  Reusing the same `--session-id` across turns resumes the same Copilot session, so conversations keep context.
- **`client/CopilotChatApp/`** — a .NET MAUI app (single codebase) with a simple chat UI and a Settings page
  to configure the server URL and auth token. Targets `net10.0-windows10.0.19041.0`, `net10.0-maccatalyst`, and `net10.0-ios`.

## 1. Run the server

Prerequisites: Node.js 18+, and the GitHub Copilot CLI installed & logged in (`copilot login`) on the machine that will run the server.

```powershell
cd server
npm install
copy .env.example .env   # edit AUTH_TOKEN to a long random secret
npm run build
npm start
```

The server listens on `ws://0.0.0.0:5219` (configurable via `PORT` in `.env`), meaning it accepts connections
on all network interfaces. **Clients must NOT use `0.0.0.0` as the address** — that's only a server bind address.
Instead, in the app's Settings, use:
- Same machine as the server → `ws://localhost:5219`
- Another device (e.g. iPhone) on the same LAN → the server machine's actual LAN IP, e.g. `ws://192.168.1.10:5219`

Make sure this port is reachable from your other devices (open the firewall / router port as needed), or run it on the same LAN.

Client apps must send `Authorization: Bearer <AUTH_TOKEN>` on connect — configure the same token in the app's Settings page.

## 2. Run/build the client

```powershell
cd client/CopilotChatApp
dotnet build -f net10.0-windows10.0.19041.0   # Windows
dotnet build -f net10.0-maccatalyst           # macOS (must be built/run on a Mac with Xcode)
dotnet build -f net10.0-ios                   # iOS (must be built/run on a Mac with Xcode + signing)
```

- **Windows**: `dotnet build`/`dotnet run` works directly on Windows (verified in this repo).
- **macOS / iOS**: Apple platform builds require Xcode and must be built on a Mac (MAUI's iOS/Mac Catalyst
  toolchain needs the Apple SDKs). Open `CopilotChatApp.csproj` in Visual Studio (Windows, with "Pair to Mac")
  or in Visual Studio / `dotnet build` directly on a Mac to produce the `.app`/`.ipa`.

On first launch, open **Settings** in the app and enter:
- **Server URL**: e.g. `ws://192.168.1.10:5219` (the IP/hostname of the machine running the server)
- **Auth Token**: same value as `AUTH_TOKEN` in `server/.env`

Then go back and start chatting. Use **New chat** in the toolbar to reset the conversation (starts a fresh Copilot CLI session).

## Notes / limitations

- Local network / non-TLS `ws://` access is explicitly allowed for iOS and Mac Catalyst via `Info.plist`
  (`NSAppTransportSecurity` + `NSLocalNetworkUsageDescription`). For access over the internet, prefer running
  the server behind a reverse proxy with TLS and using `wss://`.
- The server disables all Copilot tools (`--available-tools=`) so the app behaves as a pure chat client —
  it will not edit files, run shell commands, or access the web, even though the underlying CLI supports that.
- Conversations are keyed by a per-install conversation id (stored in app Preferences) mapped 1:1 to a Copilot
  CLI `--session-id` on the server; the server currently keeps this mapping in memory only (restarting the
  server starts fresh Copilot sessions, though the CLI's own session history is still resumable via `copilot --resume`
  independently of this app).

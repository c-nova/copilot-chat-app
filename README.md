# Copilot Chat App

**Languages:** English (below) | [日本語](#日本語)

A minimal mobile/desktop chat client for the GitHub Copilot CLI, running on **Windows**, **macOS**, and **iOS**.

> ⚠️ **This is a full agent client, not a plain chat client.** The server runs the Copilot CLI with
> `--allow-all-tools --allow-all-paths --allow-all-urls`, so every message you send can result in file edits,
> shell command execution, or web/MCP tool access on the machine running the server (scoped to `WORK_DIR`).
> Only share your Server URL and Auth Token with people/devices you trust as much as you'd trust someone with
> shell access to that machine. See [USAGE.md](USAGE.md) for details.

Since the GitHub Copilot CLI (`copilot`) is a Node.js tool, it can't run natively inside an iOS app.
This project uses a **server/client** architecture instead:

```
┌─────────────────────────┐        WebSocket (wss/ws + Bearer token)        ┌──────────────────────────┐
│  .NET MAUI client app    │  ───────────────────────────────────────────▶  │  Node.js chat server      │
│  (Windows / macOS / iOS) │  ◀───────────────────────────────────────────  │  (runs on your PC/Mac,     │
│  server/CopilotChatApp   │        streamed chat replies                   │  wraps the `copilot` CLI)  │
└─────────────────────────┘                                                 └──────────────────────────┘
```

- **`server/`** — a small Node.js/TypeScript WebSocket server that runs `copilot -p "<message>" --allow-all-tools --allow-all-paths --allow-all-urls -C <WORK_DIR> --output-format json -s --session-id=<id>`
  per chat turn (full agent mode: file edits, shell commands, and MCP tools are all pre-approved and confined to
  `WORK_DIR`), and streams the reply back over WebSocket. Reusing the same `--session-id` across turns resumes
  the same Copilot session, so conversations keep context.
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

## Features

- **Streaming chat** with the Copilot CLI, running in full agent mode (file edits, shell commands, MCP tools).
- **Sessions**: browse and resume past Copilot CLI sessions (read from the CLI's own session history on the
  server) from the **Sessions** screen, or start a brand new one.
- **Image attachments**: paste an image from the system clipboard (Mac Catalyst and iOS) and send it along with
  your message; the server writes it to a temp file and passes it to the CLI via `--attachment`.
- **In-app MCP server management**: add/remove `stdio` or `http` MCP servers from the **Manage MCP Servers**
  screen in Settings — no need to SSH into the server machine to run `copilot mcp add`.
- **Biometric/passcode lock**: on Mac Catalyst and Windows, the app can require Face ID / Touch ID / Windows
  Hello (or device passcode) to unlock after being backgrounded, protecting the saved Auth Token.
- Adjustable chat font size, from the Settings page.

## Notes / limitations

- Local network / non-TLS `ws://` access is explicitly allowed for iOS and Mac Catalyst via `Info.plist`
  (`NSAppTransportSecurity` + `NSLocalNetworkUsageDescription`). For access over the internet, prefer running
  the server behind a reverse proxy with TLS and using `wss://`.
- The server runs the Copilot CLI in **full agent mode** (`--allow-all-tools --allow-all-paths --allow-all-urls`):
  it *will* edit files, run shell commands, and access the web/MCP tools, all confined to the `WORK_DIR` folder
  (see [USAGE.md](USAGE.md) for the full list of implications). This is not a sandboxed, tool-free chat client.
- Conversations are keyed by a per-install conversation id (stored in app Preferences) mapped 1:1 to a Copilot
  CLI `--session-id` on the server; the server currently keeps this mapping in memory only (restarting the
  server starts fresh Copilot sessions, though the CLI's own session history is still resumable via `copilot --resume`
  independently of this app, and from the app's own **Sessions** screen).
- On iOS, connecting to the server for the first time after a fresh install triggers the OS's local-network-access
  permission prompt. The app retries connecting for a while and keeps retrying in the background if that first
  attempt fails — if you see a connection error right after granting that permission, just wait a few seconds and
  try again.

---

# 日本語

GitHub Copilot CLI を使ったチャットクライアントです。**Windows**・**macOS**・**iOS** で動きます。

> ⚠️ **これは素のチャットクライアントではなく、フルエージェントクライアントです。** サーバーは Copilot CLI を
> `--allow-all-tools --allow-all-paths --allow-all-urls` 付きで実行するため、送信したメッセージによって
> サーバーを動かしているマシン上(`WORK_DIR` の範囲内)でファイル編集・シェルコマンド実行・Web/MCPツール
> アクセスが行われることがあります。Server URL と Auth Token は、そのマシンのシェルアクセス権を渡しても
> 良いと思える相手/端末にだけ教えてください。詳細は [USAGE.md](USAGE.md) を参照してください。

GitHub Copilot CLI (`copilot`) は Node.js 製のツールなので、iOSアプリの中でそのまま動かすことはできません。
そのため、このプロジェクトは **サーバー/クライアント構成** を採用しています:

```
┌─────────────────────────┐        WebSocket (wss/ws + Bearer token)        ┌──────────────────────────┐
│  .NET MAUI クライアント   │  ───────────────────────────────────────────▶  │  Node.js チャットサーバー   │
│  (Windows / macOS / iOS) │  ◀───────────────────────────────────────────  │  (PC/Macで起動し、          │
│  server/CopilotChatApp   │        ストリーミングで返信                     │  `copilot` CLIをラップ)     │
└─────────────────────────┘                                                 └──────────────────────────┘
```

- **`server/`** — Node.js/TypeScript製の小さなWebSocketサーバー。チャット1ターンごとに
  `copilot -p "<message>" --allow-all-tools --allow-all-paths --allow-all-urls -C <WORK_DIR> --output-format json -s --session-id=<id>`
  を実行し(ファイル編集・シェルコマンド・MCPツールが全て事前承認済みのフルエージェントモードで、
  `WORK_DIR` 内に限定)、返信をWebSocket経由でストリーミングします。同じ `--session-id` を使い続けることで
  同一のCopilotセッションが再開され、会話の文脈が維持されます。
- **`client/CopilotChatApp/`** — .NET MAUI製アプリ(単一コードベース)。シンプルなチャットUIと、
  サーバーURL/認証トークンを設定するSettings画面を持ちます。対象は
  `net10.0-windows10.0.19041.0`、`net10.0-maccatalyst`、`net10.0-ios` です。

## 1. サーバーを起動する

前提条件: Node.js 18以上、サーバーを動かすマシンに GitHub Copilot CLI がインストール・ログイン済み(`copilot login`)であること。

```powershell
cd server
npm install
copy .env.example .env   # AUTH_TOKEN を長いランダムな秘密文字列に編集する
npm run build
npm start
```

サーバーは `ws://0.0.0.0:5219`(`.env` の `PORT` で変更可)で待ち受けます。これは全ネットワーク
インターフェースからの接続を受け付けるという意味です。**クライアント側では `0.0.0.0` をアドレスとして
使わないでください** — あれはサーバーのバインド用アドレスに過ぎません。アプリのSettingsでは:
- サーバーと同じマシン → `ws://localhost:5219`
- 同じLAN上の別端末(iPhoneなど) → サーバーマシンの実際のLAN IP、例: `ws://192.168.1.10:5219`

このポートが他の端末から到達可能であること(必要に応じてファイアウォール/ルーターのポートを開放)、
または同じLAN内で動かすようにしてください。

クライアントは接続時に `Authorization: Bearer <AUTH_TOKEN>` を送る必要があります — アプリのSettings画面で
同じトークンを設定してください。

## 2. クライアントを実行/ビルドする

```powershell
cd client/CopilotChatApp
dotnet build -f net10.0-windows10.0.19041.0   # Windows
dotnet build -f net10.0-maccatalyst           # macOS(Xcode入りのMacでビルド/実行する必要あり)
dotnet build -f net10.0-ios                   # iOS(Xcode+署名設定入りのMacでビルド/実行する必要あり)
```

- **Windows**: `dotnet build`/`dotnet run` がWindows上でそのまま動作します(このリポジトリで確認済み)。
- **macOS / iOS**: Appleプラットフォーム向けビルドにはXcodeが必要で、Mac上でビルドする必要があります
  (MAUIのiOS/Mac CatalystツールチェインはApple SDKを必要とします)。Visual Studio(Windows、
  「Macとペア設定」使用)で `CopilotChatApp.csproj` を開くか、Mac上で直接 Visual Studio /
  `dotnet build` を使って `.app`/`.ipa` を生成してください。

初回起動時は、アプリの **Settings** を開いて以下を入力します:
- **Server URL**: 例 `ws://192.168.1.10:5219`(サーバーを動かしているマシンのIP/ホスト名)
- **Auth Token**: `server/.env` の `AUTH_TOKEN` と同じ値

戻ってチャットを開始してください。ツールバーの **New chat** で会話をリセットできます
(新しいCopilot CLIセッションが始まります)。

## 機能

- Copilot CLI との**ストリーミングチャット**(フルエージェントモード: ファイル編集・シェルコマンド・MCPツール)
- **Sessions**: サーバー側に残っている過去のCopilot CLIセッション(CLI自身のセッション履歴から読み込み)を
  **Sessions** 画面から一覧・再開、または新規セッションを開始できます
- **画像添付**: システムクリップボードから画像を貼り付けて(Mac Catalyst / iOS)メッセージと一緒に送信。
  サーバーが一時ファイルに書き出し、CLIの `--attachment` に渡します
- **アプリ内MCPサーバー管理**: Settingsの **Manage MCP Servers** 画面から `stdio`/`http` のMCPサーバーを
  追加・削除できます — サーバーマシンにSSHして `copilot mcp add` を叩く必要はありません
- **生体認証/パスコードロック**: Mac CatalystとWindowsでは、バックグラウンドから復帰する際に
  Face ID / Touch ID / Windows Hello(またはデバイスのパスコード)でのロック解除を要求し、
  保存済みのAuth Tokenを保護できます
- Settings画面からチャットのフォントサイズを調整可能

## 注意点・制限事項

- ローカルネットワーク/非TLSの `ws://` アクセスは、iOSとMac Catalystでは `Info.plist`
  (`NSAppTransportSecurity` + `NSLocalNetworkUsageDescription`)により明示的に許可されています。
  インターネット経由でアクセスする場合は、TLS対応のリバースプロキシ経由で `wss://` を使うことを推奨します。
- サーバーはCopilot CLIを**フルエージェントモード**(`--allow-all-tools --allow-all-paths --allow-all-urls`)で
  実行します: ファイル編集・シェルコマンド実行・Web/MCPツールアクセスを実際に行います(`WORK_DIR` フォルダ内に限定、
  影響の詳細は [USAGE.md](USAGE.md) を参照)。サンドボックス化された、ツール無しのチャットクライアントでは
  ありません。
- 会話は、アプリごとの会話ID(アプリのPreferencesに保存)とサーバー側のCopilot CLI `--session-id` が
  1:1で対応付けられています。サーバーはこの対応関係を現状メモリ上にのみ保持しているため
  (サーバー再起動で新しいCopilotセッションが始まりますが、CLI自身のセッション履歴は
  このアプリとは独立して `copilot --resume` で、またはアプリ自身の **Sessions** 画面からも再開可能です)。
- iOSでは、新規インストール後に初めてサーバーへ接続する際、OSのローカルネットワークアクセス許可
  ダイアログが表示されます。アプリはしばらく接続をリトライし、最初の試行が失敗した場合もバックグラウンドで
  リトライを続けます — 許可した直後に接続エラーが出た場合は、数秒待ってからもう一度試してみてください。

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
- **`client/CopilotChatApp/`** — a .NET MAUI app (single codebase) with a chat UI and a Settings page to
  configure one or more server profiles (name/URL/auth token each — see **Multi-server support** below).
  Targets `net10.0-windows10.0.19041.0`, `net10.0-maccatalyst`, and `net10.0-ios`.

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

To have the server start automatically at login and restart itself if it crashes (rather than only
running in a terminal you keep open), see [USAGE.md's "Running the server persistently"
section](USAGE.md#running-the-server-persistently-start-at-login-auto-restart) — `scripts/install-server-startup-windows.ps1`
(Scheduled Task) and `scripts/install-server-startup-mac.sh` (LaunchAgent), no admin rights needed.

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
- **iOS on a physical device** needs more than just `dotnet build` — a provisioning profile, a signing
  identity, and a USB-connected device with Developer Mode on. See [USAGE.md](USAGE.md#ios) for the full
  step-by-step flow (a free Apple ID "Personal Team" is enough, no paid Apple Developer Program required).

### Quick build scripts

[`scripts/`](scripts/) has one-shot build scripts that install server dependencies, build the server, and
build the client for that platform:

```powershell
# Windows (PowerShell)
./scripts/build-windows.ps1          # build only
./scripts/build-windows.ps1 -Run     # build, then start the server + run the client
./scripts/build-windows.ps1 -Package # build a standalone, self-contained folder instead (no installer/MSIX)
```

```bash
# macOS
./scripts/build-mac.sh               # build only
./scripts/build-mac.sh --run          # build, then start the server + run the client
./scripts/build-mac.sh --package      # build a distributable .pkg installer instead
```

On first launch, open **Settings** and add a server:
- **Name**: any label for this server, e.g. "My Mac mini"
- **Server URL**: e.g. `ws://192.168.1.10:5219` (the IP/hostname of the machine running the server)
- **Auth Token**: same value as `AUTH_TOKEN` in that server's `server/.env`

Tap **Save**, then go back and start chatting. Add more servers the same way (see **Multi-server support**
below) - the Home screen aggregates sessions from every server you've added. Use **New chat** in the toolbar
to start a fresh Copilot CLI session (it'll ask which server, if you've configured more than one).

## Features

- **Streaming chat** with the Copilot CLI, running in full agent mode (file edits, shell commands, MCP tools).
- **Multi-server support**: add multiple server profiles in Settings (e.g. one per PC/Mac you run the server
  on). The Home screen connects to every configured server in parallel and shows all their sessions together,
  each tagged with an OS glyph + server name. Tap a server's name in the summary chip at the top to filter the
  list down to just that server; tap it again to show everything.
- **Session management**: rename (label) any session, archive/unarchive it (with a switch to show archived
  ones), and search across every configured server at once - both session titles AND the actual conversation
  content, not just the CLI's auto-generated title.
- **In-chat find**: search within the conversation you're currently viewing (🔍 in the chat toolbar), with
  prev/next buttons, a match counter, and a highlighted bubble for the current match.
- **Image attachments**: paste an image from the system clipboard (Mac Catalyst and iOS) and send it along with
  your message; the server writes it to a temp file and passes it to the CLI via `--attachment`.
- **Folder / git-clone picker for new chats**: start a new conversation rooted at a specific folder (within the
  server's configured `BROWSE_ROOTS`) or clone a git repo fresh, instead of always using the default workspace.
- **In-app MCP server management**: add/remove `stdio` or `http` MCP servers from the **Manage MCP Servers**
  screen in Settings — no need to SSH into the server machine to run `copilot mcp add`.
- **Biometric/passcode lock**: on iOS/iPadOS, Mac Catalyst, and Windows, the app can require Face ID / Touch ID /
  Windows Hello (or device passcode) to unlock after being backgrounded, protecting the saved Auth Token(s).
- Adjustable chat font size, from the Settings page - applies immediately, even to already-visible messages.
- **(Experimental) Push notification when Copilot replies**: set `NTFY_TOPIC` in `server/.env` to get a push
  notification via [ntfy](https://ntfy.sh) whenever a chat turn finishes - useful since real Apple/Google push
  would otherwise require this app to have its own paid developer account credentials. See
  [USAGE.md](USAGE.md#experimental-push-notifications-via-ntfy) for setup.

## Multi-server support

Each server you run (potentially on a different PC/Mac) is a separate, independent **profile** in Settings -
name, URL, and its own auth token. This follows a "federated" design: every server is the sole owner of its
own sessions (nothing is synced or merged server-side), and the client just fans out read-only requests
(list/search sessions) to every configured profile in parallel and merges the results for display.

- **Add a server**: Settings → "+ Add new server" → fill in Name/Server URL/Auth Token → Save. Saving switches
  the *active* profile to whichever one you just edited (used for actions like New Chat when only one server
  is configured).
- **Switch servers**: tap any profile row in Settings to make it active (the ✓ and highlighted border move to
  it), or just tap a session on the Home screen - opening it automatically switches to that session's server.
- **Remove a server**: tap "削除"/"Delete" on its row in Settings. This only removes it from *this app's*
  configuration; the server itself and its session history are untouched.
- The Home screen fetches every configured server in parallel and shows each one's sessions as soon as
  they're back, rather than waiting for the slowest one - a server still being connected to shows a "⋯" chip,
  a reachable one shows its OS glyph (tap to filter the list to just that server), and one that couldn't be
  reached in time shows a grayed-out "⚠️" instead of silently disappearing. Tap the 🔄 toolbar button to
  retry all servers again, e.g. after bringing a previously-down one back up.

## Notes / limitations

- Local network / non-TLS `ws://` access is explicitly allowed for iOS and Mac Catalyst via `Info.plist`
  (`NSAppTransportSecurity` + `NSLocalNetworkUsageDescription`). For access over the internet, prefer running
  the server behind a reverse proxy with TLS and using `wss://`.
- The server runs the Copilot CLI in **full agent mode** (`--allow-all-tools --allow-all-paths --allow-all-urls`):
  it *will* edit files, run shell commands, and access the web/MCP tools, all confined to the `WORK_DIR` folder
  (see [USAGE.md](USAGE.md) for the full list of implications). This is not a sandboxed, tool-free chat client.
- Conversations are keyed by a per-profile conversation id (stored in app Preferences, one per configured
  server) mapped 1:1 to a Copilot CLI `--session-id` on that server; each server currently keeps this mapping
  in memory only (restarting a server starts a fresh Copilot session for that profile's "current" chat, though
  the CLI's own session history is still resumable via `copilot --resume` independently of this app, and from
  the app's own Home screen).
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
- **`client/CopilotChatApp/`** — .NET MAUI製アプリ(単一コードベース)。チャットUIと、
  複数のサーバープロファイル(名前/URL/認証トークンをそれぞれ設定、下記「マルチサーバー対応について」参照)を
  管理するSettings画面を持ちます。対象は
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

サーバーをログイン時に自動起動させ、クラッシュしても自動再起動させたい場合(ターミナルを開きっぱなしに
する必要をなくしたい場合)は、[USAGE.mdの「サーバーを常時起動しておく」セクション](USAGE.md#サーバーを常時起動しておくログイン時に自動起動自動再起動)
を参照してください — `scripts/install-server-startup-windows.ps1`(Scheduled Task)と
`scripts/install-server-startup-mac.sh`(LaunchAgent)、どちらも管理者権限は不要です。

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
- **iOS実機デプロイ**は `dotnet build` だけでは完結しません — プロビジョニングプロファイル・署名ID・
  USB接続+デベロッパモード有効化が必要です。詳しい手順は [USAGE.md](USAGE.md#ios-1) を参照してください
  (無料のApple ID「Personal Team」で十分で、有償のApple Developer Programは不要です)。

### 簡単ビルドスクリプト

[`scripts/`](scripts/) に、サーバーの依存関係インストール・ビルド・各プラットフォームのクライアントビルドを
一括で行うスクリプトがあります:

```powershell
# Windows (PowerShell)
./scripts/build-windows.ps1          # ビルドのみ
./scripts/build-windows.ps1 -Run     # ビルド後、サーバー起動+クライアント実行まで行う
./scripts/build-windows.ps1 -Package # 代わりに単体で動く自己完結フォルダを生成(インストーラー/MSIXなし)
```

```bash
# macOS
./scripts/build-mac.sh               # ビルドのみ
./scripts/build-mac.sh --run          # ビルド後、サーバー起動+クライアント実行まで行う
./scripts/build-mac.sh --package      # 代わりに配布可能な.pkgインストーラーを生成
```

初回起動時は、アプリの **Settings** を開いてサーバーを追加します:
- **Name**: このサーバーに付ける好きな名前、例: 「自宅のMac mini」
- **Server URL**: 例 `ws://192.168.1.10:5219`(サーバーを動かしているマシンのIP/ホスト名)
- **Auth Token**: そのサーバーの `server/.env` の `AUTH_TOKEN` と同じ値

**Save** を押すと保存されます。戻ってチャットを開始してください。他のサーバーも同じ手順で追加できます
(下記の「マルチサーバー対応」参照) — Home画面は追加した全サーバーのセッションをまとめて表示します。
ツールバーの **New chat** で新しいCopilot CLIセッションを開始できます(サーバーを複数登録していれば、
どのサーバーで始めるか聞かれます)。

## 機能

- Copilot CLI との**ストリーミングチャット**(フルエージェントモード: ファイル編集・シェルコマンド・MCPツール)
- **マルチサーバー対応**: Settingsで複数のサーバープロファイルを追加できます(動かしているPC/Macごとに1つ)。
  Home画面は設定済みの全サーバーに並行接続し、それぞれのセッションをOSアイコン+サーバー名付きでまとめて
  表示します。上部のサマリーチップでサーバー名をタップすると、そのサーバーだけにフィルタできます
  (もう一度タップで解除)。
- **セッション管理**: 各セッションにラベル(表示名)を付けたり、アーカイブ/アーカイブ解除したり
  (アーカイブ済みを表示するスイッチもあり)、設定済みの全サーバーを横断して検索できます
  — CLIが自動生成したタイトルだけでなく、会話の実際の内容も検索対象です。
- **会話内検索**: 今開いている会話の中を検索できます(チャット画面ツールバーの🔍)。前後移動ボタン・
  一致件数・現在の一致箇所のハイライト付き。
- **画像添付**: システムクリップボードから画像を貼り付けて(Mac Catalyst / iOS)メッセージと一緒に送信。
  サーバーが一時ファイルに書き出し、CLIの `--attachment` に渡します
- **新規チャット時のフォルダ選択/Git clone**: 常にデフォルトのworkspaceを使うのではなく、サーバーの
  `BROWSE_ROOTS` 配下の特定フォルダを起点にしたり、Gitリポジトリを新規cloneして会話を始められます。
- **アプリ内MCPサーバー管理**: Settingsの **Manage MCP Servers** 画面から `stdio`/`http` のMCPサーバーを
  追加・削除できます — サーバーマシンにSSHして `copilot mcp add` を叩く必要はありません
- **生体認証/パスコードロック**: iOS/iPadOS・Mac Catalyst・Windowsでは、バックグラウンドから復帰する際に
  Face ID / Touch ID / Windows Hello(またはデバイスのパスコード)でのロック解除を要求し、
  保存済みのAuth Token(複数サーバー分)を保護できます
- Settings画面からチャットのフォントサイズを調整可能(既に表示中のメッセージにも即座に反映されます)
- **(実験的機能) Copilotが返信したらプッシュ通知**: `server/.env` に `NTFY_TOPIC` を設定すると、
  チャットの返信が完了するたびに[ntfy](https://ntfy.sh)経由でプッシュ通知が届きます。本物のApple/Google
  プッシュを自前で実装するには有償のデベロッパーアカウントが必要になるところを回避できます。
  セットアップ方法は[USAGE.md](USAGE.md#実験的機能-ntfy経由のプッシュ通知)を参照してください。

## マルチサーバー対応について

動かしているサーバー(別々のPC/Macにある場合も含む)はそれぞれ、Settings内で独立した**プロファイル**
(名前・URL・専用の認証トークン)として管理されます。「連合(Federated)」方式を採用しており、
各サーバーは自分のセッションの唯一の所有者です(サーバー側での同期・マージは一切行いません)。
クライアント側は設定済みの全プロファイルに並行して読み取り専用リクエスト(セッション一覧・検索)を
投げて、結果をまとめて表示しているだけです。

- **サーバーを追加**: Settings →「+ 新しいサーバーを追加」→ Name/Server URL/Auth Tokenを入力 → Save。
  Saveすると、今編集したサーバーが*アクティブ*なプロファイルに切り替わります(サーバーが1台だけ設定
  されている場合のNew Chat等で使われます)。
- **サーバーを切り替える**: Settingsでプロファイルの行をタップするとアクティブになります(✓マークと
  強調表示がそこに移動)。または、Home画面でセッションをタップするだけでもOK — そのセッションのサーバー
  に自動で切り替わります。
- **サーバーを削除**: Settingsのその行にある「削除」をタップ。これは*このアプリの設定から*削除する
  だけで、サーバー自体やそのセッション履歴には影響しません。
- Home画面は設定済みの全サーバーに並行して問い合わせ、一番遅いサーバーを待たずに繋がった順にセッションを
  表示します — 接続試行中のサーバーは「⋯」、繋がったサーバーはOSアイコン(タップでそのサーバーだけに
  絞り込み)、時間内に繋がらなかったサーバーはグレーアウトの「⚠️」で表示され、黙って消えることはあり
  ません。止まっていたサーバーを再起動した後などは、ツールバーの🔄で全サーバーへの再接続をやり直せます。

## 注意点・制限事項

- ローカルネットワーク/非TLSの `ws://` アクセスは、iOSとMac Catalystでは `Info.plist`
  (`NSAppTransportSecurity` + `NSLocalNetworkUsageDescription`)により明示的に許可されています。
  インターネット経由でアクセスする場合は、TLS対応のリバースプロキシ経由で `wss://` を使うことを推奨します。
- サーバーはCopilot CLIを**フルエージェントモード**(`--allow-all-tools --allow-all-paths --allow-all-urls`)で
  実行します: ファイル編集・シェルコマンド実行・Web/MCPツールアクセスを実際に行います(`WORK_DIR` フォルダ内に限定、
  影響の詳細は [USAGE.md](USAGE.md) を参照)。サンドボックス化された、ツール無しのチャットクライアントでは
  ありません。
- 会話は、サーバープロファイルごとの会話ID(アプリのPreferencesに保存、設定済みサーバー1台につき1つ)と
  そのサーバー側のCopilot CLI `--session-id` が1:1で対応付けられています。各サーバーはこの対応関係を
  現状メモリ上にのみ保持しているため(サーバー再起動でそのプロファイルの「現在の」チャットは新しい
  Copilotセッションから始まりますが、CLI自身のセッション履歴はこのアプリとは独立して `copilot --resume`
  で、またはアプリ自身のHome画面からも再開可能です)。
- iOSでは、新規インストール後に初めてサーバーへ接続する際、OSのローカルネットワークアクセス許可
  ダイアログが表示されます。アプリはしばらく接続をリトライし、最初の試行が失敗した場合もバックグラウンドで
  リトライを続けます — 許可した直後に接続エラーが出た場合は、数秒待ってからもう一度試してみてください。

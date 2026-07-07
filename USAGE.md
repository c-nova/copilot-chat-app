# Usage Guide (Copilot Chat App)

**Languages:** English (below) | [日本語](#日本語)

A usage guide for the chat app backed by the GitHub Copilot CLI.
Windows / macOS / iOS clients can all connect to the same server.

Structure:

- `server/` … a Node.js server that wraps and runs the Copilot CLI (keep it running on a PC/Mac)
- `client/CopilotChatApp/` … the .NET MAUI chat app (Windows/macOS/iOS)

---

## 1. Set up the server (once, on a PC/Mac)

Prerequisites:

- Node.js 18+
- GitHub Copilot CLI (`copilot` command) installed and logged in (`copilot login` already run)

Steps:

```powershell
cd copilot-chat-app\server
npm install
copy .env.example .env
```

Open `.env` and change `AUTH_TOKEN` to a long random string of your choice (this becomes the app's password).

Build and start:

```powershell
npm run build
npm start
```

If it shows `Copilot chat server listening on ws://0.0.0.0:5219`, it started successfully.
This PC/Mac needs to stay running (client devices just connect to it).

If you also want to use this from an iPhone outside your home/office LAN, set up router port
forwarding, a VPN, or a TLS-terminating reverse proxy (`wss://`) separately.
(Exposing plain `ws://` directly to the internet is not recommended.)

### Running the server persistently (start at login, auto-restart)

`npm start` only runs the server in your current terminal session - close the terminal (or log out)
and it stops. To make it start automatically and keep itself running like a real service:

```powershell
# Windows: registers a Scheduled Task (runs at logon for the current user, restarts on crash)
./scripts/install-server-startup-windows.ps1
./scripts/install-server-startup-windows.ps1 -Uninstall   # stop + remove
```

```bash
# macOS: registers a per-user LaunchAgent (same idea, via launchd)
./scripts/install-server-startup-mac.sh
./scripts/install-server-startup-mac.sh --uninstall   # stop + remove
```

Both need the server already built and configured first (`./scripts/build-windows.ps1` /
`./scripts/build-mac.sh` at least once). Neither needs admin/root - they're scoped to your own login
session, not a system-wide daemon, so the server starts when you log in and gets restarted
automatically if it ever crashes. Logs go to `%LOCALAPPDATA%\CopilotChatServer\server.log` (Windows)
or `~/Library/Logs/CopilotChatServer.log` (macOS).

⚠️ On corporate-managed Windows PCs, Group Policy sometimes blocks standard users (even elevated
ones) from registering Scheduled Tasks ("Access is denied" when running the script). If that
happens, `install-server-startup-windows.ps1` automatically falls back to a Startup folder shortcut
instead (`shell:startup`) - this still starts the server at login, just without the auto-restart-on-
crash behavior a Scheduled Task gives you.

---

## 2. Use the client app

### Windows

```powershell
cd copilot-chat-app\client\CopilotChatApp
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0
```

Runs directly on this PC.

#### Installing a built copy instead of running from source

`dotnet run` above rebuilds and launches from source every time, which is fine for development but not for
just "install it and use it" on a PC that doesn't have this repo cloned. To produce a standalone, installable
build instead:

```powershell
cd copilot-chat-app\client\CopilotChatApp
dotnet publish -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
```

This produces a folder at `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\` containing
`CopilotChatApp.exe` and everything it needs to run. Copy that whole folder (not just the `.exe`) to wherever
you want to run it from — including another PC that doesn't have .NET or this repo installed, since
`WindowsAppSDKSelfContained=true` bundles the runtime — and double-click `CopilotChatApp.exe`. There's no
installer or Start Menu entry; it's just a folder you can run in place, move around, or delete.

Drop `-p:WindowsAppSDKSelfContained=true` if you'd rather have a much smaller output that relies on the .NET
runtime already being installed on the machine that runs it.

> This publish flow follows Microsoft's documented "unpackaged .NET MAUI app for Windows" steps but hasn't
> been run end-to-end on an actual Windows machine as part of writing this doc — if something doesn't match,
> the [official docs](https://learn.microsoft.com/dotnet/maui/windows/deployment/publish-unpackaged-cli) are
> the source of truth.

### macOS (Mac Catalyst)

Requires a Mac + Xcode. Run the following on a Mac (or remote-build via Visual Studio for Mac, or
"Pair to Mac" from Windows VS):

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet build -f net10.0-maccatalyst
dotnet run -f net10.0-maccatalyst
```

#### Installing a built copy instead of running from source

To get an installable app instead of running from source every time:

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet publish -f net10.0-maccatalyst -c Release
```

This produces an installer package at
`bin/Release/net10.0-maccatalyst/publish/CopilotChatApp-1.0.pkg`. Double-click it to install — it installs to
`/Applications/GitHub Copilot CLI-GUI.app` (the app's display name, from `<ApplicationTitle>` in the
`.csproj`), just like any other Mac app installer.

Because this `.pkg` is unsigned (no Apple Developer ID certificate is configured for this project), macOS
Gatekeeper would normally refuse to open it — but a file built locally on your own Mac has no
`com.apple.quarantine` extended attribute (that flag is only added by the OS when a file arrives via a
browser download, AirDrop, Mail attachment, etc.), so double-clicking a `.pkg` you just built yourself
usually just works with no warning (verified: built and installed one this way with zero Gatekeeper
prompts). If you copy the `.pkg` to a *different* Mac (via AirDrop, a cloud drive, a USB stick that went
through another machine, etc.), that transfer may add the quarantine flag there, and you'll see "Apple could
not verify this app is free of malware." To get past that on the receiving Mac, either:
- System Settings → Privacy & Security → scroll down to find the blocked-app notice → **Open Anyway**, or
- Strip the flag from the terminal before opening it: `xattr -dr com.apple.quarantine CopilotChatApp-1.0.pkg`

To uninstall, just delete `/Applications/GitHub Copilot CLI-GUI.app` (there's no separate uninstaller;
`pkgutil --forget com.companyname.copilotchatapp` also clears the installer's receipt if you want a clean
slate before reinstalling a different version).

### iOS

Requires a Mac + Xcode + an Apple Developer signing identity. A free Apple ID "Personal Team" (no paid
Apple Developer Program membership) is enough for on-device testing, but the full flow needs a few manual,
one-time steps that `dotnet build` alone can't do:

1. **Generate a provisioning profile (first time only).** `dotnet build` can't register an App ID or create
   a provisioning profile on its own — the quickest way is to let Xcode do it once:
   - Xcode → File → New → Project → iOS App
   - Team: your Apple ID (Personal Team)
   - **Organization Identifier**: must match this project's bundle id prefix, e.g. `com.companyname`, so the
     generated bundle id is `com.companyname.copilotchatapp` (check `<ApplicationId>` in
     `CopilotChatApp.csproj` if this has been customized)
   - Select your iPhone/iPad as the run destination and press ▶ once — this registers the App ID and
     installs a provisioning profile on the device. Trust the developer when prompted on the device
     (Settings → General → VPN & Device Management).
   - A free Personal Team's provisioning profile expires after about a week — just redo this step to renew it.
2. **Connect the device via USB.** Wi-Fi-only pairing is not reliable for `dotnet build -t:Run` (it can hang
   indefinitely waiting to install). Enable Developer Mode on the device first (Settings → Privacy & Security
   → Developer Mode → restart to confirm), then connect with a cable.
3. **Find the device UDID:**
   ```bash
   xcrun xctrace list devices
   ```
4. **Build, then run**, passing your signing identity and the plain UDID (no `:v2:...` prefix — that format
   makes the launch hang):
   ```bash
   cd copilot-chat-app/client/CopilotChatApp
   dotnet build -f net10.0-ios -p:RuntimeIdentifier=ios-arm64 \
     -p:CodesignKey="Apple Development: you@example.com (TEAMID)" \
     -p:CodesignProvision="Automatic"
   dotnet build -t:Run -f net10.0-ios -p:RuntimeIdentifier=ios-arm64 \
     -p:CodesignKey="Apple Development: you@example.com (TEAMID)" \
     -p:CodesignProvision="Automatic" \
     -p:_DeviceName=00008132-0018513A0C79001C
   ```
   List your signing identities with `security find-identity -v -p codesigning`.

Or open the project in Visual Studio for Mac and pick your device from the run destination dropdown, which
handles signing/UDID selection for you.

> If you edit code, rebuild, and the app's behavior doesn't change, delete `bin/Debug/net10.0-ios` and
> `obj/Debug/net10.0-ios` first — MSBuild's incremental build can silently skip recompiling and just
> reinstall the same stale binary.
>
> If a provisioning profile already exists for this exact device + bundle id (check
> `~/Library/MobileDevice/Provisioning Profiles/`, e.g. with the device-listing command in step 3 above), you
> can skip straight to step 4 — step 1 is only needed the first time or after the profile expires.
>
> `dotnet build -t:Run` stays attached and streams the device's console log until the app is closed — this is
> normal, it's not hanging. Press Ctrl+C to detach; MSBuild will then report the launch as "failed" with an
> MSB3073 error because `mlaunch` got killed rather than exiting cleanly, which is expected and can be ignored.

### Quick build scripts

[`scripts/build-windows.ps1`](scripts/build-windows.ps1) and [`scripts/build-mac.sh`](scripts/build-mac.sh)
install server dependencies, build the server, and build the client for that platform in one step
(pass `-Run`/`--run` to also start the server and launch the client afterwards, or `-Package`/`--package`
to produce a distributable build instead - a standalone folder on Windows, a `.pkg` installer on macOS -
see "Installing a built copy instead of running from source" above for what to do with either one).

---

## 3. Initial app setup

After launching the app, tap/click **Settings** in the top-right and add a server:

| Field      | Value                                                                                                                         |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------ |
| Name       | Any label for this server, e.g. "My Mac mini" or "Work Desktop"                                                                |
| Server URL | `ws://localhost:5219` if using the same PC as the server. From another device (e.g. iPhone), the server machine's LAN IP address, e.g. `ws://192.168.1.10:5219` |
| Auth Token | Same value as `AUTH_TOKEN` set in that server's `.env`                                                                          |

⚠️ **Don't enter `ws://0.0.0.0:5219`.** `0.0.0.0` is a special server-side "listen on all interfaces"
address and can't be used as a client's connection target (it causes a Connection Error).

Tap **Save** to save it. If using an iPhone, make sure it's on the same Wi-Fi (same LAN).

### Adding more servers (multi-server support)

You can add more than one server profile - e.g. one for your Mac and one for your Windows PC, each running
its own `copilot-chat-app` server. In Settings, tap **"+ Add new server"**, fill in Name/Server URL/Auth Token,
and Save. The Home screen connects to *every* configured server in parallel and merges all their sessions into
one list, each tagged with an OS glyph + server name so you can tell them apart. Servers still being connected
to show a "⋯" chip, and a server that couldn't be reached shows a grayed-out "⚠️" instead of blocking the
rest of the list from appearing - tap the 🔄 button in the toolbar to retry every server again (e.g. after
starting a server you'd left off). Tap a reachable server's name in the summary chip to filter the list down
to just that server (tap it again to clear the filter).

Tapping a session (or starting a New Chat) automatically switches to that session's server, so you don't need
to manually pick the right one in Settings first - just tap what you want to open.

---

## 4. Chatting

1. Type a message in the input box at the bottom of the chat screen and tap **Send**
2. The reply from Copilot streams in as it's generated
3. The conversation is continued (previous context is remembered)
4. To switch to a new topic, tap **New chat** in the top-right to reset the conversation (asks which server to
   use, if you've configured more than one)

### Finding things

- **Across all your servers**: on the Home screen, use the search box below the server summary chip to search
  every configured server at once - both session titles AND the actual conversation content (not just the
  CLI's auto-generated title). Toggle **"Show archived"** to include archived sessions in the results.
- **Within the current conversation**: tap 🔍 in the chat screen's toolbar to open the in-chat find bar. Type
  to jump to the most recent match (highlighted with an orange border); use the ↑/↓ buttons to step through
  other matches, and the counter shows how many were found.

### Managing sessions

On the Home screen, tap the **"⋯"** on any session card to:
- **Rename it** (set a label shown instead of the CLI's auto-generated title)
- **Archive/unarchive it** - archived sessions are hidden by default; flip the **"Show archived"** switch at
  the top to see them again

Archiving is non-destructive and reversible - it's just a local annotation, not a deletion. The session and its
full history stay on the server either way.

### Sending images

On Mac Catalyst / iOS, use the 📎 button at the bottom of the screen to paste an image from the
system clipboard and send it along with your message (tap ✕ to remove one before sending). On iOS,
reading an image copied from another app may show the system's "Allow Paste?" prompt — that's expected.

### Opening past sessions

The Home screen (the first thing you see when the app launches) lists every session from every server you've
configured, most recently updated first. Tap one to resume it - if the list is empty, none of your configured
servers have any session history yet (or none of them are reachable right now).

---

## 5. About agent features (file edits, command execution, MCP)

This app runs in "full agent mode": besides conversation, it can also do the following, just like
the Copilot CLI itself:

- Create/edit files
- Run shell commands
- Web search / URL access
- Use tools from configured MCP servers

All of these operations happen only inside the server's **workspace folder**:

```
copilot-chat-app\server\workspace
```

(also shown in the server startup log as `Copilot agent working directory: ...`.)
To change it, set a different path in `WORK_DIR` in `server/.env`.

In the chat screen, a small gray line appears each time a tool runs:

```
🔧 Running: shell — ...
✅ shell — ...
```

⚠️ **Security note**: in this mode, a remote/mobile device can run shell commands inside the server
PC's workspace. Don't share the `Server URL` and `Auth Token` with anyone/any device you don't trust.

### Adding an MCP server

Use **Manage MCP Servers** in Settings to add/remove servers directly from the app. MCP servers are managed
per-server, not globally - the page shows "管理対象サーバー: <name>" at the top so you always know which
server's list you're editing (whichever profile is currently active; tap a different profile row in Settings
first if you meant to manage a different server).

- `stdio`: enter the server name, launch command, and arguments (space-separated)
- `http`: enter the server name and URL

You don't need to SSH into the server machine to run `copilot mcp add`.
(If you prefer the command line, it's still available as before:)

```powershell
copilot mcp add <name> <command or URL>
copilot mcp list
```

Registered MCP servers are automatically loaded whenever the server launches `copilot` in full agent
mode (`--allow-all-tools`). When one of its tools is called during a chat, the same
"🔧 Running: ..." indicator appears as for built-in tools.

---

## Troubleshooting

| Symptom                                                     | Check                                                                                                                     |
| ------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------- |
| App can't connect                                           | Are you using `0.0.0.0` as the Server URL? (→ change to `localhost` or the actual IP), is the server running, is the port correct, is the device on the same LAN? |
| "Unauthorized"-style error                                  | Does the Auth Token match the server's `.env`?                                                                          |
| Only the iPhone can't connect                                | Same Wi-Fi? Is port 5219 blocked by the router/PC firewall?                                                             |
| No reply comes back                                          | Is `copilot login` done on the PC/Mac running the server? Any errors in the server log?                                 |
| Sessions/chat only fail right after installing on iOS         | Answer the iOS local-network-access permission prompt, then wait a bit (or go back to the chat screen and reopen it). It keeps retrying automatically in the background. |
| One server's sessions never show up on Home                  | Check that its `server/` process is actually running and reachable from this device - it'll show a grayed-out ⚠️ chip instead of blocking the rest of the list. Start the server, then tap the 🔄 button on the Home screen to retry without leaving the page. |

---

# 日本語

# 使い方ガイド (Copilot Chat App)

GitHub Copilot CLI をバックエンドにした、チャット専用アプリの使い方です。
Windows / macOS / iOS で同じサーバーに接続して使えます。

構成:

- `server/` … Copilot CLI をラップして動かす Node.js サーバー(PCかMacで常時起動しておく)
- `client/CopilotChatApp/` … チャット画面の .NET MAUI アプリ(Windows/macOS/iOS)

---

## 1. サーバーを準備する(最初に1台のPC/Macで)

事前に必要なもの:

- Node.js 18以上
- GitHub Copilot CLI (`copilot` コマンド) がインストール済み・ログイン済み (`copilot login` 実行済み)

手順:

```powershell
cd copilot-chat-app\server
npm install
copy .env.example .env
```

`.env` を開いて `AUTH_TOKEN` を好きな長いランダム文字列に変更する(これがアプリ側のパスワードになります)。

ビルドして起動:

```powershell
npm run build
npm start
```

`Copilot chat server listening on ws://0.0.0.0:5219` と表示されれば起動成功。
このPC/Macを常時起動しておく必要があります(スマホ側はこのサーバーに接続しに行くだけ)。

社内LAN/自宅LANの外からiPhoneでも使いたい場合は、ルーターのポート開放やVPN、
またはTLS終端(`wss://`)できるリバースプロキシなどを別途用意してください
(素の `ws://` をインターネットに直接晒すのは非推奨です)。

### サーバーを常時起動しておく(ログイン時に自動起動+自動再起動)

`npm start` は今のターミナルセッションで動くだけなので、ターミナルを閉じたり
ログアウトしたりすると止まってしまいます。本物のサービスっぽく、自動起動+
自動再起動させたい場合:

```powershell
# Windows: Scheduled Taskとして登録(現在のユーザーのログオン時に起動、クラッシュ時は自動再起動)
./scripts/install-server-startup-windows.ps1
./scripts/install-server-startup-windows.ps1 -Uninstall   # 停止+削除
```

```bash
# macOS: ユーザーごとのLaunchAgentとして登録(launchd経由で同様の仕組み)
./scripts/install-server-startup-mac.sh
./scripts/install-server-startup-mac.sh --uninstall   # 停止+削除
```

どちらも事前にサーバーのビルド・設定が済んでいる必要があります(最低1回は
`./scripts/build-windows.ps1` / `./scripts/build-mac.sh` を実行しておいてください)。
管理者権限は不要です — システム全体のデーモンではなく、自分のログインセッションに
紐づく仕組みなので、ログインすると起動し、クラッシュしても自動的に再起動されます。
ログは `%LOCALAPPDATA%\CopilotChatServer\server.log`(Windows)または
`~/Library/Logs/CopilotChatServer.log`(macOS)に出力されます。

⚠️ 会社管理のWindows PCだと、Group Policyで一般ユーザー(管理者権限で実行してもダメな
場合あり)によるScheduled Task登録がブロックされていることがあります(実行すると
「アクセスが拒否されました」エラーになる)。その場合、`install-server-startup-windows.ps1`
は自動的にスタートアップフォルダのショートカット方式(`shell:startup`)にフォールバック
します — ログイン時の自動起動は同じようにできますが、Scheduled Taskのような
クラッシュ時の自動再起動は行われません。

---

## 2. クライアントアプリを使う

### Windows

```powershell
cd copilot-chat-app\client\CopilotChatApp
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0
```

そのままこのPCで動かせます。

#### ソースからではなく、ビルド済みのものをインストールする

上の`dotnet run`は毎回ソースからビルド&起動するので開発向けです。このリポジトリをクローンしていない
PCに「インストールして使うだけ」にしたい場合は、単体で動く成果物を作ります:

```powershell
cd copilot-chat-app\client\CopilotChatApp
dotnet publish -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
```

`bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\` フォルダに`CopilotChatApp.exe`と実行に必要な
ファイル一式が生成されます。この**フォルダごと**(`.exe`だけでなく)好きな場所にコピーして
`CopilotChatApp.exe`をダブルクリックしてください — `WindowsAppSDKSelfContained=true`によりランタイムも
同梱されるので、.NETやこのリポジトリが入っていない別PCでも動きます。インストーラーやスタートメニュー
登録は無く、その場で実行・移動・削除ができるフォルダというだけです。

もっと小さい成果物にしたい場合は`-p:WindowsAppSDKSelfContained=true`を外してください(その場合、実行
するPCに.NETランタイムが別途インストール済みである必要があります)。

> このpublish手順はMicrosoft公式の「.NET MAUI Windowsアプリのunpackaged公開」手順に沿っていますが、
> このドキュメント執筆時点で実機のWindows環境ではエンドツーエンドで検証していません — 齟齬があれば
> [公式ドキュメント](https://learn.microsoft.com/dotnet/maui/windows/deployment/publish-unpackaged-cli)を
> 正としてください。

### macOS (Mac Catalyst)

Mac + Xcode が必要です。Macで以下を実行(またはVisual Studio for Macや、Windows版VSから「Macとペア設定」でリモートビルド):

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet build -f net10.0-maccatalyst
dotnet run -f net10.0-maccatalyst
```

#### ソースからではなく、ビルド済みのものをインストールする

毎回ソースから実行するのではなく、インストール可能なアプリを作るには:

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet publish -f net10.0-maccatalyst -c Release
```

`bin/Release/net10.0-maccatalyst/publish/CopilotChatApp-1.0.pkg` にインストーラーが生成されます。
ダブルクリックすればインストールされ、他のMacアプリと同じように
`/Applications/GitHub Copilot CLI-GUI.app`(アプリ名は`.csproj`の`<ApplicationTitle>`から)に入ります。

この`.pkg`は未署名(このプロジェクトにApple Developer ID証明書は設定していない)なので、本来macOSの
Gatekeeperに弾かれるはずですが、**自分のMacでローカルビルドしたファイルには`com.apple.quarantine`
拡張属性が付いていません**(このフラグはブラウザダウンロードやAirDrop、メール添付など、OSが「外から
来たファイル」と認識したときだけ付与されます)。そのため、自分でビルドした`.pkg`をダブルクリックする
分には警告なしで普通にインストールできます(検証済み: 実際にこの手順でビルド&インストールし、
Gatekeeperの警告は一切出ませんでした)。この`.pkg`をAirDropやクラウドストレージ経由で**別のMac**に
コピーした場合は、そちらではquarantineフラグが付与されて「Appleはこのアプリに問題がないことを
確認できません」と出ることがあります。その場合は:
- システム設定 → プライバシーとセキュリティ → 下の方にブロック通知が出ているので **このまま開く**、または
- ターミナルで開く前にフラグを剥がす: `xattr -dr com.apple.quarantine CopilotChatApp-1.0.pkg`

アンインストールは`/Applications/GitHub Copilot CLI-GUI.app`を削除するだけです(専用アンインストーラー
は無し。別バージョンを入れ直す前にまっさらにしたい場合は
`pkgutil --forget com.companyname.copilotchatapp`でインストーラーのレシートも消せます)。

### iOS

Mac + Xcode + Apple Developer署名設定が必要です。無料のApple ID「Personal Team」(有償のApple Developer
Program未加入)でも実機テストは可能ですが、`dotnet build` だけでは完結しない手動の初回セットアップが
あります:

1. **プロビジョニングプロファイルを生成する(初回のみ)。** `dotnet build` だけではApp ID登録や
   プロビジョニングプロファイルの自動生成はできないので、Xcodeに一度やらせるのが一番早いです:
   - Xcode → File → New → Project → iOS App
   - Team: 自分のApple ID(Personal Team)
   - **Organization Identifier**: このプロジェクトのBundle IDプレフィックスと一致させる必要があります。
     例: `com.companyname`(生成されるBundle IDが `com.companyname.copilotchatapp` になるように。
     カスタマイズしている場合は `CopilotChatApp.csproj` の `<ApplicationId>` を確認してください)
   - 実機(iPhone/iPad)を実行先に選んで▶を一度押す → App ID登録とプロビジョニングプロファイルの
     デバイスへのインストールが行われます。初回はデバイス側で開発者を信頼してください
     (設定 → 一般 → VPNとデバイス管理)。
   - 無料のPersonal Teamのプロファイルは約1週間で失効するので、期限が切れたらこの手順をやり直すだけでOKです。
2. **実機をUSBケーブルで接続する。** Wi-Fiのみのペアリングだと `dotnet build -t:Run` のインストール待ちが
   無限にハングすることがあります。先にデバイス側でデベロッパモードを有効化(設定 → プライバシーとセキュリティ
   → デベロッパモード → 再起動して確認)してから、ケーブルで接続してください。
3. **実機のUDIDを確認する:**
   ```bash
   xcrun xctrace list devices
   ```
4. **ビルドしてから実行**。署名IDとプレーンなUDID(`:v2:...`のような接頭辞を付けると起動がハングします)を
   渡します:
   ```bash
   cd copilot-chat-app/client/CopilotChatApp
   dotnet build -f net10.0-ios -p:RuntimeIdentifier=ios-arm64 \
     -p:CodesignKey="Apple Development: you@example.com (TEAMID)" \
     -p:CodesignProvision="Automatic"
   dotnet build -t:Run -f net10.0-ios -p:RuntimeIdentifier=ios-arm64 \
     -p:CodesignKey="Apple Development: you@example.com (TEAMID)" \
     -p:CodesignProvision="Automatic" \
     -p:_DeviceName=00008132-0018513A0C79001C
   ```
   署名IDの一覧は `security find-identity -v -p codesigning` で確認できます。

または Visual Studio for Mac でプロジェクトを開き、実行先ドロップダウンから実機を選べば署名/UDID選択は
VS側がやってくれます。

> コードを直してリビルドしても挙動が変わらない場合は、先に `bin/Debug/net10.0-ios` と
> `obj/Debug/net10.0-ios` を削除してください。MSBuildの増分ビルドが再コンパイルを黙ってスキップし、
> 古いバイナリを再インストールしているだけのことがあります。

> このデバイス+Bundle ID向けのプロビジョニングプロファイルがすでにあれば(`~/Library/MobileDevice/Provisioning Profiles/`
> を確認、上の手順3のデバイス一覧コマンドで確認できます)、手順を4に直接進んでOKです — 手項1は初回や
> プロファイル失効後のみ必要です。
>
> `dotnet build -t:Run` はアプリが閉じられるまでデバイスのコンソールログを表示し続けます(フォアグラウンドで待機し
> 続けるのが仕様で、ハングしているわけではありません)。Ctrl+Cで切断できますが、`mlaunch`が正常終了ではなく
> 強制終了される形になるため、MSBuildはMSB3073エラーで「起動失敗」と表示します — これは想定通りの動作で
> 気にしなくて大丈夫です。

### 簡単ビルドスクリプト

[`scripts/build-windows.ps1`](scripts/build-windows.ps1) と [`scripts/build-mac.sh`](scripts/build-mac.sh) を
使うと、サーバーの依存関係インストール・ビルドと、そのプラットフォームのクライアントビルドを
一括で行えます(`-Run`/`--run` を付けるとビルド後にサーバー起動+クライアント実行まで行います)。
`-Package`/`--package` を付けると、代わりに配布可能なビルド(Windowsは単体フォルダ、macOSは`.pkg`
インストーラー)を生成します — それぞれの使い方は上の「ソースからではなく、ビルド済みのものを
インストールする」を参照してください。

---

## 3. アプリの初期設定

アプリを起動したら、右上の **Settings** をタップ/クリックしてサーバーを追加します:

| 項目       | 入力する値                                                                                                                                        |
| ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Name       | このサーバーに付ける好きな名前。例: 「自宅のMac mini」「会社のWindows」                                                                     |
| Server URL | サーバーと同じPCで使うなら`ws://localhost:5219`。他の端末(iPhoneなど)から使うなら、サーバーを動かしているPC/MacのLAN IPアドレス。例: `ws://192.168.1.10:5219` |
| Auth Token | サーバーの`.env` に設定した `AUTH_TOKEN` と同じ値                                                                                                             |

⚠️ **`ws://0.0.0.0:5219` は指定しないでください。** `0.0.0.0` はサーバー側が「全ネットワークから待ち受ける」ための特殊アドレスで、
クライアントの接続先としては使えません(Connection Errorの原因になります)。

**Save** を押すと保存されます。iPhoneから使う場合は、同じWi-Fi(同一LAN)に接続してください。

### サーバーを追加する(マルチサーバー対応)

サーバープロファイルは複数追加できます — 例えばMac用とWindows用、それぞれ独立した
`copilot-chat-app` サーバーを動かして両方登録する、といった使い方です。Settingsで
**「+ 新しいサーバーを追加」** をタップし、Name/Server URL/Auth Tokenを入力してSave。
Home画面は設定済みの*全サーバー*に並行接続し、すべてのセッションを1つのリストにまとめて
表示します(OSアイコン+サーバー名のバッジ付きで区別可能)。接続試行中のサーバーは「⋯」、
到達できなかったサーバーは一覧全体をブロックする代わりにグレーアウトの「⚠️」で表示されます —
止めていたサーバーを起動したら、ツールバーの🔄ボタンで全サーバーへの再接続をやり直せます。
Home画面上部のサマリーチップで、接続できているサーバー名をタップすると、そのサーバーだけに
フィルタできます(もう一度タップで解除)。

セッションをタップ(または新規チャット開始)すると、自動的にそのセッションのサーバーに
切り替わるので、事前にSettingsで手動選択する必要はありません。

---

## 4. チャットする

1. チャット画面下の入力欄にメッセージを入力し **Send**
2. Copilot からの返信がストリーミングで表示されます
3. 会話は継続されます(前のやり取りを覚えています)
4. 新しい話題に切り替えたいときは、右上の **New chat** で会話をリセットできます
   (サーバーを複数登録していれば、どのサーバーで始めるか聞かれます)

### 探しものをする

- **全サーバー横断**: Home画面の、サーバーサマリーチップ下にある検索欄で、設定済みの全サーバーを
  一度に検索できます — セッションのタイトルだけでなく、実際の会話内容も検索対象です
  (CLIが自動生成したタイトルだけではありません)。**「アーカイブ済みも表示」**をONにすると、
  アーカイブ済みのセッションも結果に含まれます。
- **今開いている会話の中**: チャット画面ツールバーの🔍をタップすると会話内検索バーが開きます。
  入力すると直近の一致箇所にジャンプ(オレンジ色の枠でハイライト)、↑/↓ボタンで他の一致箇所に
  移動でき、件数もカウンターに表示されます。

### セッションを管理する

Home画面で、各セッションカードの**「⋯」**をタップすると:
- **ラベルを付ける**(CLIが自動生成したタイトルの代わりに表示する名前を設定)
- **アーカイブ/アーカイブ解除** — アーカイブ済みは既定で非表示。上部の**「アーカイブ済みも表示」**
  スイッチをONにすればまた見えます

アーカイブは破壊的な操作ではなく可逆です — あくまでローカルの注釈であって削除ではありません。
セッション本体とその全履歴は、どちらの状態でもサーバー側にそのまま残ります。

### 画像を送る

Mac Catalyst / iOSでは、画面下の📎ボタンでシステムのクリップボードから画像を貼り付けて
メッセージと一緒に送信できます(送信前に✕タップで個別に取り消し可能)。
iOSでは他アプリからコピーした画像を読み込む際、システムの「ペーストを許可しますか?」
ダイアログが出ることがありますが、想定通りの動作です。

### 過去のセッションを開く

Home画面(アプリ起動時に最初に表示される画面)には、設定済みの全サーバーのセッションが
更新日時の新しい順にまとめて表示されます。タップして再開できます。一覧が空の場合は、
設定済みのどのサーバーにもまだセッション履歴が無い(またはどれも現在到達不可)ということです。

---

## 5. エージェント機能(ファイル編集・コマンド実行・MCP)について

このアプリは「フルエージェントモード」で動作しており、会話だけでなく Copilot CLI と同様に
以下のようなことも行えます:

- ファイルの作成・編集
- シェルコマンドの実行
- Web検索・URLアクセス
- 設定済みの MCP サーバーのツール利用

これらの操作は、すべてサーバー側の **ワークスペース フォルダ** の中だけで行われます:

```
copilot-chat-app\server\workspace
```

(サーバー起動時のログにも `Copilot agent working directory: ...` として表示されます。)
変更したい場合は `server/.env` の `WORK_DIR` に別のパスを指定してください。

チャット画面では、ツールが実行されるたびに次のような小さいグレーの表示が出ます:

```
🔧 Running: shell — ...
✅ shell — ...
```

⚠️ **セキュリティに関する注意**: このモードではリモート/モバイル端末からサーバーPCの
ワークスペース内でシェルコマンドを実行できてしまいます。信頼できるネットワーク・端末以外に
`Server URL` と `Auth Token` を教えないでください。

### MCPサーバーを追加したい場合

Settings画面の **Manage MCP Servers** から、アプリ内で直接 追加・削除ができます。MCPサーバーは
グローバルではなく**サーバーごと**に管理されます — 画面上部に「管理対象サーバー: <名前>」と表示され、
今どのサーバーのリストを編集しているか(=現在アクティブなプロファイル)が常にわかります。別のサーバーを
管理したい場合は、先にSettingsで別のプロファイル行をタップしてアクティブに切り替えてください。

- `stdio`: サーバー名・起動コマンド・引数(スペース区切り)を入力
- `http`: サーバー名・URLを入力

サーバー機(PC/Mac)にSSH等でログインして `copilot mcp add` を叩く必要はありません。
(コマンドラインから登録したい場合は、従来通り以下でも可能です)

```powershell
copilot mcp add <名前> <コマンドまたはURL>
copilot mcp list
```

登録済みのMCPサーバーは、サーバーがフルエージェントモード(`--allow-all-tools`)で
`copilot` を起動する際に自動的に読み込まれます。チャット中にそのMCPサーバーのツールが
呼ばれると、通常のツールと同じく「🔧 Running: ...」の表示が出ます。

---

## トラブルシューティング

| 症状                                                          | 確認すること                                                                                                                                                      |
| ------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| アプリから繋がらない                                          | Server URLに`0.0.0.0` を使っていないか(→`localhost`か実際のIPに変更)、サーバーが起動しているか、ポートが合っているか、同じLANにいるか                        |
| "Unauthorized" 的なエラー                                     | Auth Token がサーバーの`.env` と一致しているか                                                                                                                  |
| iPhoneだけ繋がらない                                          | 同じWi-Fiか、ルーター/PCのファイアウォールでポート5219がブロックされていないか                                                                                    |
| 返信が来ない                                                  | サーバーを動かしているPC/Macで`copilot login` が済んでいるか、サーバーのログにエラーが出ていないか                                                              |
| iOSでアプリインストール直後だけSessionsやチャットが繋がらない | iOSのローカルネットワークアクセス許可ダイアログに応答してから、少し待って(または一度チャット画面に戻って)再度開いてみてください。裏で自動的に再接続を試み続けます |
| 特定のサーバーのセッションがHome画面に一切出てこない               | その `server/` プロセスが実際に起動していて、この端末から到達可能か確認してください — 一覧全体をブロックする代わりにグレーアウトの⚠️チップで表示されます。サーバーを起動したら、Home画面の🔄ボタンで(このページを開き直さずに)再試行できます |

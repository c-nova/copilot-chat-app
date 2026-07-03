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

---

## 2. Use the client app

### Windows

```powershell
cd copilot-chat-app\client\CopilotChatApp
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0
```

Runs directly on this PC.

### macOS (Mac Catalyst)

Requires a Mac + Xcode. Run the following on a Mac (or remote-build via Visual Studio for Mac, or
"Pair to Mac" from Windows VS):

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet build -f net10.0-maccatalyst
dotnet run -f net10.0-maccatalyst
```

### iOS

Requires a Mac + Xcode + Apple Developer signing setup.

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet build -f net10.0-ios -t:Run -p:_DeviceName=:v2:udid=<connected device UDID>
```

Or select a simulator/device and run from Visual Studio / Visual Studio for Mac.

---

## 3. Initial app setup

After launching the app, tap/click **Settings** in the top-right and enter:

| Field      | Value                                                                                                                         |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------ |
| Server URL | `ws://localhost:5219` if using the same PC as the server. From another device (e.g. iPhone), the server machine's LAN IP address, e.g. `ws://192.168.1.10:5219` |
| Auth Token | Same value as `AUTH_TOKEN` set in the server's `.env`                                                                          |

⚠️ **Don't enter `ws://0.0.0.0:5219`.** `0.0.0.0` is a special server-side "listen on all interfaces"
address and can't be used as a client's connection target (it causes a Connection Error).

Tap **Save** to save it. If using an iPhone, make sure it's on the same Wi-Fi (same LAN).

---

## 4. Chatting

1. Type a message in the input box at the bottom of the home screen and tap **Send**
2. The reply from Copilot streams in as it's generated
3. The conversation is continued (previous context is remembered)
4. To switch to a new topic, tap **New chat** in the top-right to reset the conversation

### Sending images

On Mac Catalyst / iOS, use the 📎 button at the bottom of the screen to paste an image from the
system clipboard and send it along with your message (tap ✕ to remove one before sending). On iOS,
reading an image copied from another app may show the system's "Allow Paste?" prompt — that's expected.

### Opening past sessions

Use the ☰ (Sessions) icon at the top of the screen to browse and resume past Copilot CLI sessions
still on the server. If the list is empty, that server simply doesn't have any session history yet.

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

Use **Manage MCP Servers** in Settings to add/remove servers directly from the app.

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

---

## 2. クライアントアプリを使う

### Windows

```powershell
cd copilot-chat-app\client\CopilotChatApp
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0
```

そのままこのPCで動かせます。

### macOS (Mac Catalyst)

Mac + Xcode が必要です。Macで以下を実行(またはVisual Studio for Macや、Windows版VSから「Macとペア設定」でリモートビルド):

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet build -f net10.0-maccatalyst
dotnet run -f net10.0-maccatalyst
```

### iOS

Mac + Xcode + Apple Developer署名設定が必要です。

```bash
cd copilot-chat-app/client/CopilotChatApp
dotnet build -f net10.0-ios -t:Run -p:_DeviceName=:v2:udid=<接続した実機のUDID>
```

または Visual Studio / Visual Studio for Mac でシミュレータ/実機を選んで実行してください。

---

## 3. アプリの初期設定

アプリを起動したら、右上の **Settings** をタップ/クリックし:

| 項目       | 入力する値                                                                                                                                                        |
| ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Server URL | サーバーと同じPCで使うなら`ws://localhost:5219`。他の端末(iPhoneなど)から使うなら、サーバーを動かしているPC/MacのLAN IPアドレス。例: `ws://192.168.1.10:5219` |
| Auth Token | サーバーの`.env` に設定した `AUTH_TOKEN` と同じ値                                                                                                             |

⚠️ **`ws://0.0.0.0:5219` は指定しないでください。** `0.0.0.0` はサーバー側が「全ネットワークから待ち受ける」ための特殊アドレスで、
クライアントの接続先としては使えません(Connection Errorの原因になります)。

**Save** を押すと保存されます。iPhoneから使う場合は、同じWi-Fi(同一LAN)に接続してください。

---

## 4. チャットする

1. ホーム画面下の入力欄にメッセージを入力し **Send**
2. Copilot からの返信がストリーミングで表示されます
3. 会話は継続されます(前のやり取りを覚えています)
4. 新しい話題に切り替えたいときは、右上の **New chat** で会話をリセットできます

### 画像を送る

Mac Catalyst / iOSでは、画面下の📎ボタンでシステムのクリップボードから画像を貼り付けて
メッセージと一緒に送信できます(送信前に✕タップで個別に取り消し可能)。
iOSでは他アプリからコピーした画像を読み込む際、システムの「ペーストを許可しますか?」
ダイアログが出ることがありますが、想定通りの動作です。

### 過去のセッションを開く

画面上部の ☰ (Sessions) から、サーバー側に残っている過去のCopilot CLIセッション一覧を
確認・再開できます。一覧が空の場合は、そのサーバーでまだセッション履歴が無いだけです。

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

Settings画面の **Manage MCP Servers** から、アプリ内で直接 追加・削除ができます。

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

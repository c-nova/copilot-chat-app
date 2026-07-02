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

| 項目 | 入力する値 |
|---|---|
| Server URL | サーバーと同じPCで使うなら `ws://localhost:5219`。他の端末(iPhoneなど)から使うなら、サーバーを動かしているPC/MacのLAN IPアドレス。例: `ws://192.168.1.10:5219` |
| Auth Token | サーバーの `.env` に設定した `AUTH_TOKEN` と同じ値 |

⚠️ **`ws://0.0.0.0:5219` は指定しないでください。** `0.0.0.0` はサーバー側が「全ネットワークから待ち受ける」ための特殊アドレスで、
クライアントの接続先としては使えません(Connection Errorの原因になります)。

**Save** を押すと保存されます。iPhoneから使う場合は、同じWi-Fi(同一LAN)に接続してください。

---

## 4. チャットする

1. ホーム画面下の入力欄にメッセージを入力し **Send**
2. Copilot からの返信がストリーミングで表示されます
3. 会話は継続されます(前のやり取りを覚えています)
4. 新しい話題に切り替えたいときは、右上の **New chat** で会話をリセットできます

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

MCPサーバーはアプリからではなく、**サーバーを動かしているPC/Mac側**で、`copilot` コマンドを
使って一度だけ登録します(対話モードの `/mcp` を使わなくてもOKです)。

```powershell
copilot mcp add <名前> <コマンドまたはURL>
copilot mcp list
```

登録済みのMCPサーバーは、サーバーがフルエージェントモード(`--allow-all-tools`)で
`copilot` を起動する際に自動的に読み込まれるので、アプリ側から追加の作業は不要です。
チャット中にそのMCPサーバーのツールが呼ばれると、通常のツールと同じく
「🔧 Running: ...」の表示が出ます。

---

## トラブルシューティング

| 症状 | 確認すること |
|---|---|
| アプリから繋がらない | Server URLに `0.0.0.0` を使っていないか(→`localhost`か実際のIPに変更)、サーバーが起動しているか、ポートが合っているか、同じLANにいるか |
| "Unauthorized" 的なエラー | Auth Token がサーバーの `.env` と一致しているか |
| iPhoneだけ繋がらない | 同じWi-Fiか、ルーター/PCのファイアウォールでポート5219がブロックされていないか |
| 返信が来ない | サーバーを動かしているPC/Macで `copilot login` が済んでいるか、サーバーのログにエラーが出ていないか |

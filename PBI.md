# Product Backlog Items — copilot-chat-app 改善リスト

コードレビューで見つかった改善点をPBI形式で整理。優先度: 🔴High / 🟡Medium / 🟢Low

**ステータス**: ⏳ 未着手 / ✅ 対応済み(各PBIの見出しに付与し、対応後は末尾に「対応内容」を追記する)

---

## PBI-001: README.mdの記述がコードと矛盾している 🔴 ✅

**現状 / 問題点**
- [README.md](README.md#L33) には「サーバーは Copilot のツールを全部無効化してるので pure chat client として動作する(ファイル編集・シェル実行・Web アクセスなし)」と書かれている。
- しかし実装 [server/src/copilotRunner.ts](server/src/copilotRunner.ts#L67-L69) では

  ```ts
  '--allow-all-tools',
  '--allow-all-paths',
  '--allow-all-urls',
  ```

  が渡されており、**実際はフルエージェントモード**(ファイル編集・シェル実行・MCPツール利用が全部可能)。
- [USAGE.md](USAGE.md#L88) には正しい説明(フルエージェントモードで危険性の注意書き)があるが、README.mdだけ古いまま。

**リスク**
- 「安全な chat-only アプリ」と誤認したまま Server URL / Auth Token を信頼できない人やネットワークに渡してしまう事故につながる。

**対応案**
- README.md の該当セクションを USAGE.md の記述に揃えて書き直す。
- 「ツール無効化」という表現を削除し、「サーバー側の `WORK_DIR` 内でフルエージェント動作する」旨と、トークンの取り扱い注意を明記する。

**対応内容**
- 冒頭に「フルエージェントクライアントであり単なるchatクライアントではない」旨の警告ブロックを追加。
- サーバー起動コマンドの説明を実際の `--allow-all-tools --allow-all-paths --allow-all-urls -C <WORK_DIR>` に修正。
- Notes / limitations の「tools disabled」記述を削除し、実態(ファイル編集・シェル実行・Web/MCPアクセス可、WORK_DIR内に限定)に修正。

**影響ファイル**: [README.md](README.md)

---

## PBI-002: 認証トークンの比較がタイミングセーフでない 🟡 ✅

**現状 / 問題点**
- [server/src/wsServer.ts](server/src/wsServer.ts#L27) の `isAuthorized()` が `provided === config.authToken` という単純な文字列比較を使っている。JS の `===` は先頭から不一致が見つかった時点で早期リターンするため、理論上タイミング攻撃(応答時間差からトークンを推測)の余地がある。

**対応案**
- `crypto.timingSafeEqual` を使った定数時間比較に置き換える。長さが異なる場合は先に弾いてから比較する(`timingSafeEqual` は同じ長さのBufferしか比較できないため)。

**対応内容**
- `timingSafeEqualString()` ヘルパーを追加。長さが違う場合も同じ長さのバッファ同士で `crypto.timingSafeEqual` を走らせてから `false` を返すことで、長さ不一致のケースだけ早く返る側面を減らした。

**影響ファイル**: [server/src/wsServer.ts](server/src/wsServer.ts)

---

## PBI-003: 認証失敗ログが攻撃者に有用な情報を出しすぎている 🟡 ✅

**現状 / 問題点**
- [server/src/wsServer.ts](server/src/wsServer.ts#L22) で認証失敗時に `console.warn` がトークンの文字数(`received X chars, expected Y chars`)まで出力している。

**対応案**
- ログには成否とタイムスタンプ・接続元情報程度に留め、トークン長などのヒントになる情報は出力しない。

**対応内容**
- トークン長を含むログメッセージを `[auth] rejected: token mismatch` のみに簡略化(PBI-002対応と同じ箇所)。

**影響ファイル**: [server/src/wsServer.ts](server/src/wsServer.ts)

---

## PBI-004: Auth TokenがMAUI側で平文保存されている 🟡 ✅

**現状 / 問題点**
- [client/CopilotChatApp/Services/SettingsService.cs](client/CopilotChatApp/Services/SettingsService.cs#L18) の `AuthToken` プロパティが `Preferences.Default`(平文保存)を使っている。

**対応案**
- MAUI標準の `SecureStorage` API に置き換える(Windows/macOS/iOSでそれぞれOSのセキュアストレージを使ってくれる)。

**対応内容**
- `AuthToken` 同期プロパティを廃止し、`GetAuthTokenAsync()` / `SetAuthTokenAsync()`(`SecureStorage` 使用)に変更。
- 旧バージョンで平文Preferencesに保存されていたトークンがあれば、初回読み込み時にSecureStorageへ自動移行してPreferences側は削除する後方互換処理を追加。
- `IsConfigured` も `IsConfiguredAsync()` に変更。呼び出し元4ファイル([SettingsPage.xaml.cs](client/CopilotChatApp/Views/SettingsPage.xaml.cs), `SessionsPage.xaml.cs`(削除済み), [McpServersPage.xaml.cs](client/CopilotChatApp/Views/McpServersPage.xaml.cs), [MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs))を非同期呼び出しに追従。
- ⚠️ 実機/Xcode環境がなく `dotnet build` でのフルビルド確認は未実施(静的エラーチェックのみ済み)。次回実機ビルド時に要確認。

**影響ファイル**: [client/CopilotChatApp/Services/SettingsService.cs](client/CopilotChatApp/Services/SettingsService.cs), [client/CopilotChatApp/Views/SettingsPage.xaml.cs](client/CopilotChatApp/Views/SettingsPage.xaml.cs), `client/CopilotChatApp/Views/SessionsPage.xaml.cs`(削除済み), [client/CopilotChatApp/Views/McpServersPage.xaml.cs](client/CopilotChatApp/Views/McpServersPage.xaml.cs), [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs)

---

## PBI-005: 会話管理用Mapのエントリがクリーンアップされない 🟢 ✅

**現状 / 問題点**
- [server/src/wsServer.ts](server/src/wsServer.ts#L45-L46) の `conversationSessions` / `conversationLocks` は会話が増える一方で削除されない。サーバーを長時間稼働させると微増していくメモリリーク傾向がある。

**対応案**
- WebSocket切断時、または一定時間非アクティブな会話をMapから削除する処理を追加する。

**対応内容**
- 各WebSocket接続ごとに、その接続が使った `conversationId` を `Set` で追跡し、`ws.on('close', ...)` で該当エントリを `conversationLocks` / `conversationSessions` から削除する処理を追加。
- `conversationSessions` は実質 `sessionId === conversationId` の冗長なキャッシュだが(常に同じ値が入る)、今回のスコープでは温存しつつクリーンアップのみ追加。将来的にはMap自体の削除も検討の余地あり。

**影響ファイル**: [server/src/wsServer.ts](server/src/wsServer.ts)

---

## PBI-006: 自動テストが存在しない 🟢 ✅

**現状 / 問題点**
- `server/test-tool-client.js` は手動確認用スクリプトのみで、ユニットテスト/CIが整備されていない。

**対応案**
- 最低限、`copilotRunner.ts` のJSON行パース処理や `wsServer.ts` の認証ロジックにユニットテスト(jest等)を追加する。

**対応内容**
- `jest` + `ts-jest` を devDependencies に追加し、`npm test` で実行できるように整備([server/jest.config.js](server/jest.config.js), [server/package.json](server/package.json))。
- `copilotRunner.ts` の `summarizeToolArguments` / `truncate` / `formatToolDetail` をexportしてユニットテスト可能にし、[server/tests/copilotRunner.test.ts](server/tests/copilotRunner.test.ts) を追加(11ケース)。
- `wsServer.ts` の `timingSafeEqualString`(PBI-002で追加)をexportし、[server/tests/wsServer.test.ts](server/tests/wsServer.test.ts) を追加(5ケース)。
- `config.ts` が `AUTH_TOKEN` 未設定で例外を投げる仕様のため、[server/tests/jest.setup.ts](server/tests/jest.setup.ts) でテスト用のダミートークンを注入。
- `npm test`(16 tests)・`npm run build` とも成功を確認済み。
- 未対応: `copilotRunner.ts` のCLI呼び出し(`spawn`)自体や `wsServer.ts` のWebSocket結合部分の統合テストは今回のスコープ外(モックが必要になるため別PBI候補)。

**影響ファイル**: `server/` 全般(`package.json`, `jest.config.js`, `tests/`)

---

## PBI-007: MainPage.xaml.csにロジックが集中している 🟢 ✅

**現状 / 問題点**
- [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs) がコードビハインド方式で、チャット送受信・ツールイベント処理・UI操作が全部同じクラスに集中している。

**対応案**
- 今の規模では許容範囲だが、機能を増やすならMVVM(ViewModel分離)を検討する。

**対応内容**
- [client/CopilotChatApp/ViewModels/ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs) を新規作成。`Messages`、`InputText`、`IsSending`、`IsLocked`(PBI-009のBiometricゲート状態)、`SendCommand`/`UnlockCommand`、ChatClientServiceのイベント処理、`StartNewChatAsync`/`ApplyResumedSession`など、UIコントロールに依存しないロジック・状態を全部移植。
- [client/CopilotChatApp/ViewModels/RelayCommand.cs](client/CopilotChatApp/ViewModels/RelayCommand.cs) を新規作成(非同期対応の軽量`ICommand`実装。CommunityToolkit.Mvvmは新規依存を避けるため導入せず手書き)。
- [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs) を薄いコードビハインドに縮小。`DisplayAlert`・`Clipboard`・`Navigation`・`CollectionView.ScrollTo`などPage/プラットフォーム依存の処理のみ残す。
- [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml) の`SendButton`/`InputEditor`/`LockOverlay`/`Unlock`ボタンをVMの`Command`/プロパティにバインドし、`Clicked`ハンドラを削除。
- **実機ビルド検証も実施**(下記参照): PBI-007+PBI-009込みで実際に`dotnet build -f net10.0-maccatalyst`が0エラーで通ることを確認済み。

**影響ファイル**: [client/CopilotChatApp/ViewModels/ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs), [client/CopilotChatApp/ViewModels/RelayCommand.cs](client/CopilotChatApp/ViewModels/RelayCommand.cs), [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml)

---

## PBI-010: ApplicationTitleにスラッシュが含まれていてMacCatalystビルドが壊れる 🔴 ✅

**現状 / 問題点**
- [client/CopilotChatApp/CopilotChatApp.csproj](client/CopilotChatApp/CopilotChatApp.csproj) の `<ApplicationTitle>GitHub Copilot CLI/GUI</ApplicationTitle>` に `/` が含まれており、MacCatalystのコード署名タスク(`ComputeCodesignItems`)がこれをパス区切りと誤認。`bin/.../GitHub Copilot CLI/GitHub Copilot CLI/GUI.app` という壊れたパスを探しに行って `DirectoryNotFoundException` でビルド自体が失敗する。
- PBI-007の実機ビルド検証で発覚(それまで検証環境が無く未発見だった既存バグ)。

**対応内容**
- `ApplicationTitle` を `GitHub Copilot CLI-GUI`(スラッシュ→ハイフン)に変更。
- 表示テキストの一貫性のため [MainPage.xaml](client/CopilotChatApp/MainPage.xaml) のPage `Title`・タイトルバーの`Label`、[AppShell.xaml](client/CopilotChatApp/AppShell.xaml) の `Title` も同様に修正。
- 修正後、`dotnet build -f net10.0-maccatalyst` が0エラーで成功することを確認。

**影響ファイル**: [client/CopilotChatApp/CopilotChatApp.csproj](client/CopilotChatApp/CopilotChatApp.csproj), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml), [client/CopilotChatApp/AppShell.xaml](client/CopilotChatApp/AppShell.xaml)

---

## PBI-008: ツール実行引数がマスキングなしでUIに表示される 🟢 ✅

**現状 / 問題点**
- [server/src/copilotRunner.ts](server/src/copilotRunner.ts#L47) の `formatToolDetail` がツール呼び出し引数をそのままJSON化してクライアントに送信・表示している。MCPサーバーに認証ヘッダー等を渡した場合、その値がそのままチャット画面の「詳細」ダイアログに出てしまう可能性がある。

**対応案**
- `Authorization` / `token` / `password` / `secret` 等のキー名を含む値をマスキングしてから送る。

**対応内容**
- `redactSensitiveValues()` を追加。キー名が `token|password|secret|api[-_]?key|authorization|cookie|credential` 等のパターンにマッチする値を、ネストの深さを問わず再帰的に `***REDACTED***` に置換。
- `formatToolDetail`(詳細ダイアログ用)と `summarizeToolArguments`(一覧の一行サマリー用)の両方に適用。
- [server/tests/copilotRunner.test.ts](server/tests/copilotRunner.test.ts) にマスキングのテストケースを追加(ネストしたheaders内のAuthorizationやpasswordが隠れることを確認)。
- `npm test`(18 tests)・`npm run build` とも成功確認済み。

**影響ファイル**: [server/src/copilotRunner.ts](server/src/copilotRunner.ts), [server/tests/copilotRunner.test.ts](server/tests/copilotRunner.test.ts)

---

## PBI-009: SecureStorage内のAuthTokenをBiometric/パスコードでゲートする 🟡 ✅

**背景・検討経緯**
- 認証には2つのレイヤーがある: (1) サーバー⇔クライアントの認可(Bearer Token)、(2) 端末を操作してる人が本人かのローカル確認。Biometricは(2)にしか使えない(ネットワーク越しの認証の代替にはならない)。
- 代替案として「クライアント側でもGitHub OAuthさせる」案も検討したが、本アプリは個人が自分のGitHubアカウントでログイン済みのCopilot CLI端末に、個人がローカル/VPN経由で繋ぐ用途が前提。複数人でのID使い回しはGitHubライセンス条項に抵触しうるため、複数ユーザー識別を目的にしたGitHub OAuth導入は不採用。
- 結論: **Bearer Token方式は維持しつつ、SecureStorageに保存したトークンへのアクセスをBiometric(Face ID/指紋/Windows Hello)またはデバイスパスコードでガードする**。端末紛失・盗難・覗き見時に、アプリを開いただけではトークンに触れないようにする。

**対応方針**
- `Plugin.Maui.Biometric`(または同等パッケージ)を導入し、iOS(Face ID/Touch ID)・Android(BiometricPrompt)・Windows(Windows Hello)を統一APIでラップ。
- アプリ起動時 or バックグラウンドからの復帰時に1回だけ生体認証/パスコードを要求し、成功したらフォアグラウンドにいる間はSecureStorageのAuthTokenアクセスを許可する(毎回の送信ごとに要求するとUXが悪化するため)。
- 生体認証が端末側で未設定/非対応の場合のフォールバック挙動を決める(要相談: 素通しにする か 設定必須にするか)。
- 影響範囲: `SettingsService.GetAuthTokenAsync()` の呼び出し元(MainPage, SessionsPage, McpServersPage, SettingsPage)全て。

**決定事項(ユーザー確認済み)**
- 未設定/非対応時のフォールバック: **素通し**(生体認証もパスコードも設定されてない端末ではゲートせずアクセス許可。個人専用アプリで自分の端末を締め出すのは本末転倒なため)。
- 要求タイミング: **アプリ起動時 + バックグラウンドからの復帰時のみ**(メッセージ送信ごとには要求しない)。

**対応内容**
- NuGet `Plugin.Maui.Biometric`(0.1.0, iOS/MacCatalyst/Windows/Android対応、本アプリはiOS/MacCatalyst/Windowsのみ使用)を追加。
- [client/CopilotChatApp/Services/BiometricGateService.cs](client/CopilotChatApp/Services/BiometricGateService.cs) を新規作成。`SettingsService`と同様のstaticクラスとして実装:
  - `EnsureUnlockedAsync()`: フォアグラウンドセッション中に一度アンロック済みならすぐ`true`。未設定/非対応/エラー時は`true`(素通し)。それ以外は`AuthenticationRequest{ AllowPasswordAuth = true, ... }` で生体認証 or デバイスパスコードを要求。
  - `Lock()`: アンロック状態をリセットし、`Locked`イベントを発火。
- [client/CopilotChatApp/App.xaml.cs](client/CopilotChatApp/App.xaml.cs): `Window.Stopped`イベント(アプリがバックグラウンドに移行)で`BiometricGateService.Lock()`を呼ぶよう追加。
- [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml): 既存コンテンツの上に半透明の「🔒 Locked」オーバーレイ(`LockOverlay`)を追加。ロック中は入力欄の操作を視覚的にブロックし、「Unlock」ボタンで再試行可能。
- [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs):
  - コンストラクタで`BiometricGateService.Locked`イベントを購読し、即座にオーバーレイ表示。
  - `OnAppearing`と`SendCurrentInputAsync`の先頭で`TryUnlockAsync()`を呼び、生体認証/パスコードが必要な場合はプロンプトを出す。
- ⚠️ 実機/Xcode/iOSワークロードがなく `dotnet build` でのフルビルド確認は未実施(静的エラーチェックのみ済み)。次回実機ビルド時に要確認。特に`Window.Stopped`イベントの実機での発火タイミングは要検証。→ **2026-07-08 に下記の通り解消済み**。
- **2026-07-08 実機検証で見つかったバグ、修正済み**: iOS/MacCatalystの`Info.plist`に`NSFaceIDUsageDescription`
  キーが無く、Face ID系のAPI(`LAContext`、`Plugin.Maui.Biometric`経由)を実際に呼び出すとクラッシュしうる
  状態だった(iOS 11+の必須プライバシー説明キー)。[client/CopilotChatApp/Platforms/iOS/Info.plist](client/CopilotChatApp/Platforms/iOS/Info.plist)と
  [client/CopilotChatApp/Platforms/MacCatalyst/Info.plist](client/CopilotChatApp/Platforms/MacCatalyst/Info.plist)
  に説明文を追加して解決。iPad実機・iPhone実機の両方でクリーンビルド後に動作確認済み
  (Mac miniはTouch ID未登録のため素通しフォールバックの方を確認、iPad/iPhoneは実際に起動してクラッシュしないことを確認)。

**影響ファイル**: [client/CopilotChatApp/Services/BiometricGateService.cs](client/CopilotChatApp/Services/BiometricGateService.cs), [client/CopilotChatApp/App.xaml.cs](client/CopilotChatApp/App.xaml.cs), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml), [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs), [client/CopilotChatApp/CopilotChatApp.csproj](client/CopilotChatApp/CopilotChatApp.csproj)

---

## PBI-011: チャットのフォントサイズがmacOSで小さくて設定できない 🟢 ✅

**現状 / 問題点**
- Mac(MacCatalyst)で実行するとチャットメッセージのフォントが小さく感じる、かつ調整する手段がなかった。

**対応内容**
- [SettingsService.cs](client/CopilotChatApp/Services/SettingsService.cs) に `ChatFontSize` プロパティを追加(Preferencesに永続化、範囲12〜28pt、デフォルト15pt)。setterで即座に `Application.Current.Resources["ChatFontSize"]` を更新し、`{DynamicResource ChatFontSize}` を使う全コントロールにリアルタイム反映されるようにした。
- [App.xaml](client/CopilotChatApp/App.xaml) にデフォルト値の `ChatFontSize` リソースを追加。[App.xaml.cs](client/CopilotChatApp/App.xaml.cs) 起動時に保存済みの値(または既定値)を反映。
- [MainPage.xaml](client/CopilotChatApp/MainPage.xaml): チャット本文(`idk:MarkdownView.TextFontSize`)とメッセージ入力欄(`Editor.FontSize`)を `{DynamicResource ChatFontSize}` にバインド。
- [SettingsPage.xaml](client/CopilotChatApp/Views/SettingsPage.xaml) / [SettingsPage.xaml.cs](client/CopilotChatApp/Views/SettingsPage.xaml.cs): 「Chat Font Size」セクションを追加。`Slider`(12〜28)+ サンプルテキストプレビューで、ドラッグ中にリアルタイムで反映・即座にPreferencesへ保存(明示的なSave操作は不要 — 機密情報ではないため)。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。

**影響ファイル**: [client/CopilotChatApp/Services/SettingsService.cs](client/CopilotChatApp/Services/SettingsService.cs), [client/CopilotChatApp/App.xaml](client/CopilotChatApp/App.xaml), [client/CopilotChatApp/App.xaml.cs](client/CopilotChatApp/App.xaml.cs), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml), [client/CopilotChatApp/Views/SettingsPage.xaml](client/CopilotChatApp/Views/SettingsPage.xaml), [client/CopilotChatApp/Views/SettingsPage.xaml.cs](client/CopilotChatApp/Views/SettingsPage.xaml.cs)

---

## PBI-012: AUTH_TOKENが空文字列でも起動できてしまう 🔴 ✅

**現状 / 問題点**
- [server/src/config.ts](server/src/config.ts#L21) の `requireEnv('AUTH_TOKEN')` は `??` 演算子で `undefined`/`null` の場合だけ弾いていて、`AUTH_TOKEN=`(空文字列)を`.env`に設定した場合はそのまま素通りしてしまう。
- 空文字列のトークンが有効になると、クライアント側が空のBearerトークンを送っても(あるいはAuthorizationヘッダの形式次第では)認証を突破できてしまう可能性がある理論上の穴。
- トークン長そのものへの上限/下限のバリデーションも元々存在しなかった。

**対応内容**
- [server/src/config.ts](server/src/config.ts) に `requireAuthToken()` を追加。`AUTH_TOKEN` が16文字未満(空文字列を含む)ならサーバー起動時に例外を投げて即座に落とす(`MIN_AUTH_TOKEN_LENGTH = 16`)。エラーメッセージに `openssl rand -hex 32` での再生成方法を明記。
- [server/tests/config.test.ts](server/tests/config.test.ts) を新規作成。空文字列・短すぎる文字列・16文字以上の3ケースを検証(3 tests)。
  - ⚠️ 「AUTH_TOKEN完全未設定」のケースはテストに含めていない。`dotenv.config()` は既にセットされている環境変数を上書きしない仕様のため、ローカルの実在する `server/.env`(このマシンにも実運用のため作成済み)からdeleteした変数が再読込されてしまい、テスト結果が環境依存になるため。
- `npm test`(21 tests)・`npm run build` とも成功確認済み。

**影響ファイル**: [server/src/config.ts](server/src/config.ts), [server/tests/config.test.ts](server/tests/config.test.ts)

---

## PBI-013: MacCatalystでAuth Tokenがsecure storageに保存できない 🔴 ✅(実機確認済み)

**現状 / 問題点**
- Settings画面でAuth Tokenを入力して保存しても、画面を開き直すとフィールドが空に戻る(実機スクリーンショットで確認)。メイン画面には「Server not configured yet」が表示され続ける。
- 1回目の調査: [Entitlements.plist](client/CopilotChatApp/Platforms/MacCatalyst/Entitlements.plist) で `com.apple.security.app-sandbox` が `true` になっていたが、`keychain-access-groups` エンタイトルメントが設定されておらず、かつビルドが無署名だった。Sandboxを`false`に変更したが、実機再テストで依然として失敗("Error adding record: MissingEntitlement")。
- 2回目の調査: `keychain-access-groups` エンタイトルメントを追加したところ、今度は**アプリ自体が起動できなくなった**("Launchd job spawn failed")。ログ調査の結果、管理対象MacのMDMポリシーが、プロビジョニングプロファイルを持たないentitlements付きアプリの起動自体をブロックしていることが判明。ローカルのApple Development証明書で署名しても(TeamIdentifier付与は成功)、プロビジョニングプロファイルが無い限りMDMに弾かれ続けるため、この環境ではSecureStorage(Keychain)の利用自体が現実的に不可能と判断。

**対応内容(最終方針: SecureStorage/Keychainを廃止し、独自AES暗号化ファイルストレージへ移行)**
- [Entitlements.plist](client/CopilotChatApp/Platforms/MacCatalyst/Entitlements.plist): `keychain-access-groups` は追加せず、`com.apple.security.app-sandbox=false` のみを維持(entitlements自体を持たない=MDMに弾かれない、署名も不要)。
- [CopilotChatApp.csproj](client/CopilotChatApp/CopilotChatApp.csproj): `CodesignKey` は設定せず、無署名ビルドのまま維持。
- [SettingsService.cs](client/CopilotChatApp/Services/SettingsService.cs): `SecureStorage` の代わりに、`FileSystem.AppDataDirectory` 配下に `AesGcm`(256bit鍵、都度ランダムnonce)で暗号化したファイル(`.authstore.enc` + 鍵ファイル `.authstore.key`)としてAuth Tokenを保存する自前実装に変更。両ファイルとも `File.SetUnixFileMode` で所有者読み書きのみ(600相当)に制限。旧SecureStorage・旧平文Preferencesからの一度きりの移行処理も維持。
- OS Keychainほどの堅牢性はない(鍵ファイルも同じ場所に平文で存在)が、entitlements・コード署名・プロビジョニングプロファイルを一切必要とせず、MDM制限下でも動作する。個人利用のローカルアプリとして妥当なトレードオフと判断。
- **実機確認済み**: ユーザーが実機でAuth Tokenを保存し成功を確認。`~/Library/.authstore.enc`(60byte, nonce+tag+ciphertext)・`~/Library/.authstore.key`(32byte)が生成され、パーミッション`-rw-------`、中身がランダムなバイナリ(平文トークンなし)であることをターミナルで直接検証済み。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。

**影響ファイル**: [client/CopilotChatApp/Platforms/MacCatalyst/Entitlements.plist](client/CopilotChatApp/Platforms/MacCatalyst/Entitlements.plist), [client/CopilotChatApp/CopilotChatApp.csproj](client/CopilotChatApp/CopilotChatApp.csproj), [client/CopilotChatApp/Services/SettingsService.cs](client/CopilotChatApp/Services/SettingsService.cs), [client/CopilotChatApp/Views/SettingsPage.xaml.cs](client/CopilotChatApp/Views/SettingsPage.xaml.cs)

---

## PBI-014: Chat Font Size変更が既存メッセージに反映されない 🟡 ✅

**現状 / 問題点(解決前)**
- Settings画面でChat Font Sizeを変更しても、変更前から表示されていた既存のメッセージバブルのフォントサイズは変わらない。新規に送受信したメッセージには正しい新サイズが適用される。
- 原因調査: メッセージ本文は [MainPage.xaml](client/CopilotChatApp/MainPage.xaml) の `idk:MarkdownView`(サードパーティ製 `Indiko.Maui.Controls.Markdown` v1.10.0)の `TextFontSize="{DynamicResource ChatFontSize}"` で表示している。`SettingsService.ChatFontSize` のsetterは `Application.Current.Resources[ChatFontSizeResourceKey]` を正しく更新しており(`ApplyChatFontSizeResource`)、これ自体はMAUIの標準的なDynamicResource更新であり動くはず。真因は `MarkdownView` 側ではなく、**`CollectionView`の自己サイジングセルが、DynamicResourceによるFontSize変更だけでは既存の描画済みセルを遡及再計測しない**という、より根の深いMAUI側の挙動だった(フォントを大きくすると、古い(小さい)サイズ基準の高さのままのセルに新しいサイズの文字が収まらずクリップされる、という表示崩れとして顕在化)。

**対応内容(2026-07-07)**
- [MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs) の `OnAppearing()`(Settings画面から戻ってくる度に発火)で、直前に描画した時点のフォントサイズと現在のフォントサイズを比較。変化していれば `MessagesView.ItemsSource` を一旦 `null` にしてから入れ直し、全メッセージバブルを新しいフォントサイズで強制的に再計測・再描画させる。
- 併せて、当初は「1行分だけ見切れる」という別の関連バグ(ストリーミング中の最終行クリップ)も発覚・修正した(`ItemSizingStrategy="MeasureAllItems"`、ターン完了後の追従スクロール、Markdownテーブルの行数に応じたバブル下余白の動的計算など、複数の対策の組み合わせ)。詳細は `/memories/repo/session_management_redesign_plan.md` の増分C6・C7を参照。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。**実機確認済み**(フォントサイズ変更→既存メッセージにも即座に反映されることをユーザーが複数ラウンドのテストで確認、最終的に「完璧になった!」)。

**影響ファイル**: [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml), [client/CopilotChatApp/Services/SettingsService.cs](client/CopilotChatApp/Services/SettingsService.cs), [client/CopilotChatApp/Converters/BubbleConverters.cs](client/CopilotChatApp/Converters/BubbleConverters.cs), [client/CopilotChatApp/Models/ChatMessage.cs](client/CopilotChatApp/Models/ChatMessage.cs)

---

## PBI-015: メッセージ送信中に何も表示されずハングしているか分からない 🟡 ✅

**現状 / 問題点**
- メッセージ送信後、Copilot CLIが応答を返し始めるまでの間(特にツール呼び出しが絡む長い処理の間)、画面に何の変化もなく、アプリがハングしているのか処理中なのか区別がつかなかった。

**対応内容**
- [ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs): 新しいプロパティ `IsWaitingForResponse` を追加。`SendCommand` 実行時にtrueにし、最初のストリーミングテキスト(`AppendAssistantDelta`)またはツールイベント(`HandleToolEvent`)が届いた時点、あるいはエラー/完了時にfalseに戻す。既存の `IsSending`(ターン完了まで真のまま、Sendボタンの無効化用)とは役割を分離し、「本当に無音の待機区間」だけを表す。
- [MainPage.xaml](client/CopilotChatApp/MainPage.xaml): 入力欄の直上に `ActivityIndicator` + 「Copilot is working…」ラベルを追加、`IsWaitingForResponse` にバインドして表示/非表示を切り替え。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。**実機確認済み**(ユーザーテストOK)。

**影響ファイル**: [client/CopilotChatApp/ViewModels/ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml)

---

## PBI-016: Mac CatalystでCmd+Enter送信ができない 🟡 ✅

**現状 / 問題点**
- メッセージ入力欄(`Editor`、複数行入力)でCmd+Enterを押しても送信されない。Enterは改行としてテキストに挿入されるのみで、送信にはマウスでSendボタンを押す必要があった。

**対応内容**
- MAUIの `KeyboardAccelerator` は `MenuFlyoutItem`(メニューバー項目)にしかアタッチできず、`Editor` のような任意のコントロールには直接付けられない(公式ドキュメントで確認)。`UIKeyCommand` をUITextViewに動的追加する案(`UIResponder.AddKeyCommand` 相当)も検討したが、.NET for iOS/MacCatalystの `UIKeyCommand.Create` は全てSelectorベースのオーバーロードのみで、Action/ラムダを直接渡せず、`NSObject`のサブクラス化+`[Export]`属性付きメソッドが必要になり複雑すぎるため断念。
- 代わりに、macOSのメニューバーに小さな「Message」メニューを追加し、その中の「Send」`MenuFlyoutItem`(`Command={Binding SendCommand}`)に `KeyboardAccelerator(Modifiers=Cmd, Key="\r")` を設定する方式を採用([MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs) `SetUpSubmitShortcut()`、`#elif MACCATALYST` 分岐)。
- ハマりポイント: コンストラクタ内で `MenuBarItems.Add(...)` を呼んでも、ページがまだネイティブウィンドウにアタッチされておらずメニューバーに反映されなかった。`Loaded` イベント(初回のみ実行するようフラグでガード)に移動して解決。
- Windows向けの `Ctrl+Enter`(`TextBox.PreviewKeyDown` ベース、既存実装)と同じメソッド内で `#if WINDOWS` / `#elif MACCATALYST` の分岐として共存させている。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。**実機確認済み**(ユーザーテストOK、メニューバーに「Message > Send ⌘↩」が表示されCmd+Enterで送信できることを確認)。

**影響ファイル**: [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs)

---

## PBI-017: ストリーミング中にメッセージリストが自動追従しない 🟡 ✅

**現状 / 問題点**
- アシスタントの応答が長い場合、ストリーミングでテキストが伸びていく間、表示が自動で最新行に追従せず、手動でスクロールしないと最新の内容が見えなかった。

**対応内容**
- 原因: [MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs) の `ScrollToLatest()` は `Messages.CollectionChanged` の `Add`(新規メッセージバブル追加)時にのみ呼ばれていた。しかしストリーミング中は新規バブルを都度追加するのではなく、既存の `ChatMessage.Text` プロパティを更新しているだけ(`ChatViewModel.AppendAssistantDelta`)なので、`CollectionChanged` イベントが発火せず追従しなかった。
- 新規追加されたメッセージの `PropertyChanged` イベントも購読するように変更し、`Text`(ストリーミング更新)や `IsRunning`(ツール完了)が変わるたびに `ScrollToLatest()` を呼ぶようにした。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。**実機確認済み**(ストリーミング中も自動スクロールで追従することをユーザーが確認)。

**影響ファイル**: [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs)

---

## PBI-018: グレーの吹き出し上でMarkdown見出し(H2/H3等)の文字色が薄く見づらい 🟡 ✅

**現状 / 問題点**
- アシスタントの吹き出し背景色(`#E6E6E6`、薄いグレー)の上に、Markdownの見出し(H2/H3など)がデフォルトの薄いグレー文字で描画され、コントラストが低くて読みづらかった(実機スクリーンショットで確認)。

**対応内容**
- サードパーティ製 `Indiko.Maui.Controls.Markdown` の `MarkdownView` には `H1Color`〜`H6Color`(および `H1ColorLight`/`H1ColorDark` 等のテーマ別バリアント)という `BindableProperty` が存在することをDLLのメタデータ文字列から確認。
- [MainPage.xaml](client/CopilotChatApp/MainPage.xaml) の `MarkdownView` に `H1Color`〜`H6Color` を全て `#1A1A1A`(濃いダークグレー)に明示指定。吹き出しの背景色(ユーザー `#DCE6FF`、アシスタント `#E6E6E6`)はどちらもライトカラー固定でテーマ非依存のため、見出し色も固定でよいと判断。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。**実機確認済み**(見出しの視認性が改善されたことをユーザーが確認)。

**影響ファイル**: [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml)

---

## PBI-019: 画像のコピペ(添付)対応 🟡 ✅実機確認済み

**現状 / 問題点**
- 入力欄(`Editor`)ではテキストのペーストはできるが、画像をクリップボードから貼り付けてCopilotに添付送信することはできなかった。

**実装内容**
- **採用方針**: ペースト自動検出(⌘V横取り)ではなく「📎 明示的な添付ボタン」を採用。Cmd+Vのテキスト編集動作と衝突するリスクを避けるため。
- **クライアント→サーバー**: 📎ボタンタップで `UIKit.UIPasteboard.General.Image`(MacCatalyst)から画像取得→PNGバイト列を `PendingAttachment` としてメモリ上にステージング(サムネイル表示、✕で削除可)→送信時にbase64エンコードして `ChatAttachment` としてWebSocket送信。
- **プロトコル拡張**: [server/src/protocol.ts](server/src/protocol.ts) の `ClientChatMessage` に `attachments?: AttachmentPayload[]`(mimeType/data/fileName)を追加。
- **サーバー側**: [copilotRunner.ts](server/src/copilotRunner.ts) でbase64を `os.tmpdir()` に一時ファイル書き出し(`0o600`)→`--attachment <path>` をCLI引数に追加→ターン終了後(`finally`)に必ず削除。CLIの `--attachment` はファイルパスのみ受け付ける仕様(メモリ上のバイト列を直接渡す方法は無い)ため、一時ファイル方式は必須。
- **UI表示**: 送信済みメッセージのバブル内にも添付画像のサムネイルを表示([Models/ChatMessage.cs](client/CopilotChatApp/Models/ChatMessage.cs) に `Attachments`/`HasAttachments` を追加)。
- `dotnet build -f net10.0-maccatalyst` で0エラー確認済み。**実機確認済み**(クリップボード画像→📎→送信→Copilotが画像を認識して応答することをユーザーが確認)。

**影響ファイル**: [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml), [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs), [client/CopilotChatApp/ViewModels/ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs), [client/CopilotChatApp/Models/ChatMessage.cs](client/CopilotChatApp/Models/ChatMessage.cs), [client/CopilotChatApp/Models/PendingAttachment.cs](client/CopilotChatApp/Models/PendingAttachment.cs), [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs), [server/src/protocol.ts](server/src/protocol.ts), [server/src/copilotRunner.ts](server/src/copilotRunner.ts), [server/src/wsServer.ts](server/src/wsServer.ts)

---

## PBI-020: 会話ごとに作業ディレクトリ(CWD)を切り替えられるマルチワークスペース対応 🟢 ✅

**背景 / 動機**
- 現状、サーバーは起動時に `WORK_DIR`(環境変数、なければ `server/workspace`)を **1つだけ** 解決し、全会話で共有している。CLIは毎ターン `-C config.workDir` 固定で起動される([server/src/copilotRunner.ts](server/src/copilotRunner.ts#L173), [server/src/config.ts](server/src/config.ts#L48-L50))。
- そのため「別のリポジトリ(例: `minutes-repeater`)を対象にした会話」と「このアプリ自身(`copilot-chat-app`)を対象にした会話」を **同一サーバー・同一クライアントから並行して扱えない**。作業対象を変えるにはサーバーを別の `WORK_DIR` で再起動する必要がある。
- クライアント(iPhone/Mac)から「このプロジェクトのエージェント」「あのプロジェクトのエージェント」を切り替えて、それぞれ独立した文脈で会話・管理できるようにしたい(＝1サーバーが複数CWDのCLIワーカーをオーケストレーションする構想)。

**現状の仕組み(調査結果)**
- 会話は `conversationId` 単位で `--session-id` にマップされ、CLIは「新規なら作成・既存なら再開」で文脈を保持する([wsServer.ts](server/src/wsServer.ts#L57-L58), [copilotRunner.ts](server/src/copilotRunner.ts#L138-L139))。
- **重要**: CLIは対話TTYを張りっぱなしにするのではなく、ターンごとに `spawn` するワンショット実行。よって「CWDを可変にする」だけなら node-pty のような擬似端末は不要で、**ターン起動時に渡す `-C` を会話単位で差し替える**だけで実現できる。
- ネックは `config.workDir` がプロセスグローバルに1つな点と、`conversationId → cwd` の対応を持つ層が無い点。

**対応案(段階的)**
1. **プロトコル拡張**: `ClientChatMessage`(または新規の `workspace:*` メッセージ)に `workDir`(絶対パス)または事前登録した `workspaceId` を持たせる([server/src/protocol.ts](server/src/protocol.ts))。
2. **ランナー拡張**: `runCopilotTurn` に `workDir` 引数を追加し、`-C` に `config.workDir` ではなく会話ごとの CWD を渡す([copilotRunner.ts](server/src/copilotRunner.ts#L144-L176))。未指定時は従来通り `config.workDir` にフォールバック(後方互換)。
3. **セッション管理拡張**: `wsServer.ts` に `conversationId → workDir` のマップを追加し、会話作成時に確定・以降のターンで再利用([wsServer.ts](server/src/wsServer.ts#L57-L58))。
4. **ワークスペース登録/一覧API**: 許可された CWD の一覧を返す `workspaces:list` と、任意パス指定を許すかを制御する仕組み(セキュリティ: 後述)。
5. **クライアントUI**: 会話開始時にワークスペース(プロジェクト)を選択、または会話一覧にワークスペース名バッジを表示して切り替え。

**セキュリティ上の注意(必須)**
- サーバーは `--allow-all-paths` でフルエージェント動作するため、`workDir` をクライアントから自由指定させると **任意ディレクトリをエージェントの作業対象にできてしまう**(PBI-001/USAGE.md の信頼境界がさらに広がる)。
- 対策として、`WORKSPACES`(許可リスト。環境変数でカンマ区切りの絶対パス群)を導入し、**クライアントは許可リスト内の `workspaceId` しか選べない**設計を基本とする。任意パス指定は明示的なオプトイン(`ALLOW_ARBITRARY_WORKDIR=true`)がある場合のみ許可。
- 指定パスが許可リスト配下か(パストラバーサル防止のため `path.resolve` 後に接頭辞チェック)を検証する。

**受け入れ条件(Draft)**
- 1つのサーバーに対し、`minutes-repeater` 用と `copilot-chat-app` 用の2会話を並行して持て、各会話のファイル操作が **それぞれの CWD 内に閉じる** ことを確認できる。
- `workDir` 未指定の既存クライアントは従来通り `config.workDir` で動作する(後方互換)。
- 許可リスト外のパスを指定した場合はエラーになり、CLIが起動しない。

**対応内容(サーバー側、2026-07-06)**
- 許可リストは `BROWSE_ROOTS`(カンマ区切り、複数指定可、デフォルト=ホームディレクトリ)として実装。境界チェックは `path.relative` ベース([server/src/pathAccess.ts](server/src/pathAccess.ts))。
- `runCopilotTurn` に `cwd` 引数を追加、未指定時は `config.workDir` にフォールバック([server/src/copilotRunner.ts](server/src/copilotRunner.ts))。
- `wsServer.ts` の `runConversationTurn()` が新規セッション作成時はクライアント指定cwd(要許可リスト内検証)、既存セッション再開時はCLI自身の記録値(`sessions.cwd`)を必ず使う設計に(クライアント値は再開時は無視 = 誤って別フォルダに切り替わる事故を防止)。
- フォルダブラウズ用に `fs:list-dir`/`fs:git-clone` プロトコルも追加済み([server/src/fsBrowser.ts](server/src/fsBrowser.ts))。VSCode Copilot Chatの「Select Folder / Clone Repository」的なUXをクライアント側で組み立てられる状態。
- **クライアントUIも実装済み**: `Views/NewChatPage.xaml(.cs)` が①デフォルトworkspace ②フォルダを選ぶ(BROWSE_ROOTS配下をブラウズ) ③Gitリポジトリをクローン、のミニウィザードとして機能する。`HomePage`(起動画面)の「New chat」から開く。複数サーバー(マルチサーバー化)対応後は、New Chat時にどのサーバーで始めるかも選択できる。実機確認済み(フォルダ選択・クローンの一連の流れをユーザーが確認)。

**影響ファイル**: [server/src/copilotRunner.ts](server/src/copilotRunner.ts), [server/src/config.ts](server/src/config.ts), [server/src/pathAccess.ts](server/src/pathAccess.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [server/src/fsBrowser.ts](server/src/fsBrowser.ts), [client/CopilotChatApp/Views/NewChatPage.xaml.cs](client/CopilotChatApp/Views/NewChatPage.xaml.cs), [client/CopilotChatApp/ViewModels/ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs)

---

## PBI-021: セッション削除(Soft/Hard)機能 🟡 ✅

**背景 / 動機**
- Sessions画面には一覧・resumeはあるが削除機能が無い([server/src/sessionHistory.ts](server/src/sessionHistory.ts), `client/CopilotChatApp/Views/SessionsPage.xaml.cs`(削除済み))。セッションが増えてくると整理できない。

**設計方針(合意済み、2026-07-06)**
- **Soft delete(既定)**: サイドカーメタ([server/src/sessionMeta.ts](server/src/sessionMeta.ts)、実装済み)の `archived:true` を立てるだけ。Copilot CLI自身の `~/.copilot/session-store.db` には触れない。一覧から隠すだけで、ターミナルの `copilot --resume` から見ても影響なし。可逆。
- **Hard delete(明示操作)**: CLI本体DBの該当行(sessions + turns)も実際に削除。取り消し不可。UI文言で「これは元に戻せません、CLIの履歴からも消えます」と明示 + 確認ダイアログ必須。
  - 実行中セッション(そのsessionIdが`conversationLocks`に載っている間)は削除拒否すること(CLIプロセスがDBを触っている最中に外部からDELETEする事故を防ぐ)。
- サーバー側プロトコルに `sessions:delete`(`mode: 'soft' | 'hard'`)を追加する想定。Soft側は既存の `setSessionArchived(id, true)` で完結。Hardは新規に `~/.copilot/session-store.db` への書き込み実装が必要(現状 `sessionHistory.ts` は readOnly オープンのみ)。

**対応内容(2026-07-09実装・実機/実DB確認済み)**
- [server/src/protocol.ts](server/src/protocol.ts): `ClientSessionsDeleteMessage`(`sessions:delete`, `mode: 'soft' | 'hard'`)と`ServerSessionsDeleteResultMessage`を追加。
- [server/src/wsServer.ts](server/src/wsServer.ts): `sessions:delete`ハンドラを追加。`soft`は既存の`setSessionArchived(id, true)`を呼ぶだけ。`hard`は`activeConversationTurns`(PBI由来のbusyチェックと同じ仕組み)を見て実行中セッションを拒否し、`deleteSessionHard()`実行後に`deleteSessionMeta()`でサイドカーも掃除。
- [server/src/sessionHistory.ts](server/src/sessionHistory.ts): `deleteSessionHard(sessionId)`を新規実装。**実際の`~/.copilot/session-store.db`を直接調査した結果**、`session_id`のFOREIGN KEYを持つテーブルが`turns`以外にも`checkpoints`・`session_files`・`session_refs`・`forge_trajectory_events`・`assistant_usage_events`と複数あることが判明、全部消してから`sessions`行を消す実装に。`search_index`(FTS5仮想テーブル)は`node:sqlite`が`"no such module: fts5"`エラーを返すため意図的に対象外(FK制約の対象ではないので実害なし、CLIの全文検索に若干のゴミが残るだけ)。`dbPath()`はテスト用に`COPILOT_SESSION_STORE_DB`環境変数で上書き可能に変更。
- [server/tests/sessionHistory.test.ts](server/tests/sessionHistory.test.ts)(新規): 削除対象/他セッション非干渉/存在しないID/他FK参照テーブル全消去、をテスト。
- [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs): `DeleteSessionAsync(sessionId, mode)`を追加。
- [client/CopilotChatApp/Views/HomePage.xaml.cs](client/CopilotChatApp/Views/HomePage.xaml.cs): カードの「⋯」メニューに「完全に削除...」を追加(`DisplayActionSheet`の`destruction`ボタンとして、OS標準の強調表示)。確認ダイアログ必須。
- **実地検証**: 使い捨てのWebSocketテストセッションを作成 → Hard delete → 実際の`~/.copilot/session-store.db`を直接読んで完全に消えたことを確認。実行中(ターン処理中)のセッションへのHard delete試行が即座に拒否されること、ターン自体は正常完了することも確認。サーバーテスト: 11 suites / 93 tests 全パス。

**影響ファイル**: [server/src/sessionHistory.ts](server/src/sessionHistory.ts), [server/src/sessionMeta.ts](server/src/sessionMeta.ts), [server/src/protocol.ts](server/src/protocol.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs), [client/CopilotChatApp/Views/HomePage.xaml.cs](client/CopilotChatApp/Views/HomePage.xaml.cs)

## PBI-022: クロスセッション発信のターンを見分けられるようにする 🟡 ✅

**背景 / 動機**
- `session-control`の`run_turn_on_session`でセッションAからセッションBにメッセージを送ると、Bの履歴には「Bのユーザーが普通に打った発言」と全く区別つかずに残ってしまう。あとでBを開いた本人が「これ自分が打った覚えないんだけど!?」って混乱する懸念。

**対応内容(2026-07-08実装・実機確認済み)**
- [server/src/sessionMeta.ts](server/src/sessionMeta.ts): サイドカーに`sessionControlTurnIndexes`(ターン番号の配列)を追加、`markSessionControlTurn(sessionId, turnIndex)`で記録。CLI本体の`session-store.db`には触れない(PBI-021同様の設計方針)。
- [server/src/internalControlApi.ts](server/src/internalControlApi.ts): `/internal/run-turn`成功後、作成されたターンのインデックスを特定してマーク。
- [server/src/protocol.ts](server/src/protocol.ts) / [server/src/wsServer.ts](server/src/wsServer.ts): `SessionTurnDto`に`fromOtherSession?: boolean`を追加、`sessions:history`のレスポンスにサイドカー情報をマージ。
- クライアント(`ChatClientService.cs`の`SessionTurn`、`ChatMessage.cs`の`IsFromOtherSession`、`ChatViewModel.cs`の`ApplyResumedSession`): フラグを両方のバブル(ユーザー発言側・アシスタント返信側)に伝播。
- [client/CopilotChatApp/Converters/BubbleConverters.cs](client/CopilotChatApp/Converters/BubbleConverters.cs): `MessageToBubbleColorConverter`でアンバー色(`#FFE0B2`)のバブルに変更、[MainPage.xaml](client/CopilotChatApp/MainPage.xaml)に「🤖 Message from other Agent」バッジラベルを追加。
- **実地検証**: 実際に`run_turn_on_session`相当のAPI呼び出しを行い、サイドカーファイルへの記録とMac Catalystクライアントでのバブル色/バッジ表示をスクリーンショットで確認済み。

**影響ファイル**: [server/src/sessionMeta.ts](server/src/sessionMeta.ts), [server/src/internalControlApi.ts](server/src/internalControlApi.ts), [server/src/protocol.ts](server/src/protocol.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs), [client/CopilotChatApp/Models/ChatMessage.cs](client/CopilotChatApp/Models/ChatMessage.cs), [client/CopilotChatApp/ViewModels/ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs), [client/CopilotChatApp/Converters/BubbleConverters.cs](client/CopilotChatApp/Converters/BubbleConverters.cs), [client/CopilotChatApp/App.xaml](client/CopilotChatApp/App.xaml), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml)

## PBI-023: HomePageに「他のセッションに聞く」ショートカット → 削除済み(PBI-025に統合)

**背景 / 動機**
- 今はモデルが自然言語で気を利かせて`run_turn_on_session`を呼ぶのを待つしかない。セッションカードの「⋯」メニューに「他のセッションに一言送る」を追加し、対象セッション選択 → メッセージ入力 → 送信、をノンエージェント的に直接できるようにしたい。
- PBI-022のバッジ表示と組み合わせれば「誰が何をいつ送ったか」が追いやすくなる。

**ユースケース案(2026-07-09追記)**
- **リードCLIによる進捗監視・助言**: 1つの「リード」セッションが他の複数セッションの進捗(`list_sessions`/`get_session_summary`)を定期的に確認し、スタックしてる・詰まってそうなセッションがあれば`run_turn_on_session`でヒント/助言を送る、という使い方。
- **マルチプラットフォーム開発の自動協調**: 例えばiOS版とAndroid版を並行開発してるときなど、あるセッションが「他プラットフォーム版の実装はどうなってるか」を自動的に他セッションへ問い合わせて協調する、という使い方。

**実装(2026-07-09)→ その後撤去(2026-07-09同日中)**
- 一度は実装・実地検証まで完了(`sessions:ask`/`sessions:ask-result`、`AskSessionAsync`、HomePageの「⋯」メニューへの追加)。
- しかしユーザーが本当にイメージしていたのは「Home画面からのワンショット一言送信」ではなく、メイン+子セッションの**オーケストレーション**機能(PBI-025参照)だったと判明。中途半端に似た機能を2つ残すと混乱するため、**サーバー・クライアント双方から完全に削除**(コミット`faa128d`)。`protocol.ts`の`ClientSessionsAskMessage`/`ServerSessionsAskResultMessage`、`wsServer.ts`の`sessions:ask`ハンドラ、`ChatClientService.cs`の`AskSessionAsync`関連一式、`HomePage.xaml.cs`の`AskOtherSessionAsync`と「⋯」メニューの項目、すべて撤去済み。
- 「他セッションに一言」的なユースケースは、PBI-025の`spawn_session`(既存セッションをアタッチしてメッセージだけ送るケース)や、既存の`run_turn_on_session`(session-control MCP経由)でカバーされる想定。

**影響ファイル**: [client/CopilotChatApp/Views/HomePage.xaml.cs](client/CopilotChatApp/Views/HomePage.xaml.cs), [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs), [server/src/protocol.ts](server/src/protocol.ts), [server/src/wsServer.ts](server/src/wsServer.ts)

## PBI-024: McpServersページで`session-control` MCPの存在を可視化 🟢 ✅

**背景 / 動機**
- `session-control` MCPサーバーは`copilot mcp add`でCLI全体にグローバル登録されており、セッション単位のON/OFFという技術的な強制力は無い(運用ルールのみ、[server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts)の設計メモ参照)。
- ユーザーがどのセッションでも実は`list_sessions`/`get_session_summary`/`run_turn_on_session`が呼べる状態にあることに気づきにくい。

**対応内容(2026-07-09実装・実機確認済み)**
- [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs): `McpServerSummary`に`IsSessionControl`(`Name == "session-control"`)を追加。
- [client/CopilotChatApp/Views/McpServersPage.xaml](client/CopilotChatApp/Views/McpServersPage.xaml): 一覧の`session-control`の下に「🌐 全セッション共通で自動的に有効な組み込み機能です(削除してもサーバー再起動時に自動で再登録されます)」の注記バッジを追加。既存の「削除」ボタンはそのまま(消しても実害無し・次回サーバー起動時に`index.ts`の`ensureSessionControlMcpRegistered`が自動で再登録するため)。
- Mac Catalystクライアントで実際に起動して表示確認済み。

**影響ファイル(想定)**: [client/CopilotChatApp/Views/McpServersPage.xaml](client/CopilotChatApp/Views/McpServersPage.xaml), [client/CopilotChatApp/Views/McpServersPage.xaml.cs](client/CopilotChatApp/Views/McpServersPage.xaml.cs)

---

## PBI-025: メイン+子セッションのオーケストレーション画面(Spawn) 🟡 Phase 1・2完了、Phase 3未着手

**背景 / 動機**
- PBI-023の「他のセッションに一言送る」ショートカットは実装したが、ユーザーが本当にイメージしていたのは別物と判明(2026-07-09)。PBI-023は完全に削除し、本PBIで本命の「オーケストレーション」機能を別途設計する。
- イメージ: **メインセッション**が1つあり、そこから**子セッション**をSpawn(生成/アタッチ)する。画面は上下分割: 上部=今まで通りのチャット画面(入力欄込み、メインセッションと会話)、下部=Spawnされた子セッションのログが分割表示される。
- **子セッションには人間は直接介入しない**。子セッションを操作できるのはメインセッションのAIだけ(`run_turn_on_session`/新設の`spawn_session`経由)。人間はメインとだけ会話し、必要に応じてAIが判断して子セッションに指示を中継する。

**設計方針(2026-07-09ユーザー確認済み)**
- **Spawnのトリガー**: 人間が手動で(UIのボタン等から)子セッションを追加できる **かつ** メインセッションのAIが自分の判断でツール(`spawn_session`)を呼んでSpawnできる、の両対応。
- **子セッションへの介入タイミング**: 人間がメインセッションに指示するたびに、必要ならAIが判断して子セッションに指示を中継する(常時ではなく、AIの判断)。
- **子セッションパネルの更新方法**: フルのリアルタイムストリーミング(1文字ずつ)は見送り、**ポーリング方式**を採用。子セッションが`activeConversationTurns`的に「作業中」の間だけ`sessions:history`を数秒おきに再取得してペインを更新する(完了したら最新の返信がまとまって反映される体感)。既存の`sessions:history`をそのまま使い回せるため、新しいサーバー配線(購読/ブロードキャスト機構)が不要になり実装コストが大きく下がる。
  - **却下した案**: セッションIDごとの購読者にストリーミング配信するpub/sub機構の新設。理由: (1) 人間の手動Spawnと(2) AIが`session-control` MCP経由(HTTPベースの`internalControlApi.ts`、WebSocketと無関係な世界)で自動Spawnする場合とで通るパスが全く異なり、両方から同じリアルタイム配信の仕組みに繋ぎ込む必要があって複雑すぎると判断。ポーリングで様子を見て、物足りなければ後日改めて検討する。
- **子セッションの実体**: 新規作成 **と** 既存セッションのアタッチ、両方に対応。

**技術的課題「呼び出し元セッションIDの自己特定」→ 解決済み(2026-07-09)**
- 当初の懸念: MCPツール(`spawn_session`)はステートレスな呼び出しで、「今の自分がどのセッションIDで動いているか」を呼び出し元(モデル)自身が把握する標準的な手段が無い。
- 当初の対策案(**採用せず**): Orchestrator画面が「あなたの現在のセッションIDは`<uuid>`です」という趣旨のシステム的な一言メッセージをコンテキストに注入しておく方式。ユーザー自身が「長い会話でモデルが忘れる可能性があり、確実性に欠けるのでは?」と指摘、これがきっかけで別解を調査。
- **実際に採用した解決策(実地検証済み)**: OSのプロセスツリーを直接調べる方式。[server/src/copilotRunner.ts](server/src/copilotRunner.ts)の`runCopilotTurnCore`は毎ターン新しい`copilot`プロセスを`--session-id=<uuid>`を自分自身の引数に載せて`spawn`する。ライブで`ps -eo pid,ppid,command`を使い、`session-control` MCPサーバーのサブプロセス(`sessionControlMcpServer.js`)の直接の親プロセス(`process.ppid`)が常にその`copilot --session-id=<uuid> ...`プロセスそのものであることを実証。これにより、モデルの協力(自己申告)を一切必要とせず、OSレベルで100%決定論的に呼び出し元セッションIDを特定できる。
  - 新設 [server/src/callerSessionId.ts](server/src/callerSessionId.ts): `getCallerSessionId()`。macOS/Linuxは`ps -p <ppid> -o command=`、Windowsは PowerShellの`Get-CimInstance Win32_Process -Filter "ProcessId=<ppid>"`で親プロセスのコマンドラインを取得し、正規表現で`--session-id`を抽出。取得失敗時は例外を投げず`null`を返す(呼び出し側でエラーメッセージに変換)。
  - `spawn_session`ツールの`inputSchema`には**意図的に`parentSessionId`パラメータを含めていない**。モデルに渡させる余地自体を無くすことで、間違えようがない設計にした。
  - **実地検証**: 実際のセッションに自然言語で「`parentSessionId`は渡さなくていい、自分で判断して」と指示して`spawn_session`を呼ばせ、生成された子セッションの`session-meta.json`内`parentSessionId`が、呼び出し元セッションの実際のIDと完全一致することを確認済み。

**対応方針・進捗**
- **Phase 1(サーバー基盤・親子関係)✅ 完了**:
  - [server/src/sessionMeta.ts](server/src/sessionMeta.ts): `parentSessionId?: string`をサイドカーに追加、`setSessionParent(sessionId, parentSessionId)`・`getChildSessionIds(parentSessionId)`を新設。
  - [server/src/sessionHistory.ts](server/src/sessionHistory.ts): `getSessionSummary(sessionId)`を新設(BROWSE_ROOTSによるフィルタに関係なく単一セッションを取得)。
  - [server/src/protocol.ts](server/src/protocol.ts) / [server/src/wsServer.ts](server/src/wsServer.ts): 人間手動Spawn用の`sessions:spawn`/`sessions:children`メッセージを追加。内部的にはPBI-021/022で作った`runConversationTurn(..., { rejectIfBusy: true })`・`markSessionControlTurn`と同系統の仕組みを再利用。
- **Phase 2(AIによる自動Spawn)✅ 完了(2026-07-09)**:
  - 新設 [server/src/callerSessionId.ts](server/src/callerSessionId.ts)(上記参照)。単体テスト5件・実地E2E検証済み。
  - [server/src/wsServer.ts](server/src/wsServer.ts): `sessions:spawn`ハンドラのロジックを`spawnChildSession()`として関数化・export。WebSocketハンドラと内部HTTP APIの両方から共有。
  - [server/src/internalControlApi.ts](server/src/internalControlApi.ts): `POST /internal/spawn-session`を新設。
  - [server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts): `spawn_session`ツールを追加(`existingSessionId`/`cwd`/`message`のみ受け付け、`parentSessionId`は`getCallerSessionId()`で内部決定)。
- **Phase 3(クライアントUI)✅ 完了(2026-07-09)**:
  - 新規 [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml)(+`.xaml.cs`)・[client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs](client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs)。上部は既存の`ChatViewModel`をそのまま流用したコンパクトなチャットUI(メインセッションに接続)。下部は子セッション一覧を`BindableLayout`(ネストしたスクロール衝突を避けるためCollectionViewは使わず)で分割表示する読み取り専用ログペイン(入力欄なし)。
  - 各子ペインは、その子セッションが「作業中」(`SessionSummaryDto.busy`、`activeConversationTurns`由来)の間だけ`sessions:history`を数秒おきにポーリングして更新。`turnCount`が変わってない・busyでもない子は再フェッチをスキップして無駄なポーリングを削減。
  - 「＋ 子セッション」ボタンで手動Spawn(新規作成 or 既存セッション選択 + 任意の初回指示)。
  - [client/CopilotChatApp/Views/HomePage.xaml.cs](client/CopilotChatApp/Views/HomePage.xaml.cs)のセッションカード「⋯」メニューに「🧩 Orchestratorで開く」を追加。
  - [client/CopilotChatApp/Views/NewChatPage.xaml](client/CopilotChatApp/Views/NewChatPage.xaml)にも「🧩 Orchestratorとして開始」スイッチを追加、ブランドニューセッションからも直接Orchestratorを開始可能に(`MainPage(string cwd)`と同じ「`SettingsService.ResetConversation()`だけ済ませておき、実際のセッションは初回ターンで暗黙に作られる」パターンをOrchestratorVMにも移植)。

**影響ファイル**: [server/src/sessionMeta.ts](server/src/sessionMeta.ts), [server/src/sessionHistory.ts](server/src/sessionHistory.ts), [server/src/protocol.ts](server/src/protocol.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [server/src/internalControlApi.ts](server/src/internalControlApi.ts), [server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts), [server/src/callerSessionId.ts](server/src/callerSessionId.ts), [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml), [client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs](client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs), [client/CopilotChatApp/Views/HomePage.xaml.cs](client/CopilotChatApp/Views/HomePage.xaml.cs), [client/CopilotChatApp/Views/NewChatPage.xaml](client/CopilotChatApp/Views/NewChatPage.xaml)

---

## PBI-026: Orchestratorのクロスサーバー(マルチマシン)対応 🟢 ✅

**背景 / 動機**
- PBI-025のPhase 3を実機テストしたところ、Orchestratorの子セッションはメインセッションと**同じサーバー(同じマシン)上にしか作れない**ことが判明(ユーザーが「Windowsで`ipconfig`実行して」と頼んだのに、実際はMac上で`ifconfig`が実行された)。
- 原因: `spawn_session`(AI自動)も`sessions:spawn`(手動)も、実装がそのサーバープロセス自身(`wsServer.ts`)の中だけで完結していた。`session-control` MCPサーバーは`127.0.0.1`の自分と同じマシンのサーバーとしか喋れず(`internalControlApi.ts`はループバック限定)、複数サーバーを跨ぐ「フェデレーション」の概念はクライアント側(MAUIアプリの`SettingsService.GetProfiles()`)にしか存在しなかった。サーバー同士はお互いの存在を一切知らない設計だった。

**設計方針(2026-07-09ユーザー確認済み)**
- **P2Pフレームワーク(libp2p等)は不採用**: 対象は「少数・固定・完全に信頼できる、自分が所有するマシン」であり、libp2p/WebRTCメッシュ/DHTベース発見が解決する「未知の相手の発見・NAT越え・信頼してない相手との通信」という問題をそもそも持っていない。導入すると運用コスト(ブートストラップノード、暗号アイデンティティ管理等)だけ増える判断。
- **ネットワーク到達性の層とアプリロジックの層を分離**: 到達性(同一LAN/VPN/Tailscale等)は完全に別レイヤーとして扱い、アプリ側は「URL + トークンを知ってる相手」という抽象だけを見る設計に。
  - 現状は同一LAN前提でOK(社給PCにVPN導入不可のため)。
  - 将来Azure VM等の遠隔ピアを足したくなった場合は、**Tailscale**(WireGuardベースのメッシュVPN)を「自分が管理する側のマシン」(Mac・Azure VM)だけに入れる案が有力(社給Windows PCの制約を回避できる)。ピア設定のURLをLAN IPからTailscale IP/MagicDNS名に変えるだけで、アプリのコードは一切変更不要。
- **サーバー間通信は新しいプロトコルを作らず、既存のWebSocotプロトコルを再利用**: `internalControlApi.ts`(ループバック限定のHTTP)をネットワークに開放するのではなく、`session-control` MCPサーバーが受け取った`targetServer`指定を`internalControlApi.ts`が解釈し、**このサーバー自身がpeerに対して`ws`パッケージのクライアントとして接続**、既存の`sessions:spawn`/`sessions:spawn-result`メッセージをそのまま送受信する形にした。新しいポート・新しい認証方式を増やさずに済む。

**対応内容(2026-07-09実装・実地検証済み)**
- **サーバー側**:
  - [server/src/config.ts](server/src/config.ts): `PeerServerConfig`(`name`/`url`/`token`)と`config.peers`を追加。`PEER_SERVERS`環境変数(JSON配列)からパース、不正なエントリはスキップしてログ警告。単体テスト5件追加。
  - 新設 [server/src/peerClient.ts](server/src/peerClient.ts): `spawnOnPeer(peer, options)`。ピアのURLへ`ws`パッケージで短命な接続を開き、`sessions:spawn`を送って`sessions:spawn-result`を待つだけの薄い実装(接続10秒・全体5分でタイムアウト)。単体テスト7件(`ws`をモック)。
  - [server/src/internalControlApi.ts](server/src/internalControlApi.ts): `GET /internal/peers`(ピア名一覧、トークンは含めない)を新設。`POST /internal/spawn-session`に`targetPeer`を追加、指定時は`spawnOnPeer`経由でピアへ委譲、未指定時は従来通りローカルの`spawnChildSession`。
  - [server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts): `list_servers`ツールを新設(ピア名一覧をモデルに提示)。`spawn_session`に`targetServer`(任意)を追加。
- **クライアント側**:
  - [client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs](client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs): `mainProfileId`を保持するよう変更、他プロファイルへの`ChatClientService`接続を`_peerClients`辞書で遅延生成。`RefreshChildrenAsync`は**設定済み全プロファイル**に対して`sessions:children`を問い合わせて集約(未到達のプロファイルはその回だけスキップ、HomePageの集約と同じ「各サーバーは独立」原則)。`SpawnNewChildAsync`/`AttachExistingChildAsync`に`targetProfileId`を追加。
  - [client/CopilotChatApp/Views/OrchestratorPage.xaml.cs](client/CopilotChatApp/Views/OrchestratorPage.xaml.cs): 「＋ 子セッション」フローに「どのサーバーに作成しますか?」のサーバー選択ステップを追加(設定済みプロファイルが1つだけなら自動スキップ)。
  - [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml): 子ペインのヘッダーに`ProfileDisplayLabel`(どのサーバー上の子か)バッジを追加。
- **実地検証(2026-07-09)**: ローカルに2つ目のサーバープロセスを別ポート(5230)・別トークンで起動し疑似ピアとして使用。メインサーバー(5219)の`.env`に一時的に`PEER_SERVERS`を設定して再起動、実際のセッションに自然言語で「`list_servers`で確認してから`targetServer`を指定して`spawn_session`を呼んで」と指示 → `list_servers`が`test-peer`を正しく返し、`spawn_session`がピア(5230)上に子セッションを実際に作成、返信も正常に取得。ピア側の`session-meta.json`に`parentSessionId`(メインセッションのID)が正しく記録されていることも確認。テスト後、`.env`とテストセッションは元の状態にクリーンアップ済み。
- **本物のクロスマシン実地検証(2026-07-09、同日中)**: 疑似ピアだけでなく、**実際に別LAN上のWindows PC**を`PEER_SERVERS`に登録して本番テストを実施。
  - 1回目: Windows側が旧ビルド(`sessions:spawn`未対応)のままだったため、`peerClient.ts`が汎用エラー応答を無視して5分間ハングするバグが発覚 → `peerClient.ts`が`error`タイプの応答も即座に検知して分かりやすいメッセージで reject するよう修正(コミット`5123562`、テスト1件追加・115件全パス)。
  - 2回目(Windows側リビルド後): Task Schedulerの`Stop/Start`だけでは`node.exe`が孤児プロセスとして生き残り、古いビルドが動き続けるケースを警告 → ユーザーが確実に再起動し直した結果、**実際にWindows機上で`ipconfig /all`が実行され、実機のプライベートIPv4アドレスとサブネットマスクが正しく返ってくることを確認**。AIが`list_servers`→`spawn_session(targetServer: "windows-peer")`を自分の判断で呼び出し、人間は一切セッションIDや接続先を指定していない。
  - これにより、**クロスプラットフォーム(macOS→Windows)のAI自動Spawnが実運用レベルで動作することを実証済み**。テスト後、Mac側・Windows側双方のテストセッションをhard deleteでクリーンアップ済み。
- サーバー側テストスイート: 115件全パス(新規13件: config 5件 + peerClient 8件)。

**影響ファイル**: [server/src/config.ts](server/src/config.ts), [server/src/peerClient.ts](server/src/peerClient.ts), [server/src/internalControlApi.ts](server/src/internalControlApi.ts), [server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts), [server/.env.example](server/.env.example), [client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs](client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs), [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml), [client/CopilotChatApp/Views/OrchestratorPage.xaml.cs](client/CopilotChatApp/Views/OrchestratorPage.xaml.cs)

---

## PBI-027: OrchestratorのUX改善 5点 🟢 ✅

**背景 / 動機**
- PBI-025/026を実機で使い込んだユーザーからの細かい使用感フィードバック5点。

**対応内容(2026-07-09実装、コミット`5653626`)**
1. Orchestrator画面タイトルのアイコンを🧩→🎼に変更(「パズルのピースが謎」というフィードバックを受けて)。
2. メインチャットの入力欄を`Entry`→`Editor`に変更し、`MainPage`と同じ`Cmd+Enter`/`Ctrl+Enter`送信ショートカットを移植。
3. 「Copilotが応答中…」インジケーターを`MainChat.IsSending`にバインドして追加。
4. `HomePage`のセッションカードに「👑 親」「🔗 子」バッジを追加(後に「🎼 Conductor」「🎻 Player」へ改称、コミット`d73da75`)。
5. `orchestratorMain`フラグが立っているセッションをHomePageから開いた際、通常の`MainPage`ではなく`OrchestratorPage`へ自動的にルーティングするよう修正(せっかくOrchestratorとして使っていたセッションを開き直しても、毎回手動でOrchestrator画面に切り替える必要があった)。

**関連バグ修正**
- **クロスサーバー親バッジ不具合**(コミット`c1c277e`): サーバー側で「このセッションは親か?」を自分のサーバー内だけでスキャンして判定していたため、子が別サーバー(PBI-026)にSpawnされたケースを見逃していた。修正: サーバー側での判定をやめ、生の`parentSessionId`のみ公開。`HomePage`が全プロファイルを横断集約する際に`ApplyOrchestratorParentFlags()`でグローバルに「親」判定するよう変更。
- **Markdownテーブルの吹き出し下端クリッピング**(コミット`3a3d4fe`、微調整`4b805d8`): `Indiko.Maui.Controls.Markdown`のセルフ計測高さが、テーブルセル内でテキストが折り返す場合に特に不足しがちな問題。`MessageToBubbleBottomPaddingConverter`を`IMultiValueConverter`化し、吹き出しの実測幅とメッセージ本文の両方から、テーブル各セルの折り返し行数をCJK/Latin文字幅考慮で推定して補正するよう書き直し。直後に過剰補正(元からあった`BaseRatio`と二重計上)が発覚し`2.4→0.8`に調整、ユーザー確認済み「完璧」。
- **Markdownテーブルの後続本文クリッピング再発**(2026-07-24): 9行程度の大きな表では、セル内折り返し行数の推定が合っていても、MarkdownViewが各行の罫線・セル余白ぶんを少しずつ過小計測し、その累積で表より後の本文が吹き出し下端から見切れるケースをMac実機で再確認。既存の幅ベース折り返し補正は維持しつつ、実テーブル行1行ごとにフォントサイズの0.25倍を追加する小さな行別補正を併用。旧方式のような大きな固定余白には戻さず、行数に比例する不足分だけを補う。

**影響ファイル**: [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml), [client/CopilotChatApp/Views/OrchestratorPage.xaml.cs](client/CopilotChatApp/Views/OrchestratorPage.xaml.cs), [client/CopilotChatApp/Views/HomePage.xaml](client/CopilotChatApp/Views/HomePage.xaml), [client/CopilotChatApp/Views/HomePage.xaml.cs](client/CopilotChatApp/Views/HomePage.xaml.cs), [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs), [client/CopilotChatApp/Converters/BubbleConverters.cs](client/CopilotChatApp/Converters/BubbleConverters.cs), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml), [server/src/protocol.ts](server/src/protocol.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [server/src/sessionMeta.ts](server/src/sessionMeta.ts)

---

## PBI-028: 既存Playerへの追加指示で新しいPlayerがSpawnされてしまうバグ 🟡 ✅

**現状 / 問題点**
- ユーザー報告(2026-07-10): 「オーケストレータでAI自動、または手動でPlayerをSpawnした後、そのPlayerにまた指示を与えると新しいPlayerがSpawnされちゃう」。
- 原因: `spawn_session`ツールは`existingSessionId`を渡せば既存の子(Player)を継続利用できる設計だったが、AIが「自分の子は既に存在するか、その`sessionId`は何か」を知る手段が皆無だった。`list_sessions`はサーバー上の全セッションを返すだけで「自分の子だけ」に絞り込めない。結果、AIは毎回`existingSessionId`を省略し、新規Playerが量産されていた。
- これは以前解決した「AIは自分自身の`sessionId`をどうやって知るか」問題(`getCallerSessionId()`)と構造的に同じ - 会話の記憶に頼らず、専用ツールでIDを発見させるパターンを踏襲して解決。

**対応内容(2026-07-10実装、コミット`30b3a67`)**
- [server/src/peerClient.ts](server/src/peerClient.ts): `listChildrenOnPeer(peer, parentSessionId)`を新設。`spawnOnPeer`と同じ「短命接続で既存プロトコル(`sessions:children`)を再利用」方式でピアの子セッション一覧を取得。
- [server/src/internalControlApi.ts](server/src/internalControlApi.ts): `GET /internal/children?parentSessionId=X`を新設。このサーバー自身の子(`getChildSessionIds`+`getSessionSummary`+`getSessionMeta`)と、設定済み全ピアの子(`listChildrenOnPeer`、`Promise.allSettled`で1台落ちても他は返す)をマージして返す。
- [server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts): 新ツール`list_my_children`を追加(`getCallerSessionId()`で自分のIDを特定し、上記エンドポイントを呼ぶ)。`spawn_session`のツール説明文に「新規作成の前に`list_my_children`で既存の子を確認し、適切な子があれば`existingSessionId`を指定して継続利用すること」を明記。
- テスト: `peerClient.test.ts`に`listChildrenOnPeer`の単体テスト4件追加(成功・空配列・エラー応答・接続エラー)。サーバー側テストスイート122件全パス(新規4件)。
- **実地検証**: メインセッションに`spawn_session`でPlayerを1体作らせた後、続けて「さっき作ったPlayerに追加指示を出して」と依頼 → AIが`list_my_children`→`spawn_session(existingSessionId=<同じPlayer ID>)`の順で呼び出すことをツールコールのトレースで確認。`sessions:children`で子が1体だけ(`turnCount: 2`、両方の指示が同じセッションに乗っている)であることも確認。テストセッションはhard deleteで後片付け済み。

**影響ファイル**: [server/src/peerClient.ts](server/src/peerClient.ts), [server/src/internalControlApi.ts](server/src/internalControlApi.ts), [server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts), [server/tests/peerClient.test.ts](server/tests/peerClient.test.ts)

---

## PBI-029: OrchestratorのMarkdown表示とクロスサーバー処理の診断改善 🟡 ✅

**現状 / 問題点**
- Conductor/Playerペインが通常チャットと異なりプレーンな`Label`表示で、Markdownが描画されなかった。
- クロスサーバーのPlayer実行が遅い場合、ネットワーク・呼び出し元・ピア側CLIのどこで時間を使ったか判別できなかった。
- `getCallerSessionId()`が同じMCPプロセス内でも毎回OSプロセス照会を行っていた。

**対応内容(2026-07-10、コミット`a6f1191`)**
- Conductor/Player本文を`MarkdownView`へ変更し、通常チャットと同じ見出し色・コード表示へ統一。
- Copilot CLIターンとpeer round-tripへ経過時間ログを追加。
- 呼び出し元セッションIDのOS照会結果をMCPプロセス内でメモ化し、`list_my_children`→`spawn_session`での重複照会を削減。
- サーバーテスト124件、Mac Catalystビルド成功。

**影響ファイル**: [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml), [server/src/callerSessionId.ts](server/src/callerSessionId.ts), [server/src/copilotRunner.ts](server/src/copilotRunner.ts), [server/src/peerClient.ts](server/src/peerClient.ts), [server/tests/callerSessionId.test.ts](server/tests/callerSessionId.test.ts)

---

## PBI-030: チャットごとのCopilotモデル選択 🟡 ✅

**現状 / 問題点**
- 使用モデルはサーバー環境の`COPILOT_MODEL`またはCLIデフォルトに固定され、チャット画面からturn単位で変更できなかった。
- 利用可能モデルをハードコードすると、アカウント・時期ごとの差異やモデル追加へ追従できない。

**対応内容(2026-07-21、コミット`8425c8e`)**
- 公式`@github/copilot-sdk`の`listModels()`から、ログイン中アカウントで現在利用可能なモデルを取得する`modelCatalog.ts`を追加。
- WebSocketに`models:list`/`models:list-result`を追加し、クライアントのモデルPickerから以降のturnへ任意のモデルIDを渡せるようにした。
- **Server default**選択時は`COPILOT_MODEL`、未設定ならCLIデフォルトへフォールバック。選択は永続化しない。
- PickerにはSDKが返した表示名だけを表示し、送信時は対応するSDKモデルIDを使用。`auto`もSDKが返した場合だけ選択肢へ含める。
- 選択したモデルは現在のチャットページだけに保持し、`ServerProfile`や`COPILOT_MODEL`は変更しない。
- SDKカタログのcold startを考慮した60秒timeout、明示的な再読込、旧サーバー向けエラー表示を追加。取得失敗時も固定モデル一覧へフォールバックしない。

**受け入れ条件**
- Pickerに表示するモデルは、接続先サーバーのSDKカタログが返したものだけにする。
- **Server default**はクライアント専用の選択肢とし、model overrideを送信しない。
- `auto`はSDKレスポンスに含まれる場合だけ表示し、選択時は`model: "auto"`を送信する。
- モデル選択は現在のチャットページの後続turnだけに適用し、サーバー設定には永続化しない。
- 未選択時は`COPILOT_MODEL`をサーバーデフォルトとして使い、それも未設定ならCLI自身のデフォルトへ委ねる。
- カタログ取得失敗を画面に表示し、ハードコードまたはドキュメント由来のモデル一覧へフォールバックしない。
- カタログ取得に失敗しても**Server default**でチャットを続行でき、チャット画面から再読込できる。
- 既存セッション、画像添付、MCPツール、Orchestrator turnへ回帰を起こさない。

**検証結果(2026-07-21)**
- Microsoft CFS npm registry (`https://packagefeedproxy.microsoft.io/npm/`)を使用して検証。
- サインイン中アカウントから19モデルを取得し、`auto`を含むことを確認。モデルIDはドキュメントから推測できず、例としてMAI-Code-1-Flashは`mai-code-1-flash-picker`として返された。
- 認証付きWebSocketの`models:list`で19モデルを取得。fresh serverでは3.36秒で応答し、一時的なSDK cold-start stallも再読込で回復した。
- モデル関連テスト9件、サーバーテスト126件が成功。TypeScript buildも成功。
- Pickerをチャットcomposerへ移動した後のWindows MAUI buildとRelease packageがerror 0で成功(既存warningのみ)。

**影響ファイル**: [server/src/modelCatalog.ts](server/src/modelCatalog.ts), [server/src/protocol.ts](server/src/protocol.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [server/src/copilotRunner.ts](server/src/copilotRunner.ts), [server/tests/modelCatalog.test.ts](server/tests/modelCatalog.test.ts), [client/CopilotChatApp/MainPage.xaml](client/CopilotChatApp/MainPage.xaml), [client/CopilotChatApp/MainPage.xaml.cs](client/CopilotChatApp/MainPage.xaml.cs), [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs)

---

## PBI-031: Orchestratorが最新の処理へ自動追従しない 🟡 ✅

**現状 / 問題点**
- Conductorのストリーミング更新やPlayerのポーリング更新が増えても、各表示が最新位置へ安定して追従しなかった。
- 常時最下部へ強制すると、ユーザーが古いログを読んでいる最中にも表示が引き戻される。

**対応内容(2026-07-23、コミット`21cf545`)**
- ConductorとPlayer領域を別々に監視し、メッセージ追加・テキスト更新・サイズ変更・処理完了時に最新位置へ追従。
- 最下部から64px以上上へ手動スクロールした場合は自動追従を停止し、最下部へ戻った場合や新規送信時に再開。
- 連続更新時のスクロール要求をまとめ、古い遅延要求が新しい操作を上書きしないversion管理を追加。

**影響ファイル**: [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml), [client/CopilotChatApp/Views/OrchestratorPage.xaml.cs](client/CopilotChatApp/Views/OrchestratorPage.xaml.cs)

---

## PBI-032: iPhone/iPad実機へのReleaseデプロイ手順が長く再利用しづらい 🟢 ✅

**対応内容(2026-07-23、コミット`e47a42c`)**
- [`scripts/deploy-ipad.sh`](scripts/deploy-ipad.sh)を追加。既定はReleaseで、`--debug`/`--clean`/`--run`/`--ipad`/`--iphone`に対応。
- USB接続中の実機UDIDを自動検出し、`Automatic` provisioningでビルド・インストール・起動まで実行。
- `IOS_DEVICE_UDID`/`IOS_CODESIGN_KEY`で個人情報をスクリプトへハードコードせず上書き可能。
- iPad/iPhone実機でReleaseデプロイ確認済み。

**影響ファイル**: [scripts/deploy-ipad.sh](scripts/deploy-ipad.sh)

---

## PBI-033: Playerがいない元Conductorが常にOrchestratorで開かれる 🟢 ✅

**現状 / 問題点**
- 一度Orchestratorとして使ったセッションは、全Playerを削除した後も`orchestratorMain`履歴だけでOrchestrator画面へルーティングされた。

**対応内容(2026-07-23、コミット`5bad2f0`)**
- `OrchestratorMain && IsOrchestratorParent`の場合だけOrchestratorで再オープン。
- Playerが1つも残っていない元Conductorは通常チャットとして開くよう変更。

**影響ファイル**: [client/CopilotChatApp/Views/HomePage.xaml.cs](client/CopilotChatApp/Views/HomePage.xaml.cs)

---

## PBI-034: サーバーの起動ディレクトリによって`.env`が読み込まれない 🔴 ✅

**現状 / 問題点**
- `dotenv.config()`がプロセスの現在ディレクトリを基準にしていたため、LaunchAgent等から別CWDで起動すると`server/.env`を見つけられなかった。

**対応内容(2026-07-23、コミット`c8d58cf`)**
- コンパイル済み`config.js`の位置から`server/.env`を絶対解決し、起動元CWDに依存しないよう修正。
- 別CWDから読み込むテストを追加。

**影響ファイル**: [server/src/config.ts](server/src/config.ts), [server/tests/config.test.ts](server/tests/config.test.ts)

---

## PBI-035: Orchestratorでメッセージ本文を部分選択できない 🟢 ✅

**現状 / 問題点**
- `MarkdownView`上では本文の部分選択ができず、通常チャットにあった選択画面への導線もConductor/Playerペインには無かった。
- Player本文と選択画面のフォントサイズがSettingsと一致していなかった。

**対応内容(2026-07-23、コミット`011b737`)**
- Conductor/Player各メッセージへ**🔎 テキスト選択**を追加し、既存`SelectableTextPage`を再利用。
- 選択画面とPlayerのMarkdown/コード本文を`DynamicResource ChatFontSize`へ統一。

**影響ファイル**: [client/CopilotChatApp/Views/OrchestratorPage.xaml](client/CopilotChatApp/Views/OrchestratorPage.xaml), [client/CopilotChatApp/Views/OrchestratorPage.xaml.cs](client/CopilotChatApp/Views/OrchestratorPage.xaml.cs), [client/CopilotChatApp/Views/SelectableTextPage.xaml](client/CopilotChatApp/Views/SelectableTextPage.xaml)

---

## PBI-036: Macビルド後も常駐サーバーが古いコードを使い続ける 🟡 ✅

**現状 / 問題点**
- `server/dist`をビルドしてもLaunchAgentのNodeプロセスはロード済みJavaScriptを使い続け、新しいプロトコルハンドラ等が反映されなかった。
- `--run`時にLaunchAgentと手動`npm start`が二重起動する可能性があった。
- npm 11がoptional native packageのplatform metadataを`npm ci`中に書き換え、不要なlockfile差分を生成した。

**対応内容(2026-07-24、コミット`e95841c`)**
- `build-mac.sh`を`npm ci`へ変更し、install前後でlockfileを保護してnpm由来のplatform metadata churnを復元。
- サーバービルド成功後、インストール済みLaunchAgentを`launchctl kickstart -k`で自動再起動。
- `--run`時はLaunchAgentがあればそれを利用し、手動サーバーを重複起動しない。
- `brace-expansion`を1.1.16へ更新し、npm auditのhigh 1件を解消。残る4件は最新MCP SDKの上流依存待ち。

**影響ファイル**: [scripts/build-mac.sh](scripts/build-mac.sh), [server/package-lock.json](server/package-lock.json)

---

## PBI-037: 中間メッセージを通常バブルにせずツール行として履歴へ残す 🟡 🚧

**現状 / 問題点**
- Copilot CLIがツール実行前に出す中間ストリーム(読み込んだSkill本文など)が、巨大な通常チャットバブルとして表示される場合がある。ユーザー発言/最終回答と同列に見えて会話構造を誤認しやすい。
- ライブ中の`View`・`edit`等はコンパクトなツール行で表示されるが、CLIの`session-store.db`はユーザー本文と最終アシスタント本文しか保存しないため、チャットを開き直すとツール行が消える。

**対応内容(2026-07-24)**
- ツール開始直前の未確定Assistantストリームは通常バブルとして確定せず削除し、直後のツール行へ集約。
- `runConversationTurn`の共通経路で完了ツールの名前・要約・秘密値マスク済み詳細・成功状態を収集し、turn確定後に`session-meta.json`へturnIndex単位で保存。通常チャット・Orchestrator・session-control経由を共通でカバー。
- `sessions:history`と内部履歴APIで保存済みツール活動を返し、通常チャットとPlayer履歴を「ユーザー発言 → ツール行 → 最終回答」の順で復元。
- 過去のturnにはツールイベント情報が残っていないため遡及復元は不可。実装後に実行したturnから保存される。

**影響ファイル**: [server/src/sessionMeta.ts](server/src/sessionMeta.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [server/src/internalControlApi.ts](server/src/internalControlApi.ts), [server/src/protocol.ts](server/src/protocol.ts), [server/tests/sessionMeta.test.ts](server/tests/sessionMeta.test.ts), [client/CopilotChatApp/Services/ChatClientService.cs](client/CopilotChatApp/Services/ChatClientService.cs), [client/CopilotChatApp/ViewModels/ChatViewModel.cs](client/CopilotChatApp/ViewModels/ChatViewModel.cs), [client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs](client/CopilotChatApp/ViewModels/OrchestratorViewModel.cs)

---

## PBI-038: Copilot CLIのturn runnerをpersistent SDK runtimeへ移行 🔴 📋

**Status:** Planned / large architectural change

**現状 / 問題点**
- 現在はchat turnごとに`copilot -p ... --session-id=<id>`を新規spawnしている。同じsession IDで会話履歴は継続できるが、Node/CLIプロセスとMCP serverは毎turn cold startになる。
- Windows実測ではMCP初期化がturn開始へ約50秒加算されるケースがあり、Macとの差が大きい。ネットワークやモデル応答ではなく、複数MCPの起動・接続が主な待ち時間だった。
- `@github/copilot-sdk`はPBI-030の`listModels()`だけに使っており、chat実行、streaming、tool event、session lifecycleは引き続きCLI標準出力の独自parseに依存している。
- session一覧・履歴・hard deleteはCLI内部の`~/.copilot/session-store.db`を直接読む/更新している。内部schemaへの密結合なので、SDKが提供するsession APIと責務が重複している。
- MCP管理も`copilot mcp list/add/remove`の短命CLIをspawnしており、chat sessionへ実際に何をloadするかがCLIのglobal configと自動検出に依存する。

**目標アーキテクチャ**
- サーバープロセス内で`CopilotClient`を長寿命で1つ管理し、`createSession()`/`resumeSession()`した`CopilotSession`へ複数turnを送る。CLI runtimeとMCPをturnごとに再起動しない。
- `assistant.message_delta`、`assistant.message`、`tool.execution_start`/complete、`session.idle`等のtyped SDK eventを既存WebSocket protocolへ変換し、クライアントUIは段階移行中も変更せず使えるようにする。
- SDK既定の`mode: "copilot-cli"`と`~/.copilot`を使い、既存CLI sessionを同じstoreからlist/resumeできる互換性を維持する。新しい専用storeへ無断移行しない。
- sessionごとの`workingDirectory`、model、attachments、permission policy、MCP構成を明示し、global CLI状態への暗黙依存を減らす。
- idle sessionはdisk上の履歴を残して`disconnect()`でき、再利用時に`resumeSession()`する。サーバー終了時は`stop()`、timeout時は`forceStop()`でruntimeを確実に片付ける。

**重要な設計課題**
- `callerSessionId.ts`は「session-control MCPの親が毎turnの`copilot --session-id=<id>`プロセスである」ことを前提にしている。persistent runtimeではこの前提が壊れるため、session-controlをSDK custom toolへ移すか、改ざん不能なsession contextを明示注入する必要がある。
- 現在のUIではモデルをchat pageの後続turn単位で変更できるが、SDKのmodelはsession create/resume configで指定する。実行中sessionでのmodel変更方法と、未対応時の安全なresume手順をspikeで確定する。
- SDK eventと現行CLI JSON eventの順序・payloadは完全同一ではない。PBI-037の「中間Assistantを通常バブルにしない」「完了tool activityをturnIndexへ永続化」を維持する変換層が必要。
- SDK APIだけで取得できないturn本文・turn countがある場合は、SQLite直読を一度に削除しない。SDKをsession lifecycleのSSoTとし、履歴read modelは互換adapterとして段階的に縮小する。
- 同じsessionをCLI runnerとSDK runnerから同時実行すると、二重turnやstore/checkpoint競合が起こり得る。runnerはサーバー起動中に固定し、session単位の排他制御も入れる。

**段階移行**
1. **Compatibility spike**: disposable sessionでSDK create/resume、既存CLI sessionのlist/resume、SQLiteへの継続保存、cwd、model切替、attachments、streaming、tool event、abortをWindows/Mac双方で実測する。
2. **Runner abstraction**: 現在の`runCopilotTurn`契約を`cli`/`sdk`実装へ分離し、`COPILOT_RUNNER=cli|sdk`をサーバー起動時だけ評価する。既定は検証完了まで`cli`とし、設定変更+再起動で即rollback可能にする。
3. **Persistent SDK runtime**: singleton `CopilotClient`、session cache、同一sessionのturn queue/lock、idle disconnect、graceful/forced shutdownを実装する。SDK eventを既存delta/tool/final callbackへ変換する。
4. **Session compatibility**: CLIで作成済みの実sessionをSDKで再開し、Home一覧、履歴、soft/hard delete、CWD containment、`session-meta.json`のparent/archived/cross-session/tool activityを保持する。
5. **Deterministic MCP**: 必須の`session-control`と選択されたMCPだけをsession configへ明示する。config discoveryの有無、global CLI MCPとのmerge優先順位、OAuth/token保存方針を固定し、Windows/Macで同じ構成を再現できるようにする。
6. **Orchestrator migration**: local/peerのConductor・Player、既存Playerへの追加turn、cross-session attributionをSDK runnerで検証する。`callerSessionId`のOS process探索を廃止する。
7. **Rollout**: cold/warm turn latency、runtime/MCP起動回数、memory、失敗率をCLI baselineと比較する。SDKをdefaultにした後も1 releaseはCLI rollback pathを残し、安定後に短命CLI runnerと不要なSQLite writeを削除する。

**受け入れ条件**
- 2回目以降の同一session turnでCopilot runtimeとMCPが再spawnされず、WindowsのMCP cold-start待ちが毎turn発生しない。
- PBI-030のモデル一覧SSoTとchat page単位のモデル選択、画像添付、streaming、tool表示、cancel/timeoutが回帰しない。
- 既存CLI sessionをSDKで再開して会話を継続でき、既存履歴を失わない。CLI/SDKが同じsessionへ同時書き込みしない。
- 通常chat、Orchestrator、cross-server Playerの全経路が同じrunner abstractionを通り、parent-child関係とcross-session表示を保持する。
- MCP構成がsession作成時に予測可能で、秘密値をlog/UIへ出さず、permission policyが現在の許可範囲と同等以上に厳密である。
- SDK runtime crash、server restart、network切断後にsessionをresumeできる。終了時にruntime/MCPの孤児processを残さない。
- `COPILOT_RUNNER=cli`へ戻して再起動すれば、データ変換やsession損失なしで旧runnerへrollbackできる。
- server全test、Windows build/package、Mac Catalyst build、iPhone/iPad実機の主要chat flowが成功する。

**計測項目**
- server起動からSDK readyまで、session create/resumeまで、first deltaまで、turn完了までの時間。
- cold/warm別のMCP起動回数と各MCP ready時間。
- active/idle session数に対するNode/CLI/MCP process数とmemory使用量。
- CLI baselineとSDK版のturn成功率、timeout/abort、resume失敗、tool event欠落数。

**想定影響ファイル**: [server/src/copilotRunner.ts](server/src/copilotRunner.ts), [server/src/wsServer.ts](server/src/wsServer.ts), [server/src/internalControlApi.ts](server/src/internalControlApi.ts), [server/src/sessionHistory.ts](server/src/sessionHistory.ts), [server/src/sessionMeta.ts](server/src/sessionMeta.ts), [server/src/sessionControlMcpServer.ts](server/src/sessionControlMcpServer.ts), [server/src/callerSessionId.ts](server/src/callerSessionId.ts), [server/src/mcpManager.ts](server/src/mcpManager.ts), [server/src/modelCatalog.ts](server/src/modelCatalog.ts), [server/src/config.ts](server/src/config.ts), [server/tests](server/tests)

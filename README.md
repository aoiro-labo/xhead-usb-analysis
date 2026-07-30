# XHEAD-USB 解析プロジェクト

マイコンソフト製の超小型USB接続OFDM変調器「[XHEAD-USB](https://www.micomsoft.co.jp/xhead-usb.html)」を対象に、以下を行うプロジェクトである。

- 公式アプリ「XHEAD-STUDIO」の挙動・通信プロトコルの解析
- 公式アプリより自由度の高い設定を行える独自送出ツールの開発
- 実機の解析結果・手法のドキュメント化

## ステータス

### 現在地

- `mnservice.exe`非依存の直接USB制御で全8 ModeのRF出力・停止を実機確認済み
- DVB_T2は実験搬送波まで確認済み。規格準拠信号と16個の固有フィールドのレジスタ対応は未解決
- 独自GUIは、公式サービス経由のフル機能と直接USB経由の変調・RF設定を切り替え可能
- 次の主要課題は、実TSを適正ビットレートで直接USB送出し、変調器へ取り込まれるか確認すること

<details>
<summary>項目別の詳細ステータスを開く</summary>

| 項目 | 状態 | 詳細 |
|---|---|---|
| アーキテクチャ解析（GUI⇔サービス、gRPC構成） | 完了 | [docs/architecture.md](docs/architecture.md) |
| 隠しDebugモード (`EnableDebugMode`) の発見・有効化 | 完了 | [docs/architecture.md](docs/architecture.md)・[docs/gui_debug_mode_comparison.md](docs/gui_debug_mode_comparison.md) |
| USBドライバ不具合の原因診断・修正 | 完了 | [docs/architecture.md](docs/architecture.md) §5 |
| gRPCプロトコルの完全再構成（`.proto`、DLL非依存で再実装可能） | 完了 | [docs/protocol/README.md](docs/protocol/README.md) |
| 実機からの変調パラメータ確定（FieldID・許容値） | 完了 | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md) |
| 独自送出ツール: 読み取り（プロパティツリーのダンプ） | 完了 | `tools/custom_sender` |
| 独自送出ツール: 書き込み（`CmdChannelStart`経由でのSet） | 完了 | `ChannelOpen`→`ProgramAdd/Commit`→**`ChannelStart`(Source構築前・全プロパティ群込み)**→`Source`構築→`ProgramApply`→`SourceStart`という正しいアーキテクチャを特定。値の変更（Constellation・RF電力のPAGain/DACGain）が実際に物理層まで反映されることもネイティブログで実証済み。GUI(`--gui`)も追加し、CLI/GUI両方から送出可能 |
| 実ソース添付（動画ファイル`SourceUrl`／デスクトップキャプチャ`SourceCapture`） | 完了 | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md)「続報10」— STUDIOの基本動作（ファイル/画面を選んで送出）に相当。CLI(`--sourceurl`)・GUI(ソース選択ラジオボタン)の両方に統合、RTL-SDRでRF出力も実証済み。「STUDIOでできることは自分のツールでもできるように」という方針の最初の成果 |
| ソース種別: カラーバー/サイントーン自己完結生成（`SourceTranscode`） | 部分的成功・GUI統合済み（要注意） | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md)「続報8・18」— 外部ファイル・キャプチャ不要な自己完結テスト信号ソースを発見。正しいフォーマット(H264/MP1_L2、Rawは拒否される)を指定すればRF出力まで到達（RTL-SDRで+34〜35dB実測）。`SourceOpen`は`mnservice.exe`側の内部例外により常に`Unknown`エラーを返す（クライアント側の問題ではないと確認済み）。**続報18で追加確認**: この例外の後`mnservice.exe`のgRPCサービス全体が無応答になることがある（DTMB/J83Cと同種の未対応コードパスのバグ）。GUI(`tools/custom_sender --colorbar`)には警告表示・トラブル時は`mnservice.exe`再起動が必要という注記を追加済み |
| 出力時のBML付与（データ放送）・字幕再注入 | 部分的成功・GUI統合済み | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md)「続報9・11・16」— `mPSEncodeParam.BMLFile`(FieldID=38)は固定パスのローカル`.xbml`ファイルを指す方式と判明。TSDuckで実TSファイルを解析したところ、通常の`SourceUrl`経路では**字幕（ARIB PID 0x0114等）・データ放送カルーセルが構造的に落ちている**ことを確認、`BMLFile`経由でTSDuckで抽出した実字幕データの再注入を試行し、ネイティブ側の`mMTSBMLFile`クラス初期化まで到達（cdbで確認済み）。GUIの「メディア/コーデック」タブにファイル選択ダイアログ付きで統合済み。ビットレベルでの多重化確認・PTS同期は未着手 |
| EPGの複数番組対応 | 調査完了・制約確認・GUI統合済み | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md)「続報11・16」— `mEPGSimpleParam`の全6フィールドをダンプし、Title/Descriptor/Type/EventIDが1件のみでスケジュールモードに従い繰り返し配信される構造だと確認。プロパティツリー上に「複数番組」に対応する別グループは存在せず、STUDIO・本ツールとも現状では複数番組EPGの直接設定手段なし。1件分の設定はGUIの「EPG」タブから可能 |
| メディア/コーデック設定（Video/Audio PID・解像度・ビットレート等） | **完了（続報21で拡張）** | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md)「続報16・21」— `mPSEncodeParam`(39フィールド)の基本15フィールドをGUIの「メディア/コーデック」タブに追加。**続報21**: STUDIO本体のGUIを実際に一通り操作したところ、「STUDIOのコーデック設定タブは空」という続報16の前提が誤りだったと判明（訂正済み）——映像信号/フィールドオーダー/カラープライマリー/変換特性/マトリクス係数/GOP最小・最大フレーム数/Bピクチャ数/シーンチェンジ検出/TwoPass/映像レート/ビットレート最低・最高値/画質レベル/デバッグ機能の14フィールドを新規GUIタブ「詳細コーデック」として追加、さらにPCR_PID/PMT_PID（従来未設定だった）も「チャンネル/番組情報」タブに追加。全フィールドの`ChannelStart`受理をライブ確認済み |
| ISDB-T以外のMode切替（DVB_T等、STUDIOにない機能） | **全8 Modeで直接RF出力・停止を確認。DVB_T2は実験段階** | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md)「続報12・13・22・24・25」— DVB_T/J83A/ATSC/J83B/DTMB/ISDB_T/J83Cは`direct_usb`から送出可能。DVB_T2は`FECBlockNums`既定値0がビットレートを0にする不整合を修正すると、サービス内の別レイヤーがMode 7を明示拒否すると判明。一方、`direct_usb`の最小Mode 7列は2回連続でRF出力（最大+43.0/+35.8dB）と停止に成功し、実機も健全。ただしDVB-T2固有フィールドの生レジスタ対応と規格準拠は未確認。**アンテナ接続での非ISDB-T送出は行わないこと** |
| チャンネル/番組メタデータ（サービス名・NetworkID等）の変更 | **完了** | [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md)「続報14」— GUIタブ実装後、`mMTSChannelParam`/`mMTSProgramParam`を明示的に上書きすると`ChannelStart`が`mnservice.exe`をハングさせる問題を発見し一時撤去。原因調査でXHEAD-STUDIO自身も同じ設定値で同様にハングすることを確認、プロトコル/フィールドの問題ではなく**長時間の検証作業によるUSB接続の劣化**と判明——実機を物理的に抜き差ししたところ即座に解消し、STUDIO・本ツールとも正常に送出できるようになった。GUI機能を復活済み（`tools/custom_sender`「チャンネル/番組情報」タブ）。DTMB/J83Cのハング（続報13）は抜き差し後も再現し、こちらは本物のモード固有バグと確認 |
| RTL-SDRループバックでの実信号検証 | 完了 | [tools/rtlsdr_analysis](tools/rtlsdr_analysis) — 送出前後で470〜476MHz帯（6MHz幅、ISDB-Tの帯域幅と一致）に約38dBのパワー上昇を実測、送出停止で消失することも確認。設定した中心周波数473MHzとも一致 |
| `mnservice.exe`ネイティブ側の生USBプロトコル解析 | 検証中 | [tools/usb_capture](tools/usb_capture) — バルク転送(24064バイト=MPEG-TS 188バイト×128、224スライスのリングバッファ)の生TSフレーミングを確認。コントロール転送は`mhal_modulation.cc`が使う「アドレス設定→データ読み書き」の汎用レジスタバスと判明。**ISDB-T変調パラメータ（Frequency/Bandwidth/Constellation/FFT/CodeRate/GuardInterval/TimeInterleavce）のレジスタアドレスをほぼ完全にマップ化**、DACGainも確定。フルライフサイクルキャプチャで新たに`0x0020`台（デバイス識別情報）を発見、`0x0629`はリングバッファ占有量ステータスの可能性が高いと判明。`0x1280`〜`0x1283`は`mazo::mbroadcast::mCalibration`という専用クラスによるRF較正データ読み出し機構と判明（**PAGainは直接レジスタに書かれず、この較正データと突き合わせて使われるソフトウェア側パラメータらしいと判明**）。バルク転送ヘッダは未解読 |
| `mnservice.exe`を介さない直接制御（DLL/サービス完全非依存） | **全8 ModeでRF出力・停止を確認、GUI統合済み** | [tools/direct_usb](tools/direct_usb) — WinUSBで実機に直接接続し、解読したレジスタバスで読み書き・開始・停止を実証。DVB_T/J83A/ATSC/J83B/DTMB/ISDB_T/J83CはMode別の確認済み列で送出可能。DVB_T2も最小共通列と`0x0680=7`で2回連続して実験RF出力に成功したが、固有16フィールドのレジスタ対応と規格準拠は未確認。GUIの直接USBバックエンドは確認済み7 Modeを選択可能で、実験段階のDVB_T2のみCLI限定。直接バックエンド用の実TSバルク送信APIも追加済み。 |

</details>

## クイックリンク

- **解析ドキュメント**
  - [docs/architecture.md](docs/architecture.md) — 全体アーキテクチャ、EnableDebugModeの発見、USBドライバ問題、検討した代替アプローチ
  - [docs/protocol/README.md](docs/protocol/README.md) — gRPCプロトコルの完全リファレンス（`.proto`群 + 解説）
  - [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md) — 実機検証済みの変調パラメータ一覧、Set経路の調査結果
  - [docs/gui_debug_mode_comparison.md](docs/gui_debug_mode_comparison.md) — 通常時/Debug有効時のGUIスクリーンショット比較
- **ツール**
  - [tools/custom_sender](tools/custom_sender) — 独自送出ツール（C#、CLI/GUI両対応。`mnservice.exe`経由・GUIの「直接USB」トグルで`mnservice.exe`非経由のどちらでも送出可能）
  - [tools/direct_usb](tools/direct_usb) — `mnservice.exe`を介さずWinUSBで実機に直接読み書きする単体診断ツール（C#、`tools/custom_sender`の直接USBバックエンドの元になったロジック）
  - [tools/native_analysis](tools/native_analysis) — Ghidra/cdbによる`mnservice.exe`動的解析スクリプト・手順
  - [tools/usb_capture](tools/usb_capture) — USBプロトコル解析メモ
  - [tools/rtlsdr_analysis](tools/rtlsdr_analysis) — RTL-SDRループバック検証メモ

## 主要な発見

XHEAD-STUDIOは **GUI (`xhead_studio.exe`)** と **バックグラウンドサービス (`service\mnservice.exe`)** の2プロセス構成で、両者は `localhost:50051` の **gRPC** で通信する。実機とのUSB通信は `mnservice.exe`（ネイティブバイナリ）が担当する。

最大の発見は、GUI側に **`EnableDebugMode` という隠しフラグ** が存在し、これを有効にすると変調パラメータ（Constellation / CodeRate / GuardInterval / FFT / TimeInterleave 等）をはじめ、映像・音声・コーデックの詳細設定が公式GUI上に解放される点である。このフラグは設定ファイル保存時には書き出されない仕様だが、設定ファイルを直接編集すれば有効化できる。

<p align="center">
  <img src="docs/screenshots/normal/01_出力設定_変調設定.png" width="45%" alt="通常時の変調設定タブ">
  <img src="docs/screenshots/debug/01_出力設定_変調設定.png" width="45%" alt="Debug有効時の変調設定タブ">
  <br><sub>左: 通常時／右: EnableDebugMode有効時（変調設定タブ）</sub>
</p>

さらに、GUI⇔サービス間の通信は固定メッセージではなく汎用的なプロパティツリー方式であり、公式GUIが一切参照していない設定項目がサービス側に存在する。実機から読み出した変調パラメータの完全なFieldID一覧は [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md) にまとめてあり、8つのModeそれぞれに固有のサブ構造体が存在する。公式サービス経由では`DVB_T`/`ATSC`/`J83B`が成功し、`J83A`はサービス側の設定不能な検証値、`DTMB`/`J83C`はサービスのハング、`DVB_T2`は規格パラメータ検証で止まる。一方、サービスを迂回する`direct_usb`ではDVB_T2以外の7モードを送出できる。`tools/custom_sender`は公式サービス経由のフル機能と、この直接USB経路の双方を提供する独自クライアントである。

送出（Set）経路は当初`CmdProgramApply`が謎の`bad status`エラーで止まっていたが、Ghidra・cdbによるネイティブ動的解析の末に真因（エンコーダオブジェクトの未初期化）を特定し、さらに`CmdChannelStart`はSourceが一切存在しない段階で一度だけ呼ぶ「変調器・エンコーダの電源投入」操作であり、`CmdProgramApply`／`CmdSourceStart`はその後で「稼働中のパイプラインに実ソースを繋ぐ」別の後段ステップである、という公式アプリの実際のアーキテクチャを特定した。この順序と必須プロパティ群を揃えたところ送出パイプライン全体が動作した（詳細は [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md) の「続報3・4」）。

## ディレクトリ構成

```
docs/                          解析ドキュメント
  architecture.md               XHEAD-STUDIOの全体アーキテクチャ、隠しDebugモードの発見など
  gui_debug_mode_comparison.md  通常時/Debug有効時のGUIスクリーンショット比較
  protocol/                     gRPCプロトコルのリファレンス実装非依存な再構成 (.proto + 解説)
    modulation_capabilities.md   実機で確認した変調パラメータの実際の姿(FieldID等)
  screenshots/                  上記ドキュメントで使用するスクリーンショット
tools/
  custom_sender/        独自送出ツール (C#, CLI/GUI。mnServiceDotNet.dll経由でmnservice.exeに接続 or GUIの
                         「直接USB」トグルでmnservice.exe非経由のWinUSB直接送出も可能)
  direct_usb/            mnservice.exeを介さずWinUSBで実機に直接読み書きする単体診断ツール (C#)
  native_analysis/       Ghidra/cdbによるmnservice.exe動的解析スクリプト・手順
  usb_capture/           USBプロトコル解析用スクリプト・メモ (USBPcap/Wireshark)
  rtlsdr_analysis/       RTL-SDRループバックによる実信号検証スクリプト
captures/              実機キャプチャデータ（大容量のためリポジトリには含めない。.gitignore参照）
decompiled/            公式アプリのデコンパイル結果（著作権上リポジトリには含めない。ローカル専用）
```

## 検証環境

- XHEAD-USB 実機 + PC(Windows)をUSB接続
- XHEAD-USBのRF出力(同軸)を、SMA変換コネクタ経由でRTL-SDRの入力にループバック接続
  （電波を実際に空中線から放射せず、有線ループバックで送出信号を安全に受信・解析する構成）

## 独自送出ツール (tools/custom_sender)

C#製。公式インストール済みの `mnClientDotNet.dll` を参照して `mnservice.exe` (localhost:50051) に直接 gRPC接続する。**vendor DLLはリポジトリに含めず**、ローカルの `C:\Program Files\Micomsoft\XHEAD-STUDIO` を参照する前提。CLIとGUIの両方を同じ実行ファイルで提供する。

```
cd tools/custom_sender
dotnet build
# XHEAD-STUDIO (xhead_studio.exe) を一度起動してサービスを立ち上げた状態で:
dotnet run                              # CLI: 疎通確認・プロパティツリーダンプ・フルパイプラインテスト(デスクトップキャプチャ送出)
dotnet run -- --sourceurl [ファイルパス]  # CLI: 動画/TSファイルを指定して送出
./bin/Debug/net472/XHeadSender.exe --gui   # GUI: 接続方式(mnservice.exe経由/直接USB)・変調パラメータ+RF電力・
                                            # チャンネル情報・ソースを自由に設定して送出/停止
                                            # (net472はネイティブexeなので直接実行する。`dotnet <exe>`は
                                            # hostpolicy.dllが無いというエラーで失敗するので使わないこと)
```

直接USB CLIはTS入力を3通り選べる。内蔵ファイル送信はTSDuck不要、UDP入力は任意の
TSDuckパイプラインと接続でき、`--tsduck-file`は本ツール自身がインストール済みの
`TSDuck tsp`を子プロセスとして起動する便利モードである。

```powershell
# 内蔵送信（外部依存なし）
XHeadSender.exe --directtest --mode 5 --ts-file input.ts --bitrate 20000000

# 本ツールがTSDuckを任意利用（既定UDP port 1234）
XHeadSender.exe --directtest --mode 5 --tsduck-file input.ts --bitrate 20000000

# TSDuck側で任意の入力・加工を構成し、本ツールへplain UDP TSを渡す
XHeadSender.exe --directtest --mode 5 --udp-port 1234
tsp -I file --infinite input.ts -P regulate --bitrate 20000000 -O ip --packet-burst 7 127.0.0.1:1234
```

UDP入力は安全のためlocalhostのみで待ち受け、現時点では188-byte TSのplain UDP専用
（RTP/RS204は非対応）。受信データは同期バイトを検証し、128 TS packet単位のUSBスライスへ
組み直す。なおUSB bulk OUTを継続消費させるデバイス側リング制御は引き続き解析中であり、
この入力層の完成と「映像を含む連続RF送出」の完成は区別する必要がある。

GUI（`MainForm.cs`）はタブ構成（ソース／チャンネル・番組情報／EPG／メディア・コーデック／
詳細コーデック／変調・RF電力設定）+ 接続→送出開始→停止→切断のボタン操作を基本としている。冒頭の
「接続方式」トグルで**mnservice.exe経由**（既定、全機能）と**直接USB**（`mnservice.exe`
不要、[tools/direct_usb](tools/direct_usb)と同じWinUSB直接ロジックを`DirectUsbSession.cs`
として統合したもの、変調パラメータ+RF電力設定のみ対応）を切り替えられる。

- **変調・RF電力設定タブ**: 周波数・Constellation・Bandwidth・FFT・CodeRate・
  GuardInterval・TimeInterleavce・RF電力(Level/PAGain/DACGain)を自由に設定でき、
  `ChannelStart`単体で変調器を実際にRF駆動できる。「直接USB」バックエンド選択時のみ、
  「Mode」コンボ（DVB_T/J83A/ATSC/J83B/DTMB/ISDB_T/J83C、直接USBで確認済みの7値）でモード切替も可能
  （[続報19・20](docs/protocol/modulation_capabilities.md)）——選択したModeに応じて
  Constellationの選択肢や有効なフィールドが自動的に切り替わる。
- **チャンネル・番組情報タブ**（mnservice.exe経由のみ）: サービス名・ネットワーク名・TS名・
  地域識別・放送事業者ID・リモコン番号・サービス番号・コピー制御・PCR PID・PMT PID
  （`mMTSChannelParam`/`mMTSProgramParam`、[続報14・21](docs/protocol/modulation_capabilities.md)）。
- **EPGタブ**（mnservice.exe経由のみ）: モード・配信間隔・イベントID・ジャンル・タイトル・
  番組内容（`mEPGSimpleParam`、[続報11・16](docs/protocol/modulation_capabilities.md)。
  1件のみ・繰り返し配信という制約はハードウェア側の仕様）。
- **メディア・コーデックタブ**（mnservice.exe経由のみ）: エンコード速度・Video/Audio PID・
  レイテンシ・解像度・アスペクト比・フレームレート・音声チャンネル/サンプルレート/
  ビットレート・レート制御方式・GOP長・BMLファイル（`.xbml`選択ダイアログ付き、
  [続報9・11・16](docs/protocol/modulation_capabilities.md)）。
- **詳細コーデックタブ**（mnservice.exe経由のみ、[続報21](docs/protocol/modulation_capabilities.md)
  で新規追加）: 映像信号・フィールドオーダー・カラープライマリー・変換特性・マトリクス係数・
  GOP最小/最大フレーム数・GOP内Bピクチャ最大数・シーンチェンジ検出・TwoPass・映像レート・
  ビットレート最低/最高値・画質レベル・デバッグ機能——STUDIO本体のGUIを実際に操作して
  発見した、従来「STUDIOのコーデック設定タブは空」と誤って記録されていたフィールド群。
- **ソースタブ**（mnservice.exe経由のみ）: RFのみ／デスクトップキャプチャ（実際の画面を送出）／
  動画ファイル指定（`.ts`等を選んで送出）——STUDIO本体の基本動作（ファイル/画面を選んで送出
  開始）に相当する機能をGUI・CLI（`--sourceurl`）両方で実現している。

CLIには他にモード切替（`--dvbt`/`--atsc`/`--j83b`等、[続報12・13](docs/protocol/modulation_capabilities.md)参照——`--dtmb`/`--j83c`は`mnservice.exe`をハングさせるため非推奨）、
カラーバー自己完結生成（`--colorbar`、[続報8](docs/protocol/modulation_capabilities.md)参照、
クライアント側に既知の未解決バグあり・GUI未統合）、EPG/メディア設定の切り分けテスト
（`--epgencode`）、直接USBバックエンド単体検証（`--directtest`）などの診断用フラグがある。

## 免責・注意事項

- 本プロジェクトは、購入済み実機の相互運用性向上・自由度拡張を目的とした解析であり、第三者のシステムへの攻撃等は目的としていない。
- 電波法上、実際に電波を空中線から発射する場合は、出力・帯域外輻射等の技術基準を満たす必要がある。本プロジェクトの検証は基本的にRTL-SDRへの同軸ループバックで行っており、実際の運用（アンテナ接続・電波発射）を行う場合は自己責任で関連法令を確認すること。

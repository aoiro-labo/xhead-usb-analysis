# XHEAD-USB / XHEAD-STUDIO アーキテクチャ解析メモ

対象: マイコンソフト「XHEAD-USB」(超小型 USB接続 地デジOFDM変調器) と付属アプリ「XHEAD-STUDIO」。
解析日: 2026-07-24。解析方法: 公式アプリ(.NET)のデコンパイル(ilspycmd)、設定ファイル調査、実機のPnP情報確認。

## 1. ハードウェア

- VID:PID = `17A7:0008`
- Windows上のドライバクラスは **WinUSB**（`§5`参照——解析当初はlibusbKに誤って差し替わっていたことが判明し、WinUSBへ復元・固定済み）
- 111×42×28mm, 約100g。USBバスパワー + RF出力(同軸)。

## 2. ソフトウェア構成（インストール先: `C:\Program Files\Micomsoft\XHEAD-STUDIO`）

XHEAD-STUDIO は **GUIプロセス** と **バックグラウンドサービス** の2プロセス構成で、両者は localhost 上の **gRPC** で通信する。

```
xhead_studio.exe (GUI, .NET/WinForms, C#)
        │  gRPC (Grpc.Core, insecure, localhost:50051)
        │  service: msBroadcastService
        ▼
service\mnservice.exe (バックグラウンドサービス, ネイティブC++)
        │  - FFmpeg (avcodec/avformat/avfilter/swscale/swresample) で映像/音声デコード
        │  - Pegasys TMPGEnc SDK (service\pegasys\*.vme) でエンコード
        │  - libusbK 経由でXHEAD-USB実機にTS/制御コマンドを送出
        ▼
XHEAD-USB (実機) --USB--> OFDM変調 --RF(同軸)--> (今回はRTL-SDRへループバック)
```

- GUI: `xhead_studio.exe`
  - `mnGUIDotNet.dll` … 汎用UIフレームワーク (mnFramework.GUI)
  - `mnClientDotNet.dll` … gRPCクライアント本体 (mnFramework, mnFramework.grpc)
  - 起動時に `localhost:50051` へ接続 (`xhead_usb/xHeadApp.cs`: `SERVICE_IP = "localhost:50051"`)
- サービス: `service\mnservice.exe`
  - ネイティブバイナリ。libprotobuf.dll / abseil_dll.dll / grpc関連を使用し、gRPCサーバとして待受け。
  - `service\pegasys\` 以下はPegasys社TMPGEncエンコーダSDKのコンポーネント(映像/音声コーデック、フィルタ)。
  - 実機とのUSB通信（生プロトコル）はこのネイティブサービス内に実装されている（未解析、要Ghidra/IDA等でのバイナリ解析）。

## 3. 重要な発見: 隠しDebugモード

`xhead_usb.config.xSystemParam` に `EnableDebugMode` というbool値が存在し、GUIの各設定画面 (`uiModulation.cs`, `uiMedia.cs`, `uiEPG.cs`, `uiCodec.cs`, `uiChannel.cs`) はこのフラグを見て表示項目を切り替えている。

```csharp
public bool EnableDebugMode { get; set; }   // xSystemParam.cs:75, デフォルト false
```

さらに `xHeadConfig.cs` の `IgnorePropertiesResolver` により、`EnableDebugMode` と `EnableBML` は **設定ファイル保存時には書き出されない**（GUIから恒久的にONにする手段がない = 意図的に隠された開発者/デバッグ用フラグ）。

しかし読み込み処理 (`loadConfig`) は `ContractResolver` を使わずに全プロパティをそのまま `Newtonsoft.Json` でデシリアライズするため、**設定ファイルを直接編集して `EnableDebugMode: true` を追記すれば、次回起動時にDebugモードが有効化される**ことを確認済み（コード上の裏付け。GUI上での実地確認は別途要検証）。

### 設定ファイルの場所

```
%APPDATA%\Micomsoft\XHeadUSB\XHeadUSB_<Windowsユーザー名>.xcfg   (JSON形式)
```

### Debugモードで解放される項目（コードから判明した範囲）

`xModulationParam.createDebugModulation()` (xModulationParam.cs) より、変調パラメータがフル解放される:

| フィールド | 型 | Simple/Advanceでの可否 | Debugでの可否 |
|---|---|---|---|
| Channel (RFチャンネル) | xRFChannel | ○ | ○ |
| PowerLevel | byte (80-100) | ○ | ○ |
| Constellation (変調多値数) | xConstellation (QAM_64等) | × | ○ |
| CodeRate (畳み込み符号化率) | xCodeRate: 1/2, 2/3, 3/4, 5/6, 7/8 | × | ○ |
| GuardInterval | xGuardInterval: 1/32, 1/16, 1/8, 1/4 | × | ○ |
| FFT (キャリア数モード) | xFFT: 2K/4K/8K 等 | × | ○ |
| TimeInterleave | xTimeInterleave: Disable, Mode1-3 | × | ○ |

同様に `uiMedia.cs` / `uiCodec.cs` / `uiChannel.cs` / `uiEPG.cs` もDebugモードで制限が緩和される。
実機スクリーンショットによる比較・確認結果は [docs/gui_debug_mode_comparison.md](gui_debug_mode_comparison.md)
にまとめてある（実際に増えたのは PCR/PMT PID・Video/Audio PID・EPG Event ID・BMLタブで、
コーデック設定・システム設定タブは見た目上の差分なしと判明）。

`EnableBML` は BML (ARIB Broadcast Markup Language、データ放送)ファイルのインポート機能 (`uiBML.cs`, `formBML.cs`) に関連。データ放送コンテンツを持たせられる可能性がある。

## 4. gRPCプロトコルの完全な再構成 (docs/protocol/)

`decompiled/mnClientDotNet/mnFramework.grpc/Ms*Reflection.cs` の各ファイルには、Micomsoftの元
`.proto` を `protoc` がコンパイルした際の **`FileDescriptorProto` そのもの** がbase64で埋め込まれて
いることが判明した。これをPythonの `google.protobuf` で直接デコードすることで、フィールド名・
フィールド番号・型・oneof・enum値をIL推測ではなく **バイト単位で正確に** 復元できた。

結果は [`docs/protocol/`](protocol/) 以下に、DLL不要で再実装可能なレベルの `.proto` 群
(`docs/protocol/proto/`) と解説 (`docs/protocol/README.md`) としてまとめてある。要点:

- 設定値は固定メッセージではなく、`msProperty` (`msDescriptor`=形, `msPropertyParam`=値) による
  **汎用プロパティツリー**として表現される。`msDescriptor` は実質的に `mnservice.exe` 内部の
  ネイティブC構造体のランタイム反映（`Offset`/`Size`/`Tag` を持つ）で、GUIのコード上に見える
  `mModulationParam.Mode.ISDB_T.Constellation` のようなドット区切りパスは、実際にはネストした
  `msDescriptor` グループとして配線されている。
- **クライアントはコンパイル時に「変調パラメータ」の型を一切持つ必要がない**。`connectService`
  で返る `msClient.Outputs` 等を辿れば、実行時にフィールド名・ID・型・許容値レンジ
  (`msPropertyRange`) をすべて発見でき、公式GUIのSimple/Advance/Debugいずれのモードにも
  出てこないフィールドも含めて到達可能（詳細は `docs/protocol/README.md` §5）。
- **重要: RFパラメータの範囲はワイヤレベルで強制されていない。** `msPropertyRange` の
  Min/Max/選択肢はサーバーが「公開している」メタデータに過ぎず、プロトコル自体には
  範囲外の値の送信を防ぐ仕組みがない。`mnservice.exe`側で実際にバリデーションしているかは
  このクライアント側スキーマだけでは検証不能（ネイティブ解析が必要）。XHEAD-USBはUHF帯の
  RF送信機であるため、自作ツールでこの層を扱う際は既定で公式GUIと同じ安全な範囲に収め、
  範囲外の値を送る場合は利用者の明示的な意思確認を挟むこと。
- ファームウェア書き換えも同じローカルgRPC面 (`sendControl` + `msControlParam` +
  `msFirmwareFile`/`msFWUsbConfig`) に載っており、アクセス制御は接続時に自己申告する
  `msPrivilege` のみ（暗号的な認証は無し。localhost限定であることが前提の設計と見られる）。
- **オブジェクトのスコープに2種類ある**（実機テストで確認, 2026-07-25）。`msClient.Outputs`/
  `Engines`/`Captures`はハードウェア/システムリソースとしてどの接続からも同じ内容で見える
  グローバルなものである一方、`msClient.Channels`/`Sources`は**接続（クライアント）ごとに
  プライベート**であり、別のgRPC接続から`connectService`しても他クライアントが開いた
  Channel/Sourceは一切見えない（空リストが返る）。

## 5. USBドライバ問題（発見・解決済み）

2026-07-24、実機接続時に公式アプリが `XHEAD-USBの接続に失敗しました` エラーを表示する事象を確認。
原因はUSBドライバが標準の **WinUSB** ではなく **libusbK** (v3.1.0.0, `oem122.inf`) になっていたこと
（`C:\sdrsharp-x86\zadig.exe` でRTL-SDR用ドライバを導入した際に誤って変更してしまったと推測）。
デバイスマネージャーでドライバを削除しUSB再接続することでWinUSBへ自動的に再バインドされ、
公式アプリ・自作ツール (`tools/custom_sender`) ともに `mnservice.exe` への接続に成功することを確認。

`mnservice.exe`はTCP `50051`(gRPC)以外のポートは待受けていないことを確認済み（`Get-NetTCPConnection`）。
XHEAD-2にはBML設定用の内蔵Webサーバー（`XHEAD-2_BML_WEB.pdf`参照。放送設定/EPG設定/データ放送設定/
ネットワーク設定/著作権保護設定などのページを持つ本格的な管理画面）があるが、これはXHEAD-2がLAN/Wi-Fi
接続を持つ据置機であるためと考えられ、PC直結ドングルのXHEAD-USBには同等のWeb UIは存在しない
（gRPCが唯一の制御面）。

なお、WinUSB復元後に確認した限りでは、XHEAD-USBはUSBマスストレージクラスとしては列挙されない
（後述のXHEAD-2向けBMLマニュアルにあるような `UPDATE`/`data`/`www` フォルダを持つ仮想ドライブ
機能は見当たらない）。BML(データ放送)機能はXHEAD-USBでは gRPC経由・GUIの `EnableBML` フラグ
配下 (`uiBML.cs`) に統合されており、XHEAD-2の「USBドライブにtsファイルをドラッグ&ドロップ」
方式とは別の実装に置き換わっていると考えられる。

## 6. 検討した代替アプローチ（却下 or 保留）

- **TSDuckの `vatek`/`hides` 出力プラグインで直接OFDM変調できないか**: XHEAD-USB内部の変調チップが
  VATek系またはHiDes系のOEMチップであれば、TSDuckから直接ISDB-T変調を叩けないか検討。
  `tsvatek -a` / `tshides` とも実機接続状態で **0台検出**（2026-07-24確認）。Micomsoft独自VID
  (`17A7:0008`) のため、これらのツールが想定する既知チップのVID/PIDパターンには一致しない。
  内部チップが本当にVATek/HiDes系かどうかまでは否定できないが、少なくとも「挿すだけで動く」
  ショートカットではない。
- **ffmpegを弄って直接RF出力できないか**: `mnservice.exe`が使うffmpegは、映像/音声入力をTS化する
  **エンコード段**（`msSourceParam`のTranscode/Resampleモードに相当）であり、OFDM変調(RF出力)は
  別レイヤ（未解析のUSB生プロトコル）が担っていると考えられる。ffmpeg層をいじっても変調そのものは
  バイパスできない。ただし「任意のTSコンテンツを送出したい」という目的自体は、既存の
  Source/Channel経路（`CmdSourceOpen`+`msSourceParam`、config中の`MediaFiles`）で既に達成可能。

## 7. 未解析・要調査

- [x] `mnservice.exe` 本体のネイティブ解析（USB生プロトコル） → Ghidra（静的解析）+ cdb
      （ライブブレークポイント）で広範に実施。「アドレス設定(0x4A)→読み書き(0x4E/0x4F)」の
      汎用レジスタバスプロトコルを解読し、ISDB-T変調パラメータのレジスタアドレスをほぼ完全に
      マップ化（[tools/usb_capture](../tools/usb_capture)、[tools/native_analysis](../tools/native_analysis)）。
- [x] USB通信をUSBPcapで実キャプチャ → WinUSB経由で完了。バルク転送(24064バイト=MPEG-TS
      188バイト×128、224スライスのリングバッファ)の生TSフレーミング、コントロール転送の
      周期的なレジスタ読み出しパターンを確認。当初はフロー制御通知と解釈していたが、
      後の全呼び出し元解析で`0x4A`（アドレス設定）→`0x4E`（8バイト読み出し）という
      汎用レジスタバスだと訂正済み（[tools/usb_capture](../tools/usb_capture)）。
- [x] gRPC reflection の有効性確認 → reflectionではなく `Ms*Reflection.cs` 埋め込みの
      `FileDescriptorProto` から確定情報を取得済み（`docs/protocol/`）
- [x] Debugモード解放の実機確認 → ドライバ修正後、GUIに「BML」タブと「デバッグ機能を
      有効にする」トグルなどが実際に出現することを確認済み（2026-07-24, ユーザーのスクリーン
      ショットで確認）
- [x] `tools/custom_sender` から実際に `msClient.Outputs`/`Properties` を列挙し、変調パラメータの
      実際の `FieldID`/`msPropertyRange` を確定 → `docs/protocol/modulation_capabilities.md` に
      まとめた。ISDB_T(Constellation=19, Bandwidth=20, FFT=21, CodeRate=22, GuardInterval=23,
      TimeInterleavce=24)に加え、`Mode`セレクタの選択肢としてDVB_T/J83A/ATSC/J83B/DTMB/J83C/
      DVB_T2の完全なサブ構造体まで存在することが判明（変調チップは多規格対応の可能性が高い。
      ただし実際にISDB-T以外へ切替可能・安全かは別問題。同ファイルの注意事項を参照）
- [x] RTL-SDRループバックでの実信号検証（設定値と実際のRF出力の対応関係） →
      [tools/rtlsdr_analysis](../tools/rtlsdr_analysis)で完了。ISDB_T・DVB_T・ATSC・J83Bの
      各モードでRF電力上昇（+33〜47dB）を実測、Bandwidth変更によるスペクトラム形状の変化も確認。
      `tools/direct_usb`（`mnservice.exe`非依存の直接USB経路）でも別途実証済み。
- [x] Set経路の調査 → `CmdApplyConfig`は未実装(`unhandled command`)。正解は
      `CmdChannelStart`にPropertiesを同梱する方式（`docs/protocol/modulation_capabilities.md`
      「Set経路の調査結果」参照）。ただしSource/Contentを繋がずにStartを呼ぶと
      `mnservice.exe`がクラッシュすることを確認済み（実機には無害）。
- [x] Source(内蔵Colorbar等)を正しくアタッチした上での`CmdChannelStart`成功ケースの実装 →
      完了。デスクトップキャプチャ(`SourceCapture`)・動画ファイル(`SourceUrl`)・自己完結型
      テスト信号(`SourceTranscode`、カラーバー/サイントーン)の3方式全てで実証済み
      （`docs/protocol/modulation_capabilities.md`「続報4・8・10」）。`tools/custom_sender`の
      CLI・GUI両方から利用可能。

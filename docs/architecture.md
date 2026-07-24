# XHEAD-USB / XHEAD-STUDIO アーキテクチャ解析メモ

対象: マイコンソフト「XHEAD-USB」(超小型 USB接続 地デジOFDM変調器) と付属アプリ「XHEAD-STUDIO」。
解析日: 2026-07-24。解析方法: 公式アプリ(.NET)のデコンパイル(ilspycmd)、設定ファイル調査、実機のPnP情報確認。

## 1. ハードウェア

- VID:PID = `17A7:0008`
- Windows上のドライバクラスは **libusbk devices**（純正ベンダードライバではなく libusbK 系ドライバで認識されている状態。Zadig等で差し替え済みの可能性が高い。要確認）
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

同様に `uiMedia.cs` / `uiCodec.cs` / `uiChannel.cs` / `uiEPG.cs` もDebugモードで解像度・フレームレート・音声チャンネル/ビットレート・コーデック詳細・EPGなどの制限が緩和される（要個別確認）。

`EnableBML` は BML (ARIB Broadcast Markup Language、データ放送)ファイルのインポート機能 (`uiBML.cs`, `formBML.cs`) に関連。データ放送コンテンツを持たせられる可能性がある。

## 4. gRPCプロパティシステム

設定値は固定メッセージではなく、`mnDescriptor` / `mnField` / `msProperty*` による**汎用プロパティツリー**として表現され、ドット区切りのパス文字列（例: `mModulationParam.Mode.ISDB_T.Constellation`）でGUIとサービス間をやり取りしている（`xHeadConfig.cs` の `MODULATION_FREQ` 等の定数群、`getChnnelProperty()` を参照）。

この設計は、**GUIが参照していないパスがサービス側に存在する可能性**を示唆する。サービスが公開する全プロパティツリーを列挙できれば、公式アプリのどのUIモードにも出てこない設定（真の意味で自由度が最も高い層）を発見できる可能性がある。次のステップ候補:

- gRPC reflection (もし有効なら `grpcurl` で列挙可能)
- `mnClientDotNet.dll` を直接参照した自作クライアントで `msDescriptor` を辿るツリー探索

## 5. 未解析・要調査

- [ ] `mnservice.exe` 本体のネイティブ解析（USB生プロトコル、Ghidra/IDA向き）
- [ ] libusbK 経由でのUSB通信を Wireshark + USBPcap で実キャプチャ
- [ ] gRPC reflection の有効性確認（`grpcurl -plaintext localhost:50051 list`）
- [ ] Debugモード解放の実機確認（GUI上でタブ/項目が増えるか）
- [ ] RTL-SDRループバックでの実信号検証（設定値と実際のRF出力の対応関係）

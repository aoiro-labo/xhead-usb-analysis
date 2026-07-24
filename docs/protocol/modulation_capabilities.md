# 実機で確認した変調パラメータの実際の姿 (2026-07-24)

`tools/custom_sender` から `mnservice.exe` (standalone起動、実機WinUSB接続済み) に対して
`CmdChannelOpen` を発行し、返ってきた `msChannel.Properties` を再帰的にダンプして得られた
**実測データ**。`docs/protocol/README.md` の worked example にあったプレースホルダ値を
実値で置き換える。

XHEAD-USBの出力オブジェクト: `ObjectType=ObjectOutputModulation`, `Path` はWinUSBデバイス
インターフェースパスと一致（`\\?\usb#vid_17a7&pid_0008#<serial>#{dee824ef-729b-4a0e-9c14-b7117d33a817}`）。

## mModulationParam の構造

`mModulationParam` (struct size=72 bytes) はトップレベルに3つのフィールドのみを持つ:

| Field | FieldID | 型 | 範囲/既定値 |
|---|---|---|---|
| `Frequency` | 0 | uint | **0 〜 1,000,000 (kHz)、既定 473000** — GUIのチャンネルプルダウンとは違い、プロトコル上は任意の周波数を直接指定できる |
| `DacCtrl` (group) | 4 | struct | `IFMode`(FieldID=1: Disable/IF_FREQ/IQ_OFFSET/IF_INV_FREQ), `IFFreq`(FieldID=2, uint32full), `GAIN`(FieldID=3, uint32full, hex) — GUIのどのモードにも出てこないRF DAC/IF直接制御 |
| `Mode` | 42 | 選択式(FieldConstSelect) | 現在値 `5 = ISDB_T`。**選択肢自体は8規格ぶんある**（下表）。各選択肢は専用の `msDescriptor`(サブ構造体)を伴う |

### `Mode` が持つ8つの選択肢と、それぞれのサブ構造体

`FieldConstSelect` という型名の通り、実運用でこの値を変更できるかは未検証（ファームウェア/
アナログRFフロントエンドの対応範囲による可能性が高い。**下記はあくまで変調チップ/ファーム
ウェアが「知っている」構造であって、XHEAD-USBのアンテナ回路や認証がISDB-T以外の規格の
送出を安全・合法に行える保証は一切ない**。詳細は本ファイル末尾の注意事項を参照）。

| Mode | 主なフィールド |
|---|---|
| `DVB_T` | Constellation(QPSK/QAM16/QAM64), Bandwidth, FFT(2k/4k/8k), CodeRate(1/2〜7/8), GuardInterval(1/32〜1/4) |
| `J83A` (欧州系ケーブルQAM) | Constellation(QAM16/32/64/128/256) |
| `ATSC` | Constellation(8VSB固定) |
| `J83B` (北米ケーブルQAM) | Constellation(QAM64/256) |
| `DTMB` (中国地デジ) | Constellation, Bandwidth, CodeRate(0.4/0.6/0.8), Carrier(3780/1), Frame(420/945/595), Interleave(240/720) |
| **`ISDB_T`** | 下表参照 |
| `J83C` (日本ケーブルQAM) | Constellation(QAM64/256) |
| `DVB_T2` | Version, Bandwidth, Function(拡張キャリア/コンステレーション回転/HEM/Null Packet Deletion), L1Constellation, PLPConstellation, FFT(1k〜32k), CodeRate(1/2〜2/5), GuardInterval(1/32〜19/256), PilotPattern(PP1-8), FEC(16200/64800), NetworkID(既定0x3085=12421), SystemID(既定0x8001=32769), FECBlockNums, SysmbolNums, TINumber, ISSYLength |

### `ISDB_T` サブ構造体（公式Debugモードで見えていたものと完全一致）

| Field | FieldID | 選択肢 | 既定値 |
|---|---|---|---|
| `Constellation` | 19 | `0=DQPSK, 1=QPSK, 2=QAM16, 3=QAM64` | 3 (QAM64) |
| `Bandwidth` | 20 | uint 0-10 (MHz) | 6 |
| `FFT` | 21 | `1=_8k, 2=_4k, 0=_2k` | 1 (_8k) |
| `CodeRate` | 22 | `0=CR_1_2, 1=CR_2_3, 2=CR_3_4, 3=CR_5_6, 4=CR_7_8` | 3 (CR_5_6) |
| `GuardInterval` | 23 | `0=GI_1_32, 1=GI_1_16, 2=GI_1_8, 3=GI_1_4` | 1 (GI_1_16) |
| `TimeInterleavce` | 24 | `1=Mode1, 2=Mode2, 3=Mode3` | 3 (Mode3) |

これはGUIの `xModulationParam.createDebugModulation()` が公開する5フィールドと完全一致する
（`docs/architecture.md` §3参照）。つまり **EnableDebugModeで見えていたものが、プロトコル上の
全容そのもの** であり、GUIはこの構造体をそのまま表示しているだけだったことが実測で裏付けられた。

## その他の確認済みプロパティ（`CmdChannelOpen`直後の既定チャンネル）

- `mMTSChannelParam`: `Spec` (`FieldConstSelect`, 既定 `ARIB_STD_B10`)。ここも
  `ISO_13818_1`/`ETSI_300486`/`ARIB_STD_B10`/`ABNT_NBR15603`/`ATSC_A65_PSIP` の5規格ぶんの
  サブ構造体を持つ多規格設計。
- `mPSRFPowerAdjust`: `Level`(0-100), `PAGain`(int8, -128〜127), `DACGain`(int8, -128〜127) —
  GUIの単純な「PowerLevel」スライダーの裏には、PA/DACそれぞれの生ゲイン調整値がある。
- `mPSEncodeParam`: 映像解像度(1080Pまで)/フレームレート(60fpsまで)/色域(BT.709等)/音声
  (32-48kHz, 128-384kbps)/GOP・レート制御など、Codec Debugタブで見えていたものとほぼ一致。
  `DebugFile` の既定値が `pegasys_out.ts` になっている点は興味深い（エンコーダ内部のデバッグ
  ダンプ機能と思われる）。
- `mEPGSimpleParam`: EPGモード（`AribSchedule_8Days`等）、ジャンルコード等。

## Set経路の調査結果 (2026-07-24)

変調パラメータを実際に変更するための正しい手順を実機検証した。

### 試した経路と結果

1. **`CmdApplyConfig`** (単独の`sendRequest`) → `UNAVAILABLE: unhandled command : [5]`。
   このサーバービルドでは未実装。`docs/protocol/README.md`のworked exampleが想定していた
   経路は実際には機能しない。
2. **`CmdChannelOpen`にPropertiesを同梱** → `UNAVAILABLE: unknown property : [mModulationParam]`。
   Open時点でのProperties検証はOUTPUTオブジェクト基準（`msClient.Outputs[].Properties`は
   常に空）であり、チャンネル固有のプロパティ名はまだ「知らない」状態のため拒否される。
3. **`CmdChannelStart`にPropertiesを同梱**（正解） → デコンパイル済みGUIコード
   (`xTaskStartChannel.cs` → `xHeadConfig.applyChannel()` → `mnClient.Channel.startChannel(channel, props)`)
   を追ったところ、変調・チャンネル・コーデック・EPGのプロパティは**`CmdChannelStart`にこそ
   同梱される**設計と判明。`CmdChannelOpen`(素の名前のみ) → `CmdProgramAdd` → `CmdProgramCommit`
   まではResultSuccessで進行することを確認。

### 既知のクラッシュ再現条件（重要・要注意）

`CmdSourceOpen`等で実際の映像/音声ソースを一切アタッチしないまま`CmdChannelStart`を呼ぶと、
**`mnservice.exe`がネイティブ側で異常終了する**ことを確認した(2026-07-24, ログ:
`captures/mnservice_stdout2.log`, ローカル専用)。`CmdChannelOpen`→`CmdProgramAdd`→
`CmdProgramCommit`まではすべて`ResultSuccess`で応答が返るが、その直後の`CmdChannelStart`で
gRPC接続そのものが切断され(`Stream removed`)、プロセスが完全に終了する。

- 実機・USBデバイス自体への実害は無いことを確認済み（クラッシュ後にサービスを再起動すれば
  即座に実機を再検出し正常応答する）。ソフトウェアだけがクラッシュする。
- 原因はおそらく、`xTaskStartChannel.cs`が本来 `createSource()` → `createContent()` →
  `applyContent()` を経てチャンネルにコンテンツをアタッチしてから`startChannel()`を呼ぶ設計で
  あるところ、このテストではSource/Content工程を省略してChannelStartだけを呼んだため、
  ネイティブ側がnullな参照にアクセスした（未処理例外/アクセス違反）と推測される。
- **今後Startを試す際は、必ず先にSource(例: Colorbarテストパターン等の内蔵信号源)を
  `CmdSourceOpen`→`CmdSourceStart`し、`CmdProgramAdd`後に`msMediaContent`経由でstreamを
  結びつけてから`CmdChannelStart`を呼ぶこと。**

### Source接続時の追加調査 (2026-07-25)

`ChannelOpen → ProgramAdd → ProgramCommit → SourceOpen(Mode=SourceUrl)` まではクラッシュせず
`ResultSuccess`で進行することを確認した。ただし以下の理由で `ProgramApply`/`ChannelStart` まで
到達できていない。

- `CmdSourceOpen` の応答は即座に返るが、`Status=StatusPrepare`・`Content.Programs=0`の空状態。
  実際のファイル解析（Media Foundationによるコーデック/フォーマット検出、
  `mff_function.cc`の警告ログが大量に出る）は非同期に進み、46MBのTSファイルで**約9秒**かかった
  （ネイティブログ: `mnsource.cc:243] source[...] status changed : [3]`）。
- `subscribeService`のイベントストリームを実装し (`EventWatcher`クラス, `tools/custom_sender`)、
  `EventSourceStatus`イベントが届くことは確認したが、**このイベントは`msStatus`の値のみを運び、
  更新された`Content`(Programs/Streams)は一切含まない**（デコンパイル済み
  `mnClient.handleSource()`でも`item.Status`をラッパーに反映するだけで完結しており、
  Content再取得の経路が無いことをコードレベルでも確認）。
- 専用の「Source再取得」RPCは存在しない。`CmdSourceApply`は逆に「呼び出し側が既に把握している
  Streamを送り込む」ためのコマンドであり、探索には使えない。
- 現時点での仮説: 実際のGUIは`CmdSourceOpen`の応答が返った時点で同期的にContentが埋まっている
  ケース（高速なソース）を前提にしているか、あるいは本解析では気づけていない別の待ち合わせ
  手段が存在する。ネイティブ側 (`mnsource.cc`) の解析が必要。

この調査中はクラッシュを一切再現しておらず、実機・サービスとも健全な状態を維持できている。

## 取得方法（再現手順）

```
# xhead_studio.exe を終了した状態で、mnservice.exe を単体起動
cd "C:\Program Files\Micomsoft\XHEAD-STUDIO\service"
.\mnservice.exe

# 別ターミナルで
cd tools/custom_sender
dotnet run
```

`Program.cs` の `DumpProperty`/`DumpDescriptor`/`DumpRange` が `msDescriptor.Fields` と
`msPropertyRange.RangeValues[].StructDesc` を再帰的に辿って全体を出力する。

## 重要な注意事項

- **これは実機ファームウェアが内部的に持つ変調チップの能力表であり、Mode切り替えが実際に
  安全・合法に動作することを意味しない。** XHEAD-USBは日本の電波法に基づく型式（ISDB-T,
  UHF帯）を前提に設計・（おそらく）認証されている製品。Mode/Bandwidth/周波数レンジを
  ISDB-T以外や通常想定外の値に変更して実際にアンテナから電波を送出することは、規制外・
  スプリアス超過等の法令違反になり得る。本プロジェクトの検証は同軸ループバック
  (RTL-SDR)に限定し、アンテナ接続での送出はISDB-T・適切な範囲内でのみ行うこと。
- `Frequency`/`Mode`等をプロトコルレベルで変更できることと、実際にRFフロントエンド
  （フィルタ・PA・アンテナ整合）が対応範囲外の帯域/規格で正常に（＝規格内の綺麗な信号で）
  動作することは別問題。特に`DacCtrl`のIFFreq/GAINやDVB-T2のような広範なパラメータ群は、
  値を送ること自体はプロトコル上可能でも、ハードウェア的な裏付けが無ければ意味のある出力には
  ならない、あるいは機器を損傷させる可能性も否定できない。変更を試す場合は必ずループバック
  環境で確認し、値は保守的に。

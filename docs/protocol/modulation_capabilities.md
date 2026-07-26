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

**追加実験（別クライアント接続からの覗き見）**: `msClient.Sources`/`Channels`はクライアント
（gRPC接続）ごとにプライベートで、他クライアントからは見えないことを確認した。
既存のController接続とは別に、`PrivilegeDebug`（非排他）で第二の接続を新規に張って
`connectService`しても、その応答の`Client.Sources`は常に`0`件だった（Source自体は
最初に開いた接続の中でのみ有効な、セッションスコープのオブジェクトである）。したがって
「別の接続から覗いてContentの更新を確認する」というアプローチは原理的に成立しないと判明した。
これは`msClient.Outputs`/`Engines`/`Captures`が（ハードウェア/システムリソースとして）
どの接続からも同じ内容で見えるのとは対照的で、Source/ChannelとOutput/Engine/Captureの
スコープの違いを裏付ける追加証拠でもある。

以上により、「非同期プローブ完了後にContentを取得する手段」は本解析（gRPCクライアントからの
黒箱テスト）だけでは特定できず、`mnservice.exe`のネイティブコード(`mnsource.cc`)を読む必要が
あるという結論に達した。3種類のアプローチ（イベントのSourceケース待受け、イベントのStatus
ケース検知後の長時間待機、別クライアント接続からの覗き見）はいずれもクラッシュを引き起こさず、
実機・サービスは全過程を通じて健全な状態を維持した。

### 続報 (2026-07-25): EventSourceStatusは実はContentを運んでいた

上記の「イベントはStatusのみでContentを運ばない」という結論は**誤りだった**。原因はデコンパイル
済みクライアント側の型読み違えにある。

- `msEvent.Status` フィールドの型は生の `msStatus` ではなく **`msEventStatus`というラッパー
  メッセージ**であり、`Status`(msStatus)に加えて **`Content`(msContent)フィールドを oneofで
  持つ**。
- 公式GUIの `mnClient.handleSource()` は `item.Status.Status` だけを読んで
  `mnSource.updateStatus()` に渡し、`item.Status.Content` を一切参照していない（ラッパー
  クラス`mnSource`側にContent更新用のコードパスが無いため、結果的に握りつぶされる）。この
  ラッパーの実装だけを読んで「イベントはContentを運ばない」と判断したのが誤りの原因。
- 実際に生の `ev.Status.Content` を読むよう自作ツールを修正したところ、**`EventSourceStatus`
  （Source用）・`EventCaptureStatus`（Capture用）とも、Ready到達時に実際にProgram/Streamの
  完全な情報を運んでいる**ことを確認した。

これにより、Source接続時の「Content取得問題」は解決した。正しい待受け方法は`subscribeService`
のイベントストリームで対象HandleIDの`EventXxxStatus`を待ち、届いた`msEvent.Status.Content`
（`ev.Status`, `msEventStatus`型）を直接読むこと。別クライアント接続からの覗き見（Captureのみ
機能する）は不要になった。

### Capture経由のSource接続は成功、ChannelStartへの接続がまだ未解決

デスクトップキャプチャ（`Dxgidesktop`, RAW_RGB 1920x1080@60fps）を使い、以下の手順まで
`ResultSuccess`で到達することを確認した:

```
CmdChannelOpen → CmdProgramAdd → CmdProgramCommit
→ CmdCaptureOpen → (Readyまで待機) → CmdCaptureStart
→ CmdSourceOpen(Mode=SourceCapture, 実際に判明したCapture Program/Streamを参照)
→ (EventSourceStatusでStatusReady + Content取得を確認)
```

しかし直後の **`CmdProgramApply`が一貫して`FAILED_PRECONDITION: bad status`で失敗**する。
試して除外した仮説:

- Source/CaptureのProgramID・StreamIndexの不一致 → 一致していることを確認済み、無関係
- `CmdProgramCommit`にProgram側のPropertiesを渡し忘れている → `CmdProgramAdd`の応答は
  そもそも`Properties`が0件で、渡すものが無い
- 順序が逆（`ChannelStart`を先に呼ぶべき） → 試したところ、Source未接続のまま
  `ChannelStart`を呼ぶ形になり**再現性を持ってクラッシュ**した（`Stream removed`で
  mnservice.exeプロセスごと終了。実機には無害、再起動で復帰）

`mnservice.exe`本体から文字列抽出したところ、`bad status`という応答は`mnbridge.cc`内の汎用
コマンドディスパッチャが返す複数の類似メッセージの一つで（`unhandled command`,
`bad status : [%d]`, `object bad status : [%08x]`等と同じ関数群に隣接）、直前には
`service broadcast bad status  already start.` という文字列がある（サービス全体で一つの
放送状態を管理していることを示唆）。近傍には `program already connect` /
`program[%d] already commit` / `program not committed` / `channel already connected` /
`channel not connected` という一連の状態文字列があり、`CmdProgramApply`が要求する前提条件
（チャンネル/プログラムの「connected」「committed」状態）をまだ正しく満たせていないと推測
されるが、具体的にどの条件かは文字列抽出だけでは特定できず、**この先はGhidra/IDA等による
ディスアセンブルが必要**という結論に達した。

自作ツール (`tools/custom_sender/Program.cs`) には、この一連の調査で実装した以下の再利用可能な
基盤が残っている: `EventWatcher`(購読イベントの背景処理・Content付きStatus待受け)、
`PeekCaptureViaSecondaryConnection`(共有オブジェクトの別接続からの参照)、Capture経由Sourceの
完全なオープン手順。次回はここから`CmdProgramApply`前提条件の特定（またはネイティブ解析）を
再開できる。

### 続報2 (2026-07-25): 静的解析による前提条件特定は誤りだった【訂正】

当初、`pefile` + `capstone`による静的解析（`.rdata`内の`bad status`文字列へのRVA参照を
`.text`全体から線形disasmで検索）で `0x14002580b: cmp dword ptr [rbp+0x58], 3` という分岐を
発見し、これが`CmdProgramApply`の前提条件だと結論づけていた。

**これは誤りだった。** Ghidra（後述）で当該関数（`FUN_1400257b0`）をデコンパイルしたところ、
この関数は実際には**`CmdConnect`のハンドラ**であることが判明した。呼び出し元
`FUN_14002c660`が`"D:\mn-next\mnframework\components\service\app\src\mnclient.cc"`という
デバッグ文字列や`msClientParam::vftable`を参照しており、`msClient`応答
（Outputs/Engines等を含む）を構築する処理そのものだった。`[rbp+0x58]==3`という同じ形の
チェックがたまたま複数箇所に存在し、別のコマンドのものを掴んでいたことになる。

このセクションの以降の記述（`msMediaContent.Param`の設定試行など）は前提が誤っていたため撤回し、
下記「続報3」に置き換える。静的な文字列grepだけで「同じ定数と比較している分岐」を見つけても、
それが目的のコマンドのものとは限らない、という教訓が得られた。実際の呼び出し元を確定するには
動的解析（ライブブレークポイント）か、最低でも呼び出し階層を遡るコールグラフ解析が必要。

### 続報3 (2026-07-25): 動的解析でProgramApplyの真の前提条件を特定

Ghidra 12.1.2（headlessモード、`analyzeHeadless.bat`でインポート・自動解析、以降は
`-process -noanalysis -postScript`で高速に再利用）と`cdb.exe`（WinDbg付属のコンソール
デバッガ）を導入し、動的解析に切り替えた。

**cdbの罠**: `cdb.exe -g -G -cf <cmdfile> mnservice.exe`という起動方法（`-g`=初回ブレーク無視、
`-G`=終了時ブレーク無視、`-cf`=起動時コマンドファイル）を最初に試したが、何度やっても
コマンドファイルの内容が一切実行された形跡がなかった（`.logopen`のログファイルすら
作成されない）。`cdb -?`のヘルプを読み直したところ、`-cf`は**「最初のデバッガプロンプトで」**
実行される仕様であり、`-g`は**まさにその最初のプロンプト（プロセス生成時の初期ブレーク）を
スキップする**フラグだった。つまり`-g`と`-cf`は根本的に両立しない組み合わせで、
`-g`を外した瞬間に狙い通り動いた。以前の調査メモにあった「メインイメージのモジュール名が
`image00007ff6...`という謎の名前で読み込まれる」という観察は実は無関係な副次的事象で
（`lm`で確認すると内部的には正しく`mnservice`として登録されていた）、真因はこの`-g`の誤用
だった。

**実際に効いた起動法**:
```
cdb.exe -cf C:\Users\aoiro\cdb_cmds.txt "C:\Program Files\Micomsoft\XHEAD-STUDIO\service\mnservice.exe"
```
コマンドファイル（ASCII推奨。日本語パスを`.logopen`に渡すと解析失敗して全コマンドが
無視される事例があったため、ログ出力先はASCIIパスにする）:
```
.logopen /t C:\Users\aoiro\cdb_session.log
bu mnservice+0x36ed79 "kb 15; g"
g
```
`0x36ed79`は`absl::lts_20240116::FailedPreconditionError`（全ての`FailedPreconditionError`系
ステータス生成箇所、約121箇所から共通で呼ばれる関数）のオフセット。Ghidraのシンボル検索で
特定した。

**ログタイミング診断（cdb不要、まず先にこれで判明した重要な副産物）**: `bad status`ログ直後に
現れる`source not connected` / `object not attached` / `channel not connected`の3行が、
`ProgramApply`自体の内部失敗の一部なのか、それとも失敗後にクライアントが呼ぶ
`SourceClose`/`ChannelClose`の副作用なのかを切り分けるため、失敗検出後のクリーンアップ呼び出し
前に`Thread.Sleep(3000)`を仕込んで再実行した。結果、`bad status`から3.002秒後（=注入した
sleepと一致）に3行が出現することを確認し、**この3行は完全に無関係（自分自身のClose呼び出しが
未接続オブジェクトに対して出す想定内の警告）と判明した**。以前「channel not connected」を
手がかりに「ChannelStartが先に必要では」と推測していたが、これは誤りだったことになる。

**実際の呼び出し連鎖（cdbで実測）**: `FailedPreconditionError`にブレークを張ったまま
`XHeadSender.exe`を実行し、ネイティブログの`bad status`行の直前に記録された`kb 15`スタック
トレースを読むと、以下の呼び出し連鎖が判明した（`mnservice+0x28f3a`から先は他の失敗時にも
現れる汎用ディスパッチ経由なので割愛）:

```
FUN_140096ce0 (Channelの Program リストを検索し、見つかったProgramの仮想メソッド[vtbl+0x10]を呼ぶ)
  -> FUN_14008c4b0 (Program::Apply相当。Program+0xf0とProgram+0xf8の2つの関連オブジェクトを
                     それぞれ status==3 で条件チェック)
       -> FUN_14009a130 (`*(param_1+0x58) == 3` を最終チェック。不一致なら
                          `absl::FailedPreconditionError("bad status")` を返す ← ここが震源地)
```

`FUN_14009a130`をピンポイントに再ブレークし（`mnservice+0x9a130`）、関数エントリでの
`rcx`（第1引数=チェック対象オブジェクト）をダンプ、さらにvtableポインタから手動でMSVC RTTIを
辿った（`vtbl-8`→CompleteObjectLocator→`+0xc`のRVA→TypeDescriptor→`+0x10`のマングル名）:

```
--- HIT rcx=00000241dd284f20 vtbl=00007ff6eef9c878 status58=0 ---
".?AVmPSEncoder@micomsoft@mazo@@"   ; = class mazo::micomsoft::mPSEncoder
```

つまりチェック対象は**Source でも Channel でもなく、`mPSEncoder`という名のエンコーダ
オブジェクトそのもの**であり、そのステータスが`0`（未初期化）のまま`3`(Ready)に一度も
遷移していない、というのが`bad status`の真の原因だった。この1関数だけは1回のProgramApply
試行で正確に1回しかヒットしない（`FailedPreconditionError`本体は起動〜終了までに100回以上
ヒットする内部的なステータス生成の共通処理なので、狙った箇所を直接ブレークする方が
はるかにノイズが少ない）。

**検証して否定した仮説**:
- **SourceのStatusタイミング説**: `CmdSourceStart`を`CmdProgramApply`より先に呼ぶ順序に
  変更して再テストした。Sourceは`StatusReady`(3)を経て`StatusRunning`(4)まで確実に進んだが、
  `ProgramApply`は全く同じ`bad status`で失敗した。この時点で`FUN_14009a130`がチェックしている
  のはSourceのステータスではないと確定した。なお`xTaskStartChannel.cs`（decompiled）を確認した
  ところ、公式アプリは`applyContent()`を`source_.startSource()`より**厳密に先に**呼んでおり
  （`ProgramApply` → `SourceStart`の順）、今回の実装は元々この順序で正しかった。SourceStart
  を先出しする変更は撤回し、公式の順序に戻した。
- **Engine選択ミス説**: `msClient.Engines`には`microsoft_d3d11va`（Media Foundation経由）と
  `nvidia_cuvid`（NVENC/NVDEC）の2つが存在し、これまで`Engines[0]`（前者）を無条件で選んで
  いた。RTX 5070 Tiを積む実機なら後者の方が実用的なエンコーダだろうと考え、名前に
  `nvidia`/`cuvid`を含むものを優先選択するよう変更して再テストしたが、**どちらのEngineを
  選んでも`bad status`は完全に同一だった**。少なくともこの2エンジンのどちらも、選んだだけでは
  `mPSEncoder`が初期化されないことになる。
- **`CmdEngineApply`(=60)を直接呼ぶ説**: decompileした`mnClientDotNet`のプロトコル定義
  （`msServiceCmd.cs`）には`CmdEngineApply = 60`という、GUIコード側からは一度も参照されて
  いないコマンドが存在する。`msRequest`のoneofに専用のEngineパラメータ枠はないため、
  `ProgramApply`と同じ`Content`（`msMediaContent`）を流用する形で試しに送信したところ、
  **`mnservice.exe`がクラッシュした**（`Stream removed`でプロセス終了）。デバイス・サービスは
  再起動で問題なく復帰することを確認済みだが、この呼び方は成立しない。少なくともこの
  ペイロード形状では使えないコマンドである。

### 続報4 (2026-07-25): ブレークスルー — ProgramApply成功、パイプライン全体が動いた

`FUN_14008c4b0`のもう一つのチェック（`*(Program+0xf0 Obj)+0x140 == 3`）を同じRTTI手動解決手法で
追ったところ、対象オブジェクトは**`mazo::micomsoft::mPegasysChannel`**（＝Pegasys SDKベースの
チャンネル・エンコードパイプライン管理オブジェクトそのもの）で、これも`Status==0`のまま
だった。つまり`mPSEncoder`だけでなく、その一段上の`mPegasysChannel`ごと未初期化ということ。

これを手がかりに、decompiled GUIを`ChannelOpen`/`ProgramApply`回りだけでなく**アプリ起動時の
初期化コード**（`xTaskCreateChannel.cs`）まで遡って読み直したところ、根本的な誤解が見つかった:
**公式アプリは`CmdChannelStart`を、Sourceが一つも存在しない段階で、デバイス検出時に一度だけ
呼んでいる。** `xTaskCreateChannel.processTask()`の流れは`createChannel()`（Output由来の
プロパティで`ChannelOpen`→`ProgramAdd`→`ProgramCommit`）に続けて`startChannel()`
（`xHeadConfig.applyChannel()`で構築したプロパティで`CmdChannelStart`）を呼ぶだけで、Source/
Programの実体は一切登場しない。**`CmdChannelStart`は「変調器とエンコーダパイプラインの電源を
入れる」channelレベルの操作であり、`CmdProgramApply`+`CmdSourceStart`はその後で「稼働中の
パイプラインに実際の映像/音声ソースを繋ぐ」という完全に別の後段ステップ**、というのが実際の
アーキテクチャだった。以前「ChannelStartはSource接続後の最後の仕上げ」と誤解していたのは、
たまたま`xTaskStartChannel.cs`（Source切替用のタスク）だけを読んでいて、デバイス接続時の
初期化タスクを見ていなかったのが原因。

さらに、`xHeadConfig.applyChannel()`が`CmdChannelStart`に載せて送るプロパティは
`mModulationParam`だけではなく、`applyChannelParam`(`mMTSChannelParam.Spec.ARIB_STD_B10.
RegionID`)、`applyCodecParam`(`mPSEncodeParam.Functions`/`Quality.Functions`/`BMLFile`)、
`applyModulationParam`(`mModulationParam.Frequency`、`mPSRFPowerAdjust.Level`/`PAGain`/
`DACGain`)、`applyEPGParam`(`mEPGSimpleParam.*`)の4グループにまたがっていた。特に
**`mPSEncodeParam`が`mPSEncoder`オブジェクトの設定そのもの**であり、これを一度も送っていな
かったことが、エンコーダが永遠に`Status==0`のままだった直接の原因だったと考えられる。

以前「ChannelStartをSourceなしで先に呼ぶとクラッシュする」と確認した過去のテストは、
`mModulationParam`（それも一部フィールドのみ）しか積んでいない状態での呼び出しであり、
今回判明した必須プロパティ群（`mMTSChannelParam`/`mPSEncodeParam`/`mPSRFPowerAdjust`/
`mEPGSimpleParam`）が欠けたままの不完全なリクエストがクラッシュの真因だった可能性が高い。

**実証**: `ChannelOpen`の応答`msChannel.Properties`（これまでダンプしたことがなかった）を
確認したところ、`mModulationParam`/`mMTSChannelParam`/`mMTSProgramParam`/`mPSEncodeParam`/
`mPSRFPowerAdjust`/`mEPGSimpleParam`の6グループ全てが妥当なデフォルト値付きで返ってきていた
（`mPSEncodeParam`は39フィールド、解像度1080i/YUY2/48kHzステレオ等、実運用に足る値が
最初から入っている）。この6グループを**変更せずそのままエコーバック**する形で
`CmdChannelStart`を`ProgramAdd`/`ProgramCommit`の直後・Source構築より前に呼んだところ:

```
ChannelStart(early): Result=ResultSuccess Status=StatusPrepare ParamCase=None
...
ProgramApply: Result=ResultSuccess          ← 悲願の成功
SourceStart: Result=ResultSuccess Status=StatusRunning
[event] EventChannelMediaActivate HandleID=33554433 ParamCase=ProgramID   ← 初観測のイベント
```

ネイティブログ側でも実際にハードウェアレベルの動作が確認できた:
```
mpegasys_function.cc:174] encoder [1920x1080 [30000/1001]]   ← Pegasysエンコーダが実パラメータで起動
mpegasys_output.cc:260] adjust power : [00:00]                ← RF電力調整が呼ばれた(Level=0のまま)
mnchannel.cc:337] channel [02000001] start output              ← チャンネルが実際に出力開始
mpegasys_encode.cc:333] OK                                     ← エンコーダの応答
```

クラッシュなし、デバイス・サービスとも健全。`bad status`の壁は完全に突破した。

### 続報5 (2026-07-25): 値の変更が実際に効くことを検証、RF電力の仕組みも解明

続報4はサーバーが返してきたデフォルト値を**無変更のままエコーバック**しただけであり、
「本当に値を変更して反映させられるか」はまだ未検証だった（このツールの本来の目的は公式GUIより
自由度の高い設定なので、ここの検証が本質的に重要）。

まず`mModulationParam.Constellation`(FieldID=19)をデフォルトのQAM64(3)からQPSK(1)に、
`mPSRFPowerAdjust.Level`(FieldID=0)を`0`から`30`に変更して送ってみたところ、`ChannelStart`/
`ProgramApply`/`SourceStart`は全て成功したが、ネイティブログの`adjust power : [00:00]`が
**変化しなかった**。おかしいと思い`xPowerLevel.cs`（decompiled）を確認したところ、`Level`は
そのまま使われる値ではなく、`level - 80`を添字とするテーブル引き（有効範囲は**80〜100**の21件
のみ、範囲外だと公式アプリ側では`ArgumentOutOfRangeException`）であり、実際に物理層へ効くのは
テーブルから引いた`PAGain`/`DACGain`の方だと判明した。周波数ごとに`RFPower473`/`RFPower569`/
`RFPower707`という3本の21件テーブルがあり、`Level=30`は単に範囲外の無意味な値だった。

`Frequency=473000`（テーブル`RFPower473`）・`Level=90`（添字`90-80=10`）で該当エントリ
`PowerGain(PAGain=2, DACGain=-10)`を求め、`Level`/`PAGain`/`DACGain`の3つを揃えて送ったところ:

```
mpegasys_output.cc:260] adjust power : [f6:02]
```

`0xf6`は符号付き8bitで`-10`、`0x02`は`2`——つまり送った`DACGain=-10`/`PAGain=2`が**そのまま
物理層に反映されている**ことを確認した。`Level`単体では何も起きず、`PAGain`/`DACGain`を
テーブルに沿って計算して送る必要がある、というのが実際の仕様。`tools/custom_sender`の
`SetPropertyValue`ヘルパーで、サーバーがエコーしてきたプロパティ列の特定フィールドだけを
書き換えてから`ChannelStart`に送る、という汎用的な「一部だけ変更」パターンが確立できた。

**現状**: プロトコルレベルでの送出・値変更は実証済み。RTL-SDRループバックでの実信号検証も
完了した（[tools/rtlsdr_analysis](../../tools/rtlsdr_analysis) — 送出中のみ470〜476MHz帯に
約38dBのパワー上昇を実測、送出停止で消失することも確認）。残るはビットレベルでの検証
（フルOFDM復調、または市販チューナーでのTS直接受信）のみ。

### 続報6 (2026-07-25): ISDB-T以外のMode切替を試行 → サーバー側検証で安全に拒否された

`Mode`セレクタが持つ8つの選択肢（DVB_T/J83A/ATSC/J83B/DTMB/ISDB_T/J83C/DVB_T2）のうち、
ISDB_T以外が実際に使えるのか（＝変調チップが本当に多規格対応なのか、それとも
ワイヤプロトコル上の記述だけが多規格分残っていて実態は未実装なのか）を、
Ghidraでの静的解析とライブテストの両方で確認した。

**静的解析（事実）**: `mnservice.exe`内に`ATSC`/`J83A`〜`J83C`/`DTMB`/`QAM16`等の文字列は
確かに存在し、複数の関数から参照されている。ただしこれらは以前見た「プロパティ記述子
（`msDescriptor`/`msPropertyRange`）を組み立てて選択肢一覧をGUIに見せる」コードパターンと
一致しており、**「実際にハードウェアへ設定を反映するコードが存在する証拠」にはならない**
（GUIに選択肢を表示するためだけの文字列である可能性が高い）。

**ライブテスト（事実、ユーザー承認の上で実施、実機無害を確認済み）**: `mModulationParam.Mode`
を`DVB_T`(0)に切り替え、DVB_T用の固有フィールド（ISDB_Tとは別のFieldID: Constellation=5,
Bandwidth=6, FFT=7, CodeRate=8, GuardInterval=9）にそれぞれの規格のデフォルト値を設定して
`CmdChannelStart`を実行したところ、**ハードウェアには一切触れずサーバー側のプロパティ
検証層で安全に拒否された**:

```
ChannelStart(early): Result=ResultFailStatus Status=StatusOffline ParamCase=ErrMessage
  ErrMessage=property[mModulationParam] field [Constellation] not exists
```

クラッシュなし、実機は健全なまま（`mnservice.exe`プロセスも生存継続）。安全性の面では
理想的な結果だった。

**結論と限界（事実+推測を明記）**: **事実**として、少なくとも今回試した素朴な送信方法
（`Mode`フィールドを書き換え、Mode固有フィールドをフラットな`Values`リストへ追加する方式）
では拒否された。**推測**として、これが「ハードウェア/ファームウェアが本当にISDB_T以外
未実装」なのか、「`Mode`がタグ付きユニオン型（`FieldConstSelect`、`IsSubGroup=True`）で
あることを踏まえた正しい送信フォーマットになっていなかった」だけなのかは、この結果だけ
では判別できない。エラーメッセージがフィールド名ベースの検証失敗である点から見て、
後者（送信フォーマットの問題）の可能性も十分残っている。これ以上の深掘りには
`FieldConstSelect`型の正しいワイヤ表現を静的に調べる必要があり、少なくとも
**現状ではISDB-T以外への切替は実証できていない**、というのが正直な到達点。

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

### 続報7 (2026-07-26): `mnservice.exe`非依存の直接RF送出を実証（プロジェクトの集大成）

[tools/usb_capture](../../tools/usb_capture)で解読したISDB-T変調レジスタマップと、
`CmdChannelStart`時にネイティブ側が実際に発行するレジスタ書き込みの**順序**（cdbで
rcx/r8/r9を直接ダンプして復元）を組み合わせ、[tools/direct_usb](../../tools/direct_usb)に
`--configure`モードとして実装した。`mnservice.exe`プロセスを一切起動しない状態で
このシーケンスを実機へ送ったところ、RTL-SDRループバックで**実際にRF電力上昇を確認**
（473.02MHz付近で+33.9dB、470.35MHz付近で+33.8dB、2回のスキャンで±0.1dB以内の
再現性）。詳細な書き込み順序・実測値・事実/推測の切り分けは
[tools/direct_usb/README.md](../../tools/direct_usb/README.md)の「マイルストーン」節を参照。

これにより、プロジェクトの当初目標だった「vendor DLL・`mnservice.exe`に一切依存しない
自前実装での駆動」を、レジスタ解読からRF出力の実測検証まで含めて達成した。ただし
バルク転送（TSストリーム）は一切送っていない状態での結果であり、出力されている信号が
正しいISDB-T変調（有効なOFDMフレーム）を含んでいるかはビットレベルでは未検証——
[tools/rtlsdr_analysis](../../tools/rtlsdr_analysis)で既知の限界と同じ。

追試として、標準的なnull-TSパケット列をバルクOUTへ3秒間・約32MB送出する`--stream`
モードも試したが、フローコントロール通知（`0x4A`/`0x4E`）を伴わない生送出では
RTL-SDRで観測可能な変化は一切なかった（[tools/direct_usb/README.md](../../tools/direct_usb/README.md)
「続報」参照）。TSデータの有無に関わらず同じRF出力が観測されており、レジスタ設定
（周波数・変調方式等）だけで決まる搬送波/アイドルパターンを見ている可能性が高いという
推測を補強する結果——ただし通知なしでは無視されるだけの可能性も残り、未確定。

### 続報8 (2026-07-26): 第三のSourceMode「Transcode」でカラーバー/サイントーンを自己完結生成

`mnClientDotNet.dll`を.NETリフレクションで調べたところ、これまで使ってきた`SourceUrl`
（ファイル、Content取得で頓挫）・`SourceCapture`（デスクトップキャプチャ、動作実証済み）に
加えて、**`msSourceMode.SourceTranscode`という第三のソースモード**が存在すると判明した。
`msSourceParam.Transcode`(`msTranscodeParam`型)は:

- `Colorbar`(`msColorbarMode`): `Testsrc2`/`Smptebars`/`Smptehdbars`/`Pal75bars`/
  `Pal100bars`/`Black`の6種類（`Smptebars`等の名前はFFmpegのlavfiソースフィルタ名と
  一致しており、内部でFFmpegを使って生成していると見られる——実際、動作中のログにも
  `mhal_ffmpeg.cc`/`ffmpeg timer pool start`が出現する）
- `SineTone`(`msSineToneMode`): `Mute`/`Beep`/`NoBeep` — 音声側のテストトーン
- `Video`/`Audio`: 解像度・フレームレート・コーデック・サンプルレート等

外部ファイルも画面キャプチャも不要な、完全自己完結型の試験信号ソースである。

**必須の実装上の注意（実機テストで判明、事実）**:

1. **`Engine`フィールドは`0`（未指定）を受け付けない** — `ProgramApply`の
   `msMediaContent.EngineID`と同じ実在のEngine HandleIDを明示的に指定する必要がある
   （`0`だと`UNAVAILABLE: engine [00000000] not exists`で拒否される）。
2. **`Video.Codec`/`Audio.Codec`に生（Raw）フォーマットは使えない** — 最初
   `RawYuv420P`/`PcmS16`で試したところ、`nvidia_cuvid`・`microsoft_d3d11va`の**両エンジンで
   共通に**`engine not supported transcode format`と拒否された。原因は逆コンパイル済み公式
   クライアントラッパー(`decompiled/mnClientDotNet/mnFramework/mnTranscodeParam.cs`)の
   `implicit operator bool`に明記されていた——`Video.Codec==RawVideo`・
   `Audio.Codec==RawAudio`は明示的に無効値として拒否するバリデーションが存在する。
   同ファイルのデフォルトコンストラクタが使う値（`Video.Codec=H264`・
   `Video.FrameStruct=Interlaced`・`Audio.Codec=MP1_L2`・`QueueTime=1000`）にそのまま
   合わせたところ、フォーマット検証は通過した。

**実機での結果（部分的成功、事実）**: 上記の修正版で`ChannelOpen→ProgramAdd/Commit→
ChannelStart→SourceOpen(Transcode/Colorbar)`を実行したところ、`mnservice.exe`のログには
以下が記録された:

```
mff_hardware.cc:92] codec [h264_nvenc:cuda:00000002]     ← H264ハードウェアエンコーダ初期化成功
mnchannel.cc:337] channel [02000001] start output         ← チャンネルが出力開始
mmts_source.cc:133] [0110] - 1583  /  [0111] - 988         ← パケットカウンタが1分以上増加し続けた
```

この間にRTL-SDRでスキャンしたところ、**470〜476MHz帯に+34〜35dBの電力上昇を実測**——
`Level=90`/`DACGain=-10`の設定と、これまでの検証済みRF出力と一致するシグネチャ。
つまり**カラーバー/サイントーンの自己完結ソースは実機で確かに機能し、実際にRF出力まで
到達している**。

一方で、**クライアント側は`SourceOpen`の応答受信時に`Unknown: Unexpected error in RPC
handling`という例外を投げてしまい、応答からSourceのHandleIDを受け取れない**——上記の
サーバー側ログ（エンコーダ起動・チャンネル出力開始・パケット流動）はこの例外の**後に**
記録されたものであり、サーバー側の処理自体は成功しているにもかかわらず、応答の
シリアライズ/デシリアライズのどこかで問題が起きている（原因未特定）。この結果、
クライアントからそのSourceを正常に停止できず、`mnservice.exe`を再起動するまで
孤立したまま動き続けた（実害はなし、実機・サービスとも健全性を維持）。

**現状のまとめ（事実と未解決を明記）**:
- 事実: `SourceTranscode`/`Colorbar`は実在し、正しいフォーマットを指定すれば実機で
  RF出力まで到達する。
- 未解決: `SourceOpen`応答の受信でクライアント側が例外を投げる問題は原因未特定。
  この応答パースの問題を直せば、`tools/custom_sender`にmnservice.exe/vendor DLL経由での
  完全に自己完結した「テストパターン送出ボタン」を追加できる見込みが高い。

再現コードは`tools/custom_sender/Program.cs`の`RunColorbarTest`（`--colorbar`引数で起動）。

### 続報9 (2026-07-26): 出力時のBML付与——ファイルパス方式と判明、XHEAD-STUDIO本体でも実証

`mPSEncodeParam.BMLFile`（`custom_sender`のプロパティダンプで確認: **FieldID=38, Type=FieldString**,
既定値は空文字列）を実際に検証した。文字列型ということは、データそのものではなく
**ローカルファイルパスを渡す設計**だと推測し、公式アプリ本体（`xhead_studio.exe`、
`EnableDebugMode`/`EnableBML`は既に有効化済みの状態）を実際に起動して確認した。

**事実（`xhead_studio.exe`のログ観察）**: アプリ起動直後、常に以下の警告が出る:

```
mmts_bml.cc:102] bml file [C:\Users\<user>\AppData\Roaming\Micomsoft\XHeadUSB\XHeadUSB_<user>.xbml] not exist.
```

これは`xHeadConfig.cs`の`BMLFile`静的プロパティが返すパスと完全一致する
（`decompiled/xhead_studio/xhead_usb/xHeadConfig.cs`: `Path.Combine(ConfigPath,
getFileName(Environment.UserName, "xbml"))`）。つまり**BMLは`EnableBML`フラグ（GUIのBMLタブ
表示可否）とは独立に、`ChannelStart`のたびに常にこの固定パスを探しにいく**——ファイルが
無くても警告が出るだけで送出自体は正常に続行される（BMLはオプショナル）。

**実験: 手作りの`.xbml`ファイルを置いてみる（事実）**: `decompiled/xhead_studio/xhead_usb.config/
xBMLFile.cs`で解読済みのバイナリ形式（ヘッダタグ`4201644322`→サイズ→ストリーム数→
各ストリーム(タグ`4221112873`+PID+ComponentTag+BitRate+ESInfoLength+RawLen+ESInfo64バイト+
生TSデータ)→エンドタグ`4235331587`）に従って、PID`0x140`・ComponentTag`0x40`・188バイトの
ダミーTSパケット1本だけを含む最小限の`.xbml`ファイルを自作し、上記の固定パスに配置した。

`xhead_studio.exe`を再起動したところ、**「not exist」警告が消え**、エンコーダ初期化・
`adjust power`・`channel start output`という通常時と同じ成功シーケンスがログに記録された。
RTL-SDRでも通常運用時と同じRF出力（470〜476MHz帯に+34〜36dB）を確認した。実機・
`mnservice.exe`とも終始健全だった。

**Ghidraでの裏付けと限界（事実として明記）**: `mmts_bml.cc`内の該当関数
（`FUN_1400a56f0`、`mnservice.exe`の`mmts_bml.cc:0x66`相当）をデコンパイルしたところ、
中身は非常に単純だった:

```c
undefined1 FUN_1400a56f0(char *path) {
    if (strlen(path) != 0) {
        FILE *f = fopen(path, "rb");
        if (f == NULL) { /* "bml file [%s] not exist." を警告ログ */ return 0; }
        fclose(f);
        return 1;   // ファイルが開けるかどうかだけを見ている
    }
    return 0;
}
```

**この関数は「ファイルが存在し`fopen`できるか」だけを検証しており、中身のバイナリ構造
（ヘッダタグ・ストリームエントリ等）を解釈するコードではない**。つまり今回確定的に言えるのは
「ファイルパスの解決とオープンには成功した」ことまでで、**自作した`.xbml`の中身が実際に
正しいBMLコンテナとしてパースされ、データ放送コンポーネントとしてTSに多重化されたかどうかは
未確認**（実際のパーサー関数は今回のGhidra探索では特定できなかった）。ビットレベルでの
確認にはTSDuck等での実TS解析が必要——[tools/rtlsdr_analysis](../../tools/rtlsdr_analysis)の
既知の限界と同じ。

**まとめ（事実と未解決を明記）**:
- 事実: `mPSEncodeParam.BMLFile`は固定のローカルファイルパス方式。`EnableBML`フラグとは
  無関係に、ファイルが存在しさえすれば`ChannelStart`のたびに読み込まれる。
- 事実: 自作の最小限`.xbml`ファイルで「存在確認」ゲートは通過し、送出パイプライン全体も
  正常動作・RF出力も確認できた。
- 未解決: ファイルの中身（バイナリ構造・PID/ComponentTag/ESInfo/生TSデータ）が実際に
  正しく解釈されているかは、対応するパーサー関数の解析もビットレベルでのTS確認も
  できておらず未確認。

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

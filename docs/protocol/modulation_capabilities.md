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

`FieldConstSelect` という型名の通りタグ付きユニオン構造。**8モード全てを実機で切替テスト
済み**（結果内訳: 成功3・安全な拒否2・サービスハング2、詳細は「続報12・13・14」参照）:

| Mode | 主なフィールド | 実機テスト結果 |
|---|---|---|
| `DVB_T` | Constellation(QPSK/QAM16/QAM64), Bandwidth, FFT(2k/4k/8k), CodeRate(1/2〜7/8), GuardInterval(1/32〜1/4) | **成功**（RF出力+37〜39dB実測） |
| `J83A` (欧州系ケーブルQAM) | Constellation(QAM16/32/64/128/256) | 安全な拒否（`modulation param invalid`） |
| `ATSC` | Constellation(8VSB固定) | **成功**（RF出力+38〜47dB実測） |
| `J83B` (北米ケーブルQAM) | Constellation(QAM64/256) | **成功**（RF出力+38dB実測） |
| `DTMB` (中国地デジ) | Constellation, Bandwidth, CodeRate(0.4/0.6/0.8), Carrier(3780/1), Frame(420/945/595), Interleave(240/720) | **サービスハング**（`mnservice.exe`が無応答に、実機は健全） |
| **`ISDB_T`** | 下表参照 | 通常モード（本ドキュメントの主対象） |
| `J83C` (日本ケーブルQAM) | Constellation(QAM64/256) | **サービスハング**（同上、単一フィールドでも発生） |
| `DVB_T2` | Version, Bandwidth, Function(拡張キャリア/コンステレーション回転/HEM/Null Packet Deletion), L1Constellation, PLPConstellation, FFT(1k〜32k), CodeRate(1/2〜2/5), GuardInterval(1/32〜19/256), PilotPattern(PP1-8), FEC(16200/64800), NetworkID(既定0x3085=12421), SystemID(既定0x8001=32769), FECBlockNums, SysmbolNums, TINumber, ISSYLength | サービスは明示的にMode 7非対応。`direct_usb`最小列の実験RF出力は成功（規格準拠未確認） |

DTMB/J83Cのハングは物理的な実機の抜き差し後も再現する**モード固有の本物のバグ**と確認済み
（続報14の訂正を参照——同種のハング症状が別原因（USB接続の劣化）だったケースもあるため
混同注意）。ビットレベルでの規格準拠（正しいOFDMフレーム内容か）は成功した3モードとも未検証。
**下記はあくまで変調チップ/ファームウェアが「知っている」構造であって、XHEAD-USBのアンテナ
回路や認証がISDB-T以外の規格の送出を安全・合法に行える保証は一切ない**。詳細は本ファイル
末尾の注意事項を参照。

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

**2026-07-26追記**: この節で「行き詰まった」と結論しているContent取得問題は、直後の「続報」で
判明した通りクライアント側の実装ミスが原因であり、`SourceUrl`自体が壊れていたわけではなかった。
ただしこの節が書かれた時点ではまだ`SourceCapture`側でしか修正・再検証しておらず、
`SourceUrl`自体はその後一度も再テストされないまま放置されていた。2026-07-26に再挑戦して
**一発で成功**している——詳細は本ファイル末尾の「続報10」を参照。

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

**【2026-07-26追記】この結論は誤りだった——続報12で覆っている。** 後者（送信フォーマットの
問題）が正解で、Mode切替自体は実際に成功する。詳細は下記「続報12」を参照。

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
モードも試したが、当時「フローコントロール通知」と解釈していた周期的な
`0x4A`/`0x4E`読み出しを伴わない生送出では
RTL-SDRで観測可能な変化は一切なかった（[tools/direct_usb/README.md](../../tools/direct_usb/README.md)
「続報」参照）。TSデータの有無に関わらず同じRF出力が観測されており、レジスタ設定
（周波数・変調方式等）だけで決まる搬送波/アイドルパターンを見ている可能性が高いという
推測を補強する結果。ただし後の解析で`0x4A`/`0x4E`は通知ではなく汎用レジスタ読み出しと
判明し、さらにこの試験は約80Mbit/sの無制限送信だった。リングのオーバーランという交絡因子が
あるため、現在は「通知不足が原因」という解釈を撤回し、ビットレート制御版での再検証待ち。

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

**【2026-07-26追記・訂正】上記「クライアント側の応答パース問題」という原因推定は誤りだった。**
`--verbose-grpc`フラグ（`GRPC_VERBOSITY=DEBUG`・`GRPC_TRACE=all`・`GrpcEnvironment.SetLogger`を
`Channel`構築前に設定、`Program.cs`に追加）でgRPCの詳細ログを有効化して再実行したところ、
例外の実体は次の通りだった:

```
Grpc.Core.RpcException: Status(StatusCode="Unknown", Detail="Unexpected error in RPC handling", ...)
  ---> Grpc.Core.Internal.CoreErrorDetailException: {"description":"Error received from peer
       ipv6:[::1]:50051", "file":"...\\src\\core\\lib\\surface\\call.cc", "grpc_status":2}
```

**「Error received from peer」は、クライアントが応答をパースできなかったのではなく、
サーバー（`mnservice.exe`）自身がgRPCレベルで`UNKNOWN`ステータスを明示的に返してきた
ことを意味する**——C-coreのこの文言・ファイル位置は、本プロジェクトで何度も見てきた
「サーバーが送ってきた正規のエラー応答」（`wait service timeout`・`unhandled command`等）と
全く同じラッパーであり、クライアント側のデシリアライズ失敗ではローカルの例外型・
スタックトレースになるはずでここには出てこない。`grpc_status=2`(UNKNOWN)・
メッセージ`"Unexpected error in RPC handling"`という汎用文言は、gRPCのC++サーバー
フレームワークで一般的な「リクエストハンドラ内で未処理の例外が発生した際の
汎用catch-allレスポンス」の定型文と一致する。

つまり実際には**`mnservice.exe`が`SourceOpen(Transcode)`の処理中に内部で例外を投げており、
それをgRPCサーバー側のフレームワークが捕捉してこの汎用エラーとして返している**、という
ネイティブ側の問題であり、本ツール側の応答パースには問題が無かった。エンコーダ初期化や
チャンネル出力開始のログがこの後に記録されるのは、内部処理の一部が例外発生前後で
非同期的に既に走り始めていたためと考えられる（推測）。`SourceTranscode`はSTUDIO自身の
通常のGUIフロー（ファイル一覧からの再生）では使われていない、リフレクションで発見した
「第三のSourceMode」であるため、DTMB/J83Cのハング（続報13）と同様に、STUDIO自身が
日常的に踏まないコードパスに実装の粗さが残っている可能性が高い——これはクライアント側で
修正できる問題ではなく、`mnservice.exe`自体の挙動として受け入れるしかない。

**今後の方針**: この例外は「機能が壊れている」ことを意味しない（RF出力は実際に到達している）
ため、GUIに統合する場合は「既知の警告が出るが送出自体は動作する」という注記付きで
提供するのが現実的。より深い原因（`mnservice.exe`内のどの処理が具体的に例外を投げているか）
を特定するには、cdbで`SourceOpen`ハンドラ周辺にブレークポイントを張るネイティブ動的解析が
必要——未着手。

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

### 続報10 (2026-07-26): `SourceUrl`再挑戦 — 一発成功、GUIにも統合

ユーザーから「STUDIOでできることは自分のツールでもできるようにしたい」という長期方針が
示され、まずギャップ分析（実ソース添付・`mPSEncodeParam`全フィールド・BML統合・
チャンネル/番組メタデータ等）を行った上で、最初の一手としてデスクトップキャプチャの
GUI統合（後述）を終えた直後、次点として`SourceUrl`（動画ファイル指定）に再挑戦した。

続報（EventSourceStatusは実はContentを運んでいた）で判明した根本原因の修正
（`ev.Status.Content`を直接読む）は、当時`SourceCapture`側の調査中に見つかったもので、
**`SourceUrl`側では一度も再検証されないまま放置されていた**。同じ`EventWatcher`の仕組みを
そのまま使い、実TSファイル（`C:\Users\aoiro\Videos\ts\Record_20251109-210722.ts`、46MB、
以前の調査と同一ファイル）で試したところ:

```
SourceOpen(Url): Result=ResultSuccess ParamCase=Source
Waiting for EventSourceStatus to reach StatusReady...
Source status after wait: StatusReady ContentPrograms=2
Source's Program ID=3096 Streams=3
  Stream Index=0: Video 1440x1080 Interlaced FPS_29_97
  Stream Index=1/2: Audio AAC_LC_ADTS 48000Hz Stereo
ProgramApply: Result=ResultSuccess
SourceStart: Result=ResultSuccess Status=StatusRunning
```

**一発で成功**。RTL-SDRでも実RF出力を確認した（470〜476MHz帯に+34〜36dB、既知のシグネチャと
一致）。1回目の検証では8秒の保持時間中にスキャンのタイミングを逃して小さな差分しか
観測できなかったが、保持時間を一時的に延ばして再試行したところ確実に確認できた——
タイミングの問題であり、送出そのものは1回目から成功していた。

`tools/custom_sender`のCLI（`--sourceurl [ファイルパス]`、省略時は上記ファイルを使用）と
GUI（`GuiSession.StartUrlSource`、ソース選択に「動画ファイル」ラジオボタン+パス入力欄+
ファイル選択ダイアログを追加）の両方に統合した。`SourceCapture`用に書いた
`StartCaptureSource`と共通のRPC後半処理（`EventSourceStatus`待機→エンジン選択→
`ProgramApply`→`SourceStart`）を`AttachSourceToChannel`として切り出して再利用している。

これで`SourceUrl`（ファイル）・`SourceCapture`（デスクトップキャプチャ）の2つの実ソースが
CLI・GUI両方から動作確認済みとなった。残っているのは`SourceTranscode`（カラーバー、
クライアント側のレスポンス処理に既知のバグあり、続報8参照）のGUI統合。

### 続報11 (2026-07-26): 字幕はSourceパイプラインで落ちている、EPGは本当に1件固定と確認

ユーザーから「STUDIOだと再エンコードの関係で字幕が出せないのでは」「EPGが1件しか設定できず
ずっと繰り返される」という2つの指摘があり、それぞれ事実確認を行った。

**字幕（事実、TSDuckで確認）**: `SourceUrl`テストで使っている実TSファイルをTSDuckの
`tsanalyze`/`tstables`で解析したところ、`Service 0x0C18(3096)`（実際に`SourceOpen`が
報告する`Program ID`と一致）には映像・音声2本以外に以下の成分が存在した:

| PID | Component Tag | 内容 |
|---|---|---|
| 0x0114 | 0x30 | ARIB subtitle & teletext coding（**字幕本体**） |
| 0x0115 | 0x38 | 同上（副次的な字幕/文字スーパー） |
| 0x0840, 0x0850, 0x0857〜 | 0x40, 0x50, 0x57... | DSM-CCセクション（データ放送カルーセル、"Multimedia coding for digital terrestrial broadcasting"） |

一方、`mnservice.exe`が`SourceOpen(Url)`で報告する`Streams`は**常に3本（映像1・音声2）のみ**
——字幕・データ放送成分は一切含まれない。原因は、ソースの非同期プロービングが内部で
Media Foundationベースのデマルチプレクサを使っており、これは標準的な映像/音声コーデックしか
「ストリーム」として認識せず、ARIB字幕（MPEG-2 PES private data）やDSM-CCセクションのような
放送特有のプライベートデータは最初から見えていない、という説明が最も自然（未確定、推測）。
つまり**通常の`SourceUrl`→`ProgramApply`経路では、字幕・データ放送は構造的に絶対に
渡せない**——ユーザーの指摘は正しかった。

**BMLFile経由での字幕再注入を試行（部分的成功）**: `mPSEncodeParam.BMLFile`
(`xBMLFile.cs`で解読済みの独自バイナリコンテナ)経由なら、Source経由では見えない補助
ストリームを後から注入できるのではという仮説を検証した。

1. TSDuckで実際の字幕PES(PID 0x0114)を生TSパケットのまま抽出(`tsp -P filter --pid 0x0114`)。
2. 実際のPMT記述子(`tstables`で確認: Stream Identifier記述子+ISDB Data Component記述子、
   `data_component_id=0x0008`)から正確な`ESInfo`を組み立て、`xBMLFile.cs`の形式通りに
   `.xbml`コンテナへパッケージング。
3. `custom_sender`で`mPSEncodeParam.BMLFile`にこのファイルパスを設定して`ChannelStart`。

**重要なバグ修正**: 最初の試行では`BMLFile`プロパティを一切設定せず(`ChannelOpen`の
既定値である空文字列のまま)テストしてしまい、`mnts_bml.cc`の存在確認関数
(`strlen(path)!=0`のガードあり)が呼ばれることすらなく静かに素通りしていた
——XHEAD-STUDIO自身は内部で常にこのプロパティを固定パスに設定しているため気づかなかった
差異。`SetPropertyValue(channelStartProps, "mPSEncodeParam", 38, v => v.StrVal = path)`を
明示的に追加して修正した（`tools/custom_sender`の`--bmlfile`オプション）。

修正後、cdbで`mmts_bml.cc`の存在確認関数（`FUN_1400a56f0`）に直接ブレークポイントを張り、
`rcx`（第1引数）の文字列を`da`でダンプしたところ、意図した通りのパス
（`XHeadUSB_aoiro.xbml`）で確かに呼ばれていることを確認した。ログにエラーは一切出ず
（＝`fopen`成功）、`ChannelStart`/`SourceOpen`/`ProgramApply`/`SourceStart`まで全て
`ResultSuccess`で完走した。

さらにGhidraで呼び出し元を遡ったところ、存在確認が通った直後に
**`mazo::mrevolution::mMTSBMLFile`という実在のC++クラス**（vftableのシンボル名から判明）が
構築・初期化されていることを確認した——スタブではなく、BMLファイル読み込み専用の本格的な
実装が存在する。ただし、その内部の実際のパース処理（`FUN_1400a5b70`等）まではデコンパイル
できておらず、**投入した字幕データが実際に正しく解釈されTSに多重化されたかはビットレベルで
未確認**。

**現状のまとめ（事実と推測を明記）**:
- 事実: 通常のSource経由では字幕・データ放送成分は構造的に落ちる。
- 事実: `BMLFile`プロパティを正しく設定すれば、抽出した実字幕データを含む自作コンテナが
  存在確認・ネイティブクラス初期化まで到達する（cdbで直接確認済み）。
- 未確認: 実際にTSへ多重化されるか、正しいタイミング（PTS同期）で再生されるか
  ——ビットレベル検証にはTSDuck等での出力側キャプチャ、または実チューナーでの受信が必要。
- 推測: 字幕データはオリジナル録画のタイムスタンプのまま注入しており、再エンコードされた
  映像/音声とは時間軸が一致しない可能性が高い（同期の作り直しは未着手）。

**EPG（事実、`mEPGSimpleParam`の全フィールドをダンプして確認）**:

| FieldID | 名前 | 内容 |
|---|---|---|
| 0 | Mode | `Disable`/`PresentFollowingOnly`/`AribPresentFollowingOnly`/`AribSchedule_8Days`（既定257=8日間スケジュール） |
| 1 | IntervalHours | 0〜8（既定1） |
| 2 | EventID | 0〜65535（既定4096） |
| 3 | Type | ジャンル（News/Sport/Movie/...等、単一選択） |
| 4 | Title | 文字列（maxlen=256） |
| 5 | Descriptor | 文字列（maxlen=256） |

**ユーザーの指摘通り、`mEPGSimpleParam`は文字通り「1つの番組情報（Title/Descriptor/
Type/EventID）をスケジュールモードに従って繰り返し配信するだけ」の構造**であり、複数の
異なる時間帯・タイトルを持つ番組を並べる余地は一切ない。`ChannelOpen`が返す6プロパティ
グループ（mModulationParam/mMTSChannelParam/mMTSProgramParam/mPSEncodeParam/
mPSRFPowerAdjust/mEPGSimpleParam）の中に、より高機能な「Advanced EPG」に相当する
別グループは存在しない——少なくともプロパティツリーのレベルでは、STUDIOだけでなく本ツールも
含めて、複数番組のEPGを直接設定する手段は今のところ確認できていない。生のEIT
（Event Information Table）セクションを直接注入するような別経路が`mnservice.exe`内部に
存在するかどうかは未調査（BMLFileと同様の「別経路」がある可能性はゼロではないが、
現時点では推測の域を出ない）。

### 続報12 (2026-07-26): 【訂正】ISDB-T以外のMode切替は成功する——続報6の失敗は送信フォーマットのバグだった

続報6の「field [Constellation] not exists」拒否について、静的解析で判明した`msVariant`の
ワイヤ表現（`IsSubGroup=True`なフィールドの子は専用のネスト型を持たず、フラットな
`Values`リストに兄弟エントリとして並ぶ——これは`DacCtrl`の`IFMode`/`IFFreq`/`GAIN`で
既に確認済みのパターン）を踏まえ、続報6の失敗原因を再検証した。

**仮説**: 続報6の実装は`Mode`フィールドの値だけを書き換え、DVB_T固有フィールド
（FieldID 5=Constellation, 6=Bandwidth, 7=FFT, 8=CodeRate, 9=GuardInterval）を
フラットな`Values`リストに**追加**しただけで、`ChannelOpen`が最初にエコーしてきた
ISDB_T固有フィールド（FieldID 19=Constellation, 20=Bandwidth, 21=FFT, 22=CodeRate,
23=GuardInterval, 24=TimeInterleavce）を**一切削除していなかった**。ISDB_Tの
FieldID=19とDVB_TのFieldID=5は、どちらも表示名が同じ`"Constellation"`——同じ`Values`
リストの中に同名フィールドが2つ（異なるFieldIDで）同時に存在する状態になっており、
サーバー側の検証がこれを取り違えた可能性が高いと推測した。

**ライブテスト（事実、ユーザー承認の上で実施）**: `tools/custom_sender`に
`RunModeSwitchTest`（`--dvbt`フラグ）を新設し、`channelStartProps`から
`mModulationParam`のISDB_T固有フィールド（FieldID 19〜24、計6個）を**明示的に削除して
から**`Mode=DVB_T(0)`に切り替え、DVB_T固有フィールドを既定値
（Constellation=QAM64(4), Bandwidth=6, FFT=_8k(1), CodeRate=CR_5_6(3),
GuardInterval=GI_1_16(1)）で追加。Source構築前の早期`CmdChannelStart`（`mPSRFPowerAdjust`
等は`ChannelOpen`の既定値のまま、意図的に未変更）で実行した結果:

```
Removed 6 stale ISDB_T-mode field(s) from mModulationParam before switching Mode.
mModulationParam.Mode=DVB_T(0), Constellation=QAM64(4), Bandwidth=6, FFT=_8k(1), CodeRate=CR_5_6(3), GuardInterval=GI_1_16(1)
ChannelStart(DVB_T): Result=ResultSuccess Status=StatusPrepare ParamCase=None
```

**仮説通り、Mode=DVB_Tへの切替がサーバー側検証を通過した**——続報6の失敗はハードウェア/
ファームウェアの制約ではなく、単に本ツール側の送信フォーマットの不備（削除漏れ）だった
ことが確定した。

**RTL-SDRでのRF実測（事実）**: `ChannelStart(DVB_T)`成功直後、`rtl_power`で465〜481MHzを
1秒積分・2回連続スキャンし、既存の`rtlsdr_baseline.csv`と比較した
（[tools/rtlsdr_analysis](../../tools/rtlsdr_analysis)に
`rtlsdr_dvbt_scan1.csv`/`rtlsdr_dvbt_scan2.csv`として保存済み）。

| 周波数 | 1回目 delta | 2回目 delta |
|---|---|---|
| 471.21MHz | +37.56dB | +38.24dB |
| 472.10MHz | +37.55dB | 同帯域で+37dB台 |
| 474.31MHz | +37.91dB | 同帯域で+37dB台 |

470〜476MHz帯全体（ISDB-Tの6MHz帯域幅・設定周波数473MHzと一致する範囲）にわたって
+37〜39dBの明確なプラトーが確認でき、2回のスキャンで概ね±1dB以内の再現性があった。
数値の大きさ・帯域形状は、これまでのISDB_TモードでのRF確認（+33〜39dB、続報5/7/8/10）と
ほぼ同水準。**`mPSRFPowerAdjust`（Level/PAGain/DACGain）を一切調整していない
（`ChannelOpen`の既定値0/0/0のまま）にもかかわらず**この出力が得られた点は事実として
特筆に値する（続報5で「Level=30単体では無効、PAGain/DACGainとセットで初めて効く」と
分かっていたはずが、ここではLevel=0のままでも強い出力が出た——RF電力調整とMode/変調方式の
初期化が独立した経路である可能性を示唆する。未確定、推測）。

テスト後、`mnservice.exe`は同一PID・同一起動時刻のまま生存し続け、`Get-PnpDevice`でも
デバイスは`Status=OK`のまま——クラッシュや異常なし。

**結論と限界（事実+推測を明記）**:
- **事実**: `mModulationParam.Mode`をISDB_T以外（少なくともDVB_T）に切り替える
  プロトコル操作は、正しい送信フォーマット（旧モードの固有フィールドを削除してから
  新モードの固有フィールドを追加）であれば実際に成功し、`ChannelStart`まで完走する。
- **事実**: その状態で、ISDB_Tモードと同水準（+37〜39dB）の明確なRF出力が470〜476MHz帯に
  観測された。
- **未確認（ビットレベル）**: 出力されている信号が規格に準拠した正しいDVB-T OFDM
  フレーム（有効なパイロット・TPS・FFTサイズ設定等）を含んでいるかは未検証——これまでの
  ISDB_Tモードのテストと同じ限界。フルOFDM復調、または市販DVB-Tチューナーでの直接受信が
  必要。
- **【2026-07-26追記】残り6モードも実施した——結果は「成功」「明確な拒否」「サービス
  ハング」の3通りに分かれた。詳細は下記「続報13」を参照。**
- **重要な注意（再掲・強調）**: XHEAD-USBはISDB-T（UHF帯）専用機として設計・
  （おそらく）認証された製品。ここで確認したのは「プロトコル層でMode切替が受理され、
  RFフロントエンドから何らかの強い出力が出ること」までであり、それが電波法上適法な
  範囲の信号であることは一切意味しない。本検証は同軸ループバック(RTL-SDR)に限定して
  おり、アンテナ接続での送出は行っていない。実運用（アンテナ接続）での非ISDB-Tモード
  送出は行わないこと。

### 続報13 (2026-07-26): 残り6モードを実施 — 成功3・拒否1・サービスハング2という結果

続報12のDVB_T成功を受け、`RunModeSwitchTest`を汎用化（`ModeSpec`構造体でモードごとの
FieldID/既定値テーブルを持ち、どのモードが直前にアクティブだったかに関わらず
`mModulationParam`の全モード固有フィールド(FieldID 5〜41)を一括で剥がしてから対象モードの
フィールドを追加する版に変更）し、残り6モード（J83A/ATSC/J83B/DTMB/J83C/DVB_T2）を
`--j83a`/`--atsc`/`--j83b`/`--dtmb`/`--j83c`/`--dvbt2`として実施した。結果:

| Mode | 結果 | 詳細 |
|---|---|---|
| `J83A` | **明確な拒否** | `ChannelStart`が`ResultFail Status=StatusOffline ErrMessage=modulation param invalid`で即座に拒否。ハードウェアに触れず、`mnservice.exe`も健全なまま——安全な失敗 |
| `ATSC` | **成功・RF確認済み** | `ChannelStart: ResultSuccess`。RTL-SDRで470〜476MHz帯に+38〜47dBの明確なプラトー（`rtlsdr_atsc_scan1/2.csv`） |
| `J83B` | **成功・RF確認済み** | `ChannelStart: ResultSuccess`。同帯域に+38dB前後（`rtlsdr_j83b_scan1/2.csv`） |
| `DTMB` | **サービスハング** | `ChannelStart`が10秒のgRPCデッドラインを超過（`DeadlineExceeded`）。以降`mnservice.exe`は新規接続を一切受け付けなくなり（`wait service timeout`）、事実上のサービス全体ロック |
| `J83C` | **サービスハング** | 新規に再起動した`mnservice.exe`インスタンスでも同一の`DeadlineExceeded`→`wait service timeout`を再現。フィールド数はJ83A/ATSC/J83Bと同じ「Constellation 1個のみ」という最も単純な構造にもかかわらず発生——フィールド数の複雑さとは相関しない |
| `DVB_T2` | **明確な拒否** | ユーザー確認の上で実施。`ChannelStart`が`ResultFail Status=StatusOffline ErrMessage=modulation param invalid`で即座に拒否——J83Aと同じ安全な失敗パターン。ハング再現なし、`mnservice.exe`は同一PID・同一起動時刻のまま健全に生存 |

**ハングの性質（事実）**: DTMB/J83Cいずれも、クライアント側は`ChannelStart`のgRPC呼び出しが
10秒のデッドラインを超過して`DeadlineExceeded`になるだけだが、その直後の**あらゆる新規
リクエスト**（別プロセスからの`CmdChannelOpen`すら）が`Cancelled: wait service timeout`で
即座に拒否されるようになった——`mnservice.exe`プロセス自体は`Get-Process`上は生存・
`Responding=True`のままだが、内部のリクエスト処理が特定のチャンネル/内部ロックで完全に
停止し、**サービス全体が新規クライアントを受け付けなくなる**（過去に発見していた「単一の
サービス全体ブロードキャスト状態フラグ」説と整合する挙動）。プロセスを強制終了して
再起動する以外に回復方法はない（`mnservice.exe`は単なる子プロセスであり、実際にkill→
再起動で毎回クリーンに回復することを確認済み）。

**ハードウェア健全性（事実、最重要）**: 両ハング発生直後、`tools/direct_usb`の読み取り専用
レジスタスキャンを`mnservice.exe`を一切経由せず直接実行したところ、全レジスタが既知の
正常値のまま読み出せた（`0x1220=0x78122900`のコミット済みシグネチャ含む、ゴミ値や
オールFFのような異常は皆無）。`Get-PnpDevice`も一貫して`Status=OK`のまま。**このハングは
`mnservice.exe`のソフトウェア層に閉じた問題であり、実機USBハードウェア自体は一貫して
健全**——本セッション全体を通じて確立してきた「ソフトウェアクラッシュ/ハングは安全、
実機は別途都度確認」という前提が、このより深刻な「ハング」ケースでも成立することを確認できた。

**解釈（推測、明記）**: DTMB/J83Cの成功していない結果が「明確な拒否」ではなく「ハング」に
なっている点は、J83Aの即時拒否（`modulation param invalid`）と対照的。最も筋が通る推測は、
J83A（値レベルの検証で弾かれる）とDTMB/J83C（検証は通過するがハードウェア側の何らかの
ready/ACKビットを待ち続けて戻ってこない）とで、ネイティブ側の実装が異なるコードパスを
通っている、というもの——例えば「本当に対応していないモードは値検証で弾く」実装と
「レジスタは書き込むがチップからの完了応答を待つ」実装が混在しており、後者のモードの一部
（DTMB・J83Cがそれに該当）で実際のチップがその応答を返さない、という仮説。ただし静的/動的
解析による裏付けはまだ取っておらず、あくまで観測されたエラーメッセージの違いからの
推測にとどまる。

**方針判断**: DTMB→J83Cと2回連続でハングを確認した時点で、DVB_T2（全モード中最多の16
フィールドを持ち、未検証のフィールドが最も多い）を同じ自動バッチで無警戒に実行するのは
リスクの質が変わったと判断し、ユーザーへの結果共有を優先して自動実行を止めた——「成功」
「安全な拒否」以外に「サービスハング」という第三の結果パターンが実在すると分かった以上、
より複雑なモードほどその発生確率が上がる可能性を無視できないため。

**【追記】ユーザー確認の上でDVB_T2も実施——ハングせず、安全に拒否された。** 結果は上表の
通り`ErrMessage=modulation param invalid`（J83Aと同一パターン）。これで8モード全てを
実施済みとなり、この時点での結果は「成功=DVB_T/ATSC/J83B（3）」「安全な拒否=J83A/DVB_T2（2）」
「サービスハング=DTMB/J83C（2）」「ISDB_T=元々の動作モード（1）」の内訳で確定。フィールド
数（DVB_T2=16、J83C=1）とハング有無に相関がないことも改めて裏付けられた——「値検証で
弾かれるか、レジスタ書き込み後にチップ応答待ちでハングするか」は各モードの実装ごとの
個別事情であり、複雑さでは予測できない。

### 続報14 (2026-07-26): mMTSChannelParam/mMTSProgramParamへの明示的な書き込みは、どのフィールドでもサービスをハングさせる

STUDIOパリティの広いロードマップに戻り、`tools/custom_sender`のGUIに「チャンネル/番組情報」
タブ（サービス名・ネットワーク名・TS名・地域識別・放送事業者ID・リモコン番号・サービス
番号・コピー制御——`mMTSChannelParam`/`mMTSProgramParam`のARIB_STD_B10サブ構造体）を追加
する作業を行った。実装直後、送出前のライブ検証（`tools/custom_sender --meta <subset>`という
専用の切り分けテストを新設）を行ったところ、**続報13のDTMB/J83Cと全く同じ「サービス
ハング」が、この2プロパティグループのどのフィールドを触っても再現する**という重大な問題が
判明した。

**切り分けテストの結果（事実、全て`mnservice.exe`を毎回フルリスタートしたクリーンな状態で実施）**:

| テスト内容 | 結果 |
|---|---|
| `mMTSChannelParam`全5フィールド (RegionID/BroadcasterID/RemoteControlKeyID/NetworkName/TSName) | ハング |
| 上記の数値3フィールドのみ (RegionID/BroadcasterID/RemoteControlKeyID) | ハング |
| `BroadcasterID`単体（テスト値5） | ハング |
| `BroadcasterID`単体、**既存値と同一の値(1)へのno-op書き込み** | ハング |
| `BroadcasterID`を除く全フィールド（RegionID/RemoteControlKeyID/NetworkName/TSName/`mMTSProgramParam`全3フィールド） | ハング |
| `mMTSProgramParam`全3フィールド (ServiceNo/CopyFlag/ServiceName) 単体 | ハング |

**重要な事実**: `BroadcasterID`を**既に持っている値と全く同じ値(1)に「書き込む」だけ**でも
ハングする——つまり値の正当性の問題ではなく、**このプロパティ2グループのいずれかの
フィールドを`ChannelStart`のリクエストで明示的に触れること自体**が、`mnservice.exe`の
どこかの未知のコードパスを踏んでハングを引き起こしている。続報13のDTMB/J83Cと全く同じ
症状（`ChannelStart`が10秒のgRPCデッドラインを超過→以降`wait service timeout`でサービス
全体が新規リクエストを一切受け付けなくなる→プロセスは`Get-Process`上生存・
`Responding=True`のままだが実質全機能停止）で、`direct_usb`の読み取り専用スキャンでは
実機ハードウェアは6回のハング全てで健全なまま（レジスタ値は毎回既知の正常値、
`Get-PnpDevice`も`Status=OK`のまま）だった。

**過去の記録との矛盾**: `tools/usb_capture/README.md`には以前「`mMTSChannelParam.RegionID`
にマーカー値(55)を設定してもクラッシュせず正常に完走した」という記録がある(別セッション、
レジスタバス解析の文脈)。今回`RegionID`単体だけを切り出したテストはまだ実施していないため
直接比較はできないが、**少なくとも`BroadcasterID`・`mMTSProgramParam`全体は今回明確に
ハングを再現しており、以前の「安全」という結論を無条件にこの2グループ全体へ拡張しては
いけない**。同じ`mMTSChannelParam`という1つのstructの中でも、フィールドによって安全性が
異なる可能性がある(あるいは、その後のmnservice.exeのアップデート/環境変化で状況自体が
変わった可能性も否定できない——未確定)。

**対応**: GUI「チャンネル/番組情報」タブと、対応する`GuiSession.StartChannel()`の
`SetPropertyValue`呼び出しを全て撤去した(未完成の機能を残さない方針)。`ModulationConfig`の
関連フィールドも削除。CLIの切り分けテスト自体(`tools/custom_sender --meta <subset>`、
`subset`は`all`/`channel`/`channel-num`/`channel-str`/`program`/`regionid`/`broadcasterid`/
`broadcasterid-noop`/`broadcasterid-0`/`remotekey`)は今後の追加調査用に残してある。

**未解決（今後の課題）**: `RegionID`単体、`RemoteControlKeyID`単体、`channel-str`単体
(NetworkName/TSName)、`ServiceName`単体など、個々のフィールドに絞った切り分けはまだ
未実施——「全フィールドが一律ハングする」のか「一部の安全なフィールドもあるが今回のテスト
の組み合わせがたまたま毎回危険なフィールドを含んでいた」のかは、現時点では確定していない。
再挑戦する場合は`--meta regionid`のように1フィールドずつ確認すること。

**【2026-07-26 同日夜・重大な訂正】上記の結論は誤りだった——原因はプロトコル/フィールドの
問題ではなく、USB接続そのものの劣化状態だった。物理的な抜き差し1回で完全に解消した。**

続報13・14を通じて`mnservice.exe`を数十回という単位で強制終了・再起動し、`direct_usb`での
生レジスタ読み書きも並行して行うという、通常のUSBデバイスの使い方からは大きく外れた負荷を
一日中かけ続けていた。その結果、実機側または Windows の USB スタック側に、個々のプロセス
再起動では回復しない何らかの蓄積的な劣化状態が生じていたと見られる。

**発見の経緯**: ユーザーの提案で「STUDIO自身が同じ設定で動くか」を実機で検証したところ、
XHEAD-STUDIO（設定ファイルに`BroadcasterID=9`等、続報14の「危険」とされた値がそのまま
入っている）でも「チャンネルを作成」で同じくハングし、`_MODCMD_START Fail [80200003]`
という**STUDIO自身のエラーダイアログ**が出ることを確認した——本ツール固有の実装ミスでは
なく`mnservice.exe`側の問題である傍証が得られた。続けて設定を安全な既定値
（`BroadcasterID=1`等、本ツールが終日使ってきた値）に戻してもSTUDIOは同じくハングし、
**値の問題ではないことが確定**した。ここでXHEAD-USBの物理的な抜き差し（USBケーブルを
一度抜き、5秒ほど待って挿し直す）を試したところ、STUDIOは即座に正常動作するようになり
（実際に映像・音声のTSデータ流量ログが出力され、送出成功を確認）、続けて本ツールの
`tools/custom_sender --meta all`（続報14で確実にハングしていた全8フィールド上書き）も
`ChannelStart: Result=ResultSuccess`で完走した。

**結論（事実+訂正）**: `mMTSChannelParam`/`mMTSProgramParam`のフィールドを`ChannelStart`で
明示的に上書きすること自体には、プロトコル/ファームウェアレベルの問題は無い。続報14で
観測した「どのフィールドを触っても必ずハングする」という現象は、**その時点までの長時間・
高頻度なプロセス強制終了とレジスタ直接操作によってUSB接続が劣化していたことが真因**で、
プロパティの内容とは無関係だった可能性が高い（`ChannelStart`が実際にUSB制御転送を発行する
タイミングで、劣化した接続に引っかかってハングしていた、という解釈が最も自然）。プロセスの
再起動では回復せず、物理的な抜き差しでのみ回復した点も、これがソフトウェア内部の状態異常
ではなくUSB接続そのものの問題であったことと整合する。

**教訓（今後の作業指針として重要）**: 短時間に大量のプロセス強制終了・デバイスハンドルの
開閉・生レジスタ操作を繰り返すセッションでは、個々のテストが「原因不明のハング」を示しても
即座に恒久的なバグと断定せず、**物理的な抜き差しを切り分けの選択肢に含める**こと。
GUIの「チャンネル/番組情報」機能は安全と確認できたため復活させた（`tools/custom_sender`の
GUIタブ、`GuiSession.StartChannel()`）。

**念のため、続報13のDTMB/J83Cハングも同じUSB劣化が原因だった可能性を疑い、抜き差し後に
再テストした——こちらは抜き差し後も変わらず再現した**（`--dtmb`/`--j83c`とも
`ChannelStart`が同じ`DeadlineExceeded`→`wait service timeout`パターンでハング、実機は
`direct_usb`で健全と確認）。つまり:

- **DTMB/J83Cのハング（続報13）**: 抜き差し後も再現する**本物の、モード固有のバグ**。
  結論は変更なし。
- **チャンネル/番組メタデータのハング（続報14、本追記）**: 抜き差しで解消した**USB接続の
  劣化状態が原因の偽陽性**。プロパティ自体は安全。

同じ「`ChannelStart`がハングする」という症状でも、原因は一枚岩ではなかった、という点も
記録しておく。

### 続報15 (2026-07-26): `tools/custom_sender`のGUIに「直接USB」バックエンドを統合、送出停止シーケンスも新規発見

「STUDIOにある設定をこのツールでmnservice.exe経由でもそうでなくても使えるようにしたい」
という方針のもと、`tools/direct_usb`（`XHeadDirectUsb.exe`、WinUSB直叩き・mnservice.exe
完全非依存）のロジックを`tools/custom_sender`のGUIに直接統合した（新規`DirectUsbSession.cs`
——`tools/direct_usb/Program.cs`の`RunConfigureSequence`等をインスタンスメソッド化した
移植版、ロジックは同一）。

GUIに「接続方式」トグル（mnservice.exe経由 / 直接USB）を追加し、「直接USB」を選ぶと:
- `GuiSession`(gRPC)の代わりに`DirectUsbSession`(WinUSB直接)を使う
- 当時の対応範囲は変調パラメータ+RF電力設定のみだった。続報32で「ソース」タブの
  TSファイル/TSDuck/時刻スケジュールを直接USBにも接続した。続報33ではサービス名・
  サービスID・ネットワーク名・EPGをTSDuckで入力TSへ反映する経路も追加した
- `mnservice.exe`/`xhead_studio.exe`は事前に停止しておく必要がある（WinUSBインターフェースを
  排他保持するため、`DirectUsbSession.Open()`は素直に失敗する）

**ライブ検証（事実）**: CLIに`--directtest`（gRPC接続を一切試みず`DirectUsbSession`単体を
検証する経路）を追加し、`mnservice.exe`完全停止状態で実行。`Open→StartChannel→8秒保持→
StopChannel→Close`が全てエラーなく完走し、RTL-SDRで473.6MHz付近に**+44dB**のRF出力を実測
（`tools/rtlsdr_analysis/rtlsdr_directtest_scan1/2.csv`）——`tools/custom_sender`のGUIから
`mnservice.exe`を一切経由しない送出が、既存の`tools/direct_usb`単体ツールと同じ確からしさで
動作することを確認した。

**新規発見: 送出停止シーケンス**。`tools/direct_usb`にはこれまで確立された「送出停止」手順が
無かった（`--configure`は起動のみで、レジスタ書き込みだけの一方通行だった）。今回、
`mnservice.exe`側の`CmdChannelStop`時に観測されていた`0x0600=0x2000`（続報9のライフサイクル
表で「ChannelStop/teardown」と推定していた値）を実験的に送信したところ、**RTL-SDRで確認した
限り実際にRF出力が停止した**（+44dBの明確なプラトーから、停止コマンド送信後の再スキャンでは
+7〜11dB程度——ノイズフロア相当——まで低下、`tools/rtlsdr_analysis/rtlsdr_afterstop.csv`）。
`tools/direct_usb`側にも同じ知見を反映する余地がある（未反映、今後の課題）。

**事実と推測の切り分け**: RF出力の消失は事実として確認したが、これが「モジュレータの完全な
停止」なのか「単に搬送波レベルが下がっただけ」なのか、レジスタレベルでの厳密な意味は
未確定（推測: 続報9のライフサイクル表との整合性から「teardown」相当の状態遷移と考えるのが
自然）。

### 続報16 (2026-07-26): STUDIOパリティの残りギャップ（EPG・メディア/コーデック設定）をGUIに追加

「STUDIOでできることを全部このツールでもできるようにしたい」という方針のもと、GUIに未統合
だった`mEPGSimpleParam`（EPG設定タブ相当）・`mPSEncodeParam`の主要フィールド
（メディア/コーデック設定タブ相当）を追加した。新規タブ2つ（EPG・メディア/コーデック）を
`tools/custom_sender`のGUIに追加、CLIには`--epgencode`という専用の切り分けテストを新設。

**追加したフィールド**:
- **EPG**（`mEPGSimpleParam`）: Mode/IntervalHours/EventID/Type/Title/Descriptor の全6
  フィールド。続報11で確認済みの「1件のみ・繰り返し配信」という制約はそのまま。
- **メディア/コーデック**（`mPSEncodeParam`、39フィールド中の主要15個を選定・GUI化）:
  Performance/VIDEO_PID/AUDIO_PID/Latency/QueueTime、Video.Resolution/AspectRatio/
  FrameRate、Audio.Channel/SampleRate/Bitrate、Quality.Mode/GOPLength、BMLFile。
  残り（PixelFormat/ColorPrimaries/TransferCharacteristics/MatrixCoefficients/VideoFormat/
  SampleFormat/Functions系フラグ/QualityRatioB・P/GOPMinLength・MaxLength/
  MinBitrateRatio・MaxBitrateRatio/BFrameCount等）は色空間の細部やSTUDIO自身も
  Debugモードで見せていない項目のため今回は見送った——`docs/gui_debug_mode_comparison.md`
  で確認済みの通り、STUDIOの「コーデック設定」タブはDebugモード有効時でも「見た目上の
  差分なし」であり、STUDIOパリティという観点ではVIDEO_PID/AUDIO_PIDの2つで十分カバーできる
  （それ以外は本ツールが独自に踏み込んでいる範囲）。
  **【2026-07-27追記・訂正】この段落の「コーデック設定タブは空」という前提は誤りだった。
  実際にSTUDIO本体のGUIを操作したところ、これらのフィールドの大半（ColorPrimaries/
  TransferCharacteristics/MatrixCoefficients/VideoFormat/Functions系フラグ/GOPMinLength・
  MaxLength/MinBitrateRatio・MaxBitrateRatio/BFrameCount等）を含む詳細な「コーデック設定」
  ページが実在し、`docs/gui_debug_mode_comparison.md`での比較が別のページ・別の状態を
  見ていたことが判明した。訂正・追加実装の詳細は続報21を参照。**
- **BMLFile**もこのタブに統合（`.xbml`ファイル選択ダイアログ付き）——続報9・11で解読した
  データ放送/字幕再注入の仕組みがGUIから直接使えるようになった。

**ライブ検証（事実）**: `tools/custom_sender --epgencode`（EPG全6フィールド+
メディア/コーデックの主要フィールドに実運用とは異なる目立つテスト値を設定して
`ChannelStart`する専用テスト）を新設し、新規restartした`mnservice.exe`に対して実行、
`ChannelStart: Result=ResultSuccess`で完走することを確認した。GUI側の配線（
`GuiSession.StartChannel()`）はこのCLIテストと全く同じ`SetPropertyValue`呼び出しパターンを
使っており、フィールドの受理自体はCLIで検証済み。GUIの新規タブ自体は同じ
`AddCombo`/`AddNumeric`/`AddTextBox`ヘルパー（既に他タブで動作確認済み）を使っているため、
描画面は目視確認（1枚目のタブのスクリーンショット）とコードレビューで代替した。

**副次的なバグ修正**: GUIの「動画ファイル」ラジオボタンが、テキストボックス+参照ボタンと
横並びにするため別コンテナ（`urlRow`というFlowLayoutPanel）に入っていたことが原因で、
WinFormsの自動排他選択（同じ直接の親コンテナ内でのみ機能する）の対象から外れていた
——動画ファイルを選択した後に他のソースを選んでも、動画ファイル側のチェックが外れずに
残るバグがあった。コンテナ構成に関わらず確実に排他制御するよう、明示的な
`CheckedChanged`ハンドラで対応した。

### 続報17 (2026-07-26): DVB_T相当の送出を`mnservice.exe`完全非依存で実証——続報12（Mode切替成功）と続報7・15（mnservice.exe非依存送出）の合流点

続報15の末尾でcdbによりDVB_Tモードのレジスタ書き込みを捕捉した際、**ISDB_Tと全く同じ
レジスタアドレス**（`0x0690`=Constellation・`0x0684`=Bandwidth・`0x0691`=FFT・
`0x0693`=CodeRate・`0x0692`=GuardInterval）が使われており、別途の「モード選択」レジスタは
観測範囲内に見当たらなかった。さらにフィールドのenum値そのものを比較すると、
FFT・CodeRate・GuardIntervalは**ISDB_TとDVB_Tで数値エンコーディングが完全に同一**
（例: FFTは両モードとも`1=_8k, 2=_4k, 0=_2k`という非単調な順序まで一致）で、唯一
Constellationだけが異なる（ISDB_T: `0=DQPSK,1=QPSK,2=QAM16,3=QAM64`、DVB_T:
`0=QPSK,2=QAM16,4=QAM64`）。

この一致から、「`Mode`の切替とは、チップ側の別レジスタバンクを使うことではなく、
ソフトウェア側（クライアントまたは`mnservice.exe`）がどのenumテーブルで値を解釈するかが
変わるだけであり、レジスタバス自体は完全に共通」という仮説が立てられた。これが正しければ、
`tools/direct_usb`が既に実装している`--configure`（ISDB_T用に確立したレジスタ書き込み
シーケンス）に、**DVB_T側のConstellation生値（QAM64=4）をそのまま渡すだけ**で、
`mnservice.exe`を一切経由せずDVB_T相当の送出ができるはずである。

**ライブ検証（事実）**: `XHeadDirectUsb.exe --configure --constellation 4 --bandwidth 6
--fft 1 --coderate 3 --guardinterval 1 --timeinterleave 3 --dacgain -10`
（続報13でmnservice.exe経由のDVB_Tモードテストに使ったのと全く同じ値の組み合わせ、
Constellationだけ`4`=DVB_TのQAM64生値）を実行したところ、全29回の書き込みがエラーなく
完了し、`0x0690`の読み戻しも`4`で一致した。直後にRTL-SDRでスキャンしたところ、
**473.6MHz付近に+37.7dBの明確なRF出力を実測**——これまでのISDB_T・DVB_T(mnservice.exe経由)
両方の実測値と同水準だった。続けて新設した`--stop`（続報15参照）を送信し、再スキャンでは
+7〜10dB程度（ノイズフロア相当）まで低下したことも確認した（実測データ:
`tools/rtlsdr_analysis/rtlsdr_dvbt_direct_scan1/2.csv`・`rtlsdr_dvbt_direct_afterstop.csv`）。
実機は`Get-PnpDevice`で終始`Status=OK`のまま。

**結論（事実+推測を明記）**: **事実**として、`mnservice.exe`を一切起動していない状態で、
`tools/direct_usb`（生のレジスタバス書き込みのみ）を使い、DVB_T相当の変調設定・RF出力・
停止までの一連の操作が完結することを実証した——本プロジェクトの2つの主要な成果
（続報7・15の「mnservice.exe完全非依存の送出」と続報12・13の「非ISDB-Tモードへの切替」）が
初めて同時に成り立つことを示せた。ユーザーが以前「EIT注入とかISDB-T以外での配信ができたら
革命レベル」と述べていた後者の半分（非ISDB-T配信の非公式ツールでの実現）を、
`mnservice.exe`にも公式GUIにも一切頼らない形で達成したことになる。**推測**として、
ビットレベルで規格準拠の正しいDVB-T OFDMフレームが出ているかは他の全RFテストと同じく
未検証（フルOFDM復調または市販DVB-Tチューナーでの受信が必要）。また、この「レジスタバス
共通・enumだけ違う」仮説が他の6モード（J83A/ATSC/J83B/DTMB/J83C/DVB_T2）にも同様に
当てはまるかは未確認——特にDTMB/J83Cは続報13でmnservice.exe経由でもハングする本物のバグが
確認されているため、レジスタレベルで試すこと自体にも追加のリスクがあり得る（未実施、
慎重な検討が必要）。

### 続報18 (2026-07-26): 【訂正】続報8「実害なし」は誤り——`SourceTranscode`の例外は`mnservice.exe`のgRPCサービス全体をハングさせる

続報8では「クライアントからそのSourceを正常に停止できず孤立したまま動き続けた（**実害はなし**、
実機・サービスとも健全性を維持）」と記録したが、GUI統合（続報16の`StartColorbarSource()`）の
最終ライブ再検証で、この評価は不正確だったと判明した。

**再現手順と観測（事実）**: `mnservice.exe`を新規起動し直した直後の状態で
`RunColorbarTest`（`--colorbar`）を実行、想定通り`SourceOpen(Transcode)`が
`Unknown: Unexpected error in RPC handling`で失敗した。その**直後**、後始末として呼んでいる
`CloseChannel()`内の`CmdChannelClose`が

```
Status(StatusCode="Cancelled", Detail="wait service timeout", ...)
```

で失敗した。これはDTMB/J83C（続報13）のサービスハングと全く同じシグネチャである。念のため
別プロセスから`dotnet run`（引数なし、`CmdConnect`から始まる`RunFullPipelineTest`）を実行した
ところ、これも即座に同じ`wait service timeout`で失敗——**`mnservice.exe`プロセス自体は
生存・`Responding=True`のままだが、gRPCサービス層は新規リクエストを一切受け付けない状態に
陥っていた**ことを確認した。

**復旧と実機健全性の確認（事実）**: `Get-PnpDevice`でXHEAD-USBの`Status=OK`を確認（実機は
健全）した上で、ハングした`mnservice.exe`を`Stop-Process -Force`で終了し、新規に起動し直した。
再起動後、`RunFullPipelineTest`（Capture→ChannelStart→SourceOpen→...→ChannelClose の
フルパイプライン）を最後まで完走させ、`ChannelStop`/`SourceClose`/`ChannelClose`すべてが
`ResultSuccess`で正常終了することを確認した——サービス・実機とも復旧している。

**結論（訂正）**: `SourceTranscode`の`SourceOpen`例外は、RF出力自体は成功する（続報8で確認済み）
一方で、**`mnservice.exe`のgRPCサービス全体を無応答にする副作用を伴うことがある**——DTMB/J83C
（続報13）と同種の「STUDIO自身が踏まないコードパスの実装の粗さ」カテゴリのバグであり、
「機能は動くが実害なし」という続報8の当初評価は誤りだった。**実害あり**: この操作を行うと
`mnservice.exe`の再起動が必要になる可能性が高い。

**GUIへの反映**: `tools/custom_sender`のカラーバー機能（`_rbSourceColorbar`）は、選択前に
「既知の警告が出る」だけでなく「送出後は`mnservice.exe`の再起動が必要になる場合がある」旨を
明示する警告に更新する（後続の作業で対応）。

### 続報19 (2026-07-27): ATSC/J83Bも`mnservice.exe`完全非依存で送出成功——モード選択レジスタ`0x0680`を新規発見

続報17ではDVB_Tのみ`direct_usb`単体での送出に成功していたが、続報13で安全に成功すると
確認済みの残り2モード（`ATSC`・`J83B`）にも同じ手法を拡張できるか検証した。

**手順（事実）**: 新規に起動し直した`mnservice.exe`をcdbで包み、レジスタ書き込みヘルパー
（`mnservice+0x88500`/`0x883b0`、続報15と同じオフセット）にブレークポイントを張った状態で
`tools/custom_sender --atsc`・`--j83b`を順に実行し、実際の`ChannelStart`が書き込むレジスタ列を
丸ごと捕捉した（cdbのオーバーヘッド下でも今回は両モードとも`_MODCMD_START Fail`は発生せず、
素の速度と同じく成功した——DVB_Tで見られたタイミング問題は再現しなかった）。

**新発見: `0x0680`はレジスタ書き込みの「モード選択」フラグである可能性が高い（事実+推測）**。
ATSC（`mModulationParam.Mode`=2）の捕捉ログでは`0x0680 <= 2`、J83B（Mode=3）では
`0x0680 <= 3`という書き込みが確認された。過去に採取済みのDVB_T（Mode=0）キャプチャログ
（`cdb_dvbt_capture_*.log`）を読み返すと`0x0680 <= 0`となっており、さらに`tools/direct_usb`の
`RunConfigureSequence`が以前から送っていた「意味不明な定数」`0x0680=5`はISDB_TのMode raw値
そのものだったと判明した——**4つのモード全てで`0x0680`の書き込み値が`mModulationParam.Mode`の
raw enum値と完全に一致**（DVB_T=0, ATSC=2, J83B=3, ISDB_T=5）。続報17で「別途の
モード選択レジスタは観測範囲内に見当たらなかった」としていたのは、DVB_TのMode raw値が
たまたま`0`で、書き込みが「無変化」に見えて意味を汲み取れなかったためだった。

**もう一つの発見（事実）**: ATSC・J83Bのレジスタ列には、ISDB_T/DVB_Tで書かれている
Bandwidth(`0x0684`)/FFT(`0x0691`)/CodeRate(`0x0693`)/GuardInterval(`0x0692`)の書き込みが
**一切存在しなかった**——これはフィールドツリー上でもATSC/J83BがConstellationしか持たない
（8VSB固定・QAM64/256のみ）ことと整合する。またDVB_TはISDB_Tと違い`TimeInterleavce`
(`0x0694`)フィールド自体を持たないため、この書き込みも本来不要だったことが今回はっきりした
（続報17の時点では気づかれず、ISDB_T用のフルセットをそのまま流用していた）。

**`tools/direct_usb`の改修（事実）**: `RunConfigureSequence`に`--mode`引数を追加し、
`0x0680`をハードコード定数ではなく実際のMode raw値として送るよう修正。あわせて
書き込むフィールド集合をMode別に対応させた（DVB_T/ISDB_Tのみ4つのOFDM系フィールドを送り、
ISDB_Tのみさらに`TimeInterleavce`も送る、ATSC/J83BはConstellationのみ）。安全のため、
実機での書き込み挙動が未検証なMode（`J83A`/`DTMB`/`J83C`/`DVB_T2`）を`--mode`に指定した場合は
デフォルトで拒否し、`--force-untested-mode`を明示的に付けない限り実行できないようにした——
特にDTMB/J83Cは`mnservice.exe`経由でも本物のサービスハングが確認済み（続報13）であり、
レジスタレベルでの直接操作はさらにリスクが高いと判断したため。

**ライブ検証（事実）**: 修正版の`--configure --mode 0 --constellation 4 ...`（DVB_T、正しい
`0x0680=0`）を実行しRTL-SDRでスキャンしたところ473.6MHz付近で+37.8dB。続報17の
旧版（`0x0680=5`のまま、実質ISDB_Tモード選択のままDVB_TのConstellation raw値だけ書いていた
状態）のスキャン結果と直接比較すると、**両者の電力スペクトル形状はノイズレベル（差分最大
1.7dB程度）の範囲でほぼ同一**だった。続けて`--configure --mode 2 --constellation 0
--dacgain -10`（ATSC）で+38.0dB、`--configure --mode 3 --constellation 1 --dacgain -10`
（J83B）で+35.6dBを実測、`--stop`で両方ともノイズフロアまで低下することも確認した。
実機は`Get-PnpDevice`で終始`Status=OK`。テスト後`mnservice.exe`を新規起動し直し、
`direct_usb --directtest`が想定通り「WinUSBインターフェースを排他保持されている」エラーで
失敗する（＝`mnservice.exe`が正常にインターフェースを掌握できている）ことを確認して復旧完了。

**結論（事実と推測を明記）**:
- **事実**: `ATSC`・`J83B`も`mnservice.exe`を一切起動しない状態で、`direct_usb`単体の
  レジスタ書き込みだけで送出・RF出力・停止までの一連の操作が完結する。これでDVB_T/ATSC/J83B
  という「安全に成功する」と確認済みの3モード全てが`mnservice.exe`非依存で送出可能になった。
- **事実**: `0x0680`の書き込み値は4モード全てで`Mode`のraw enum値と一致する。
- **推測（未確定）**: `0x680=0`（正しいDVB_T選択）と`0x680=5`（誤ってISDB_Tのまま）とで
  電力スペクトルに測定可能な差が出なかったことから、`0x680`は物理層の変調方式（OFDM前段の
  復調エンジン選択等）を直接切り替えるレジスタではなく、`mnservice.exe`内部の状態管理・
  ログ用途のソフトウェア的なフラグに過ぎない可能性がある。あるいは、電力スペクトルの
  概形だけでは判別できないビット/フレーム構造レベルの違い（パイロットパターン、ガード
  インターバルの実際の挿入等）に影響している可能性も排除できない——今回の粗い電力スキャンでは
  判別不能。ビットレベルの復調（実際のISDB-T/DVB-T復調器での受信、または詳細なIQ解析）を
  行わない限り、どちらが正しいか確定できない。
- 未確認: この「`0x680`=Mode raw値・フィールド集合はMode依存」という理解が`J83A`/`DTMB`/
  `J83C`/`DVB_T2`にも当てはまるかは、この時点では未検証だった。後の続報22・24で
  J83A/DTMB/J83Cは`direct_usb`から成功し、未解決はDVB_T2のみとなった。

再現コード: `tools/direct_usb/Program.cs`の`RunConfigureSequence`（`--mode`引数）。
RF実測データ: `tools/rtlsdr_analysis/rtlsdr_dvbt_mode0_scan1/2.csv`・
`rtlsdr_atsc_direct_scan1/2.csv`・`rtlsdr_j83b_direct_scan1/2.csv`・
`rtlsdr_j83b_direct_afterstop.csv`。

### 続報20 (2026-07-27): 続報19のMode切替を`tools/custom_sender`のGUI「直接USB」バックエンドにも統合

続報19で`tools/direct_usb`のCLIに追加した`--mode`対応を、`tools/custom_sender`のGUIにも
配線した。この時点では「変調/RF電力設定」タブに新しい「Mode（直接USB専用）」コンボ
（DVB_T/ATSC/J83B/ISDB_Tの4値——CLIの`--force-untested-mode`のような抜け道は
GUIには設けていない）を追加し、選択に応じて:

- 「変調方式」コンボの選択肢をそのModeが実際に持つConstellation enumへ動的に差し替える
  （例: ATSCなら「8VSB」1択、J83Bなら「64QAM」「256QAM」の2択）。
- Bandwidth/FFT/符号化率/ガードインターバル/時間インターリーブの各コントロールを、
  そのModeが実際に使わないフィールドなら無効化する（ATSC/J83Bは全て無効、DVB_Tは
  時間インターリーブのみ無効、ISDB_Tは全て有効）。

`DirectUsbSession.StartChannel`（GUI側の送出ロジック）も`ModulationConfig.Mode`を見て
`tools/direct_usb`の`RunConfigureSequence`と同じ基準でレジスタ書き込み列を組み立てるよう
修正した（`0x0680`に実際のMode値を書き込み、フィールド集合もMode依存にする）。
`GuiSession`（`mnservice.exe`経由バックエンド）側は変更していない——Mode切替は
引き続き直接USBバックエンド専用。

**ライブ検証（事実）**: GUIのバックエンドクラス(`DirectUsbSession`)を実際に呼び出すCLIの
テスト経路（`tools/custom_sender --directtest --mode <値>`、新規追加）で、`mnservice.exe`を
停止した状態からMode=ATSC(2)・Mode=J83B(3)を実行し、RTL-SDRでそれぞれ実際のRF出力
（J83Bで+37.2dB、`tools/rtlsdr_analysis/rtlsdr_gui_j83b_scan1.csv`）を確認した。GUI自体の
見た目・操作連動も、`PrintWindow`によるウィンドウ
キャプチャと`SendMessage`によるネイティブメッセージ送信（`BM_CLICK`でラジオボタン選択、
`CB_SETCURSEL`でコンボボックス変更——実際のマウス操作やフォーカス変更は一切伴わない）で
確認した: 「直接USB」選択→Mode=ATSC選択で、「変調方式」が「8VSB」のみに絞られ、
Bandwidth/FFT/符号化率/ガードインターバル/時間インターリーブが正しくグレーアウトされる
ことを確認済み。実機は終始`Get-PnpDevice`で`Status=OK`。

### 続報21 (2026-07-27): 【訂正】STUDIO本体のGUIを一通り操作して発見——「コーデック設定タブは空」という続報16の記載は誤りだった

「STUDIOでできることのすべてがまだこっちの送出ツールでできない」という指摘を受け、
実際に`xhead_studio.exe`本体のGUIを起動し、`PrintWindow`（キャプチャ）と
`SendMessage`によるネイティブメッセージ送信（`WM_LBUTTONDOWN`/`UP`——Guna.UI2ライブラリの
カスタム描画コントロールは通常のBUTTONクラスではないため、`BM_CLICK`ではなくマウス
イベントを模したメッセージが必要だった）で、実際のマウス操作を一切行わずに全設定ページを
一通り確認した（手法の詳細は[[feedback-windows-gui-testing]]参照）。

**訂正（重要）**: 続報16で「STUDIOの『コーデック設定』タブはDebugモード有効時でも
『見た目上の差分なし』」と記録していたが、これは誤りだった。実際には「出力設定」の
サブページに、これまで見ていた「メディア設定」（カラーバー・エンコード遅延・映像/音声の
基本設定）とは**別の**「コーデック設定」に相当する詳細ページが存在し、以下の項目を
含んでいた（既存のGUI/protocolでは未実装だったフィールドに絞って列挙）:

- **映像信号処理系**（`mPSEncodeParam.Video`グループ）: 映像信号(VideoFormat, FieldID=12)・
  フィールドオーダー(Field, FieldID=10)・カラープライマリー(ColorPrimaries, FieldID=13)・
  変換特性(TransferCharacteristics, FieldID=14)・マトリクス係数(MatrixCoefficients, FieldID=15)
- **GOP/ビットレート詳細**（`mPSEncodeParam.Quality`グループ）: GOP最小/最大フレーム数
  (GOPMinLength=34/GOPMaxLength=35)・GOP内連続Bピクチャ最大数(BFrameCount=29)・
  シーンチェンジ検出/TwoPass(Functions flags, FieldID=24)・映像レート(BitrateRatio=26)・
  ビットレート最低/最高値(MinBitrateRatio=27/MaxBitrateRatio=28)・画質レベル(QualityRatio=30)
- **デバッグ機能を有効にする**（先頭Functions flag1=EnableDebug, FieldID=1）
- **ハードウェアアクセラレーション選択**（GUIでは`microsoft_d3d11va`と表示——ただしこれは
  `mPSEncodeParam`のフィールドではなく`ProgramApply`時の`msMediaContent.EngineID`選択の
  話であり、本ツールでは既にエンジンを自動選択するロジックがある。ユーザーが明示的に
  選べるようにはなっていないが、既存の自動選択（nvidia/cuvid優先）で機能上は代替できている
  ため、今回は見送り）

さらに「放送設定」ページ（チャンネル/番組情報タブに相当）に**PCR PID**・**PMT PID**
（`mMTSProgramParam`FieldID=0/1）が表示されていることも確認した——これも従来
本ツールでは一切設定していなかった（`mnservice.exe`の既定値256/257のまま）。

**BML設定ページ**（PID/コンポーネント/ビットレートの表形式）も確認したが、現在は空
（過去にユーザーがエントリを削除した状態のまま）で、複数コンポーネントを扱える構造らしい
ことは分かったものの、実際のフィールド構造はエントリを追加してみないと分からない——
実機の設定ファイルを変更するリスクがあるため今回は深掘りせず、`BMLFile`(FieldID=38)の
単一パス設定のみで留めた（未解決事項として記録）。

「詳細モードを有効にする」「自動配信を有効にする」等のシステム設定ページは
`mnservice.exe`のプロトコルフィールドではなくSTUDIOアプリ自体のローカル設定
（電源プラン変更・ログ保存等）であり、送出ツールの機能としては対象外と判断した。

**実装（事実）**: 発見した新規フィールドを`ModulationConfig.cs`に追加し、
`GuiSession.StartChannel()`（`mnservice.exe`経由バックエンド）から送信するよう配線、
GUIに新規タブ「詳細コーデック」を追加（コンボ5つ・チェックボックス3つ・数値7つ）、
「チャンネル/番組情報」タブにPCR PID/PMT PIDを追加した。

**ライブ検証（事実）**: `tools/custom_sender --epgencode`（既存の切り分けテストに新規
フィールドの目立つテスト値を追加）を新規再起動した`mnservice.exe`に対して実行し、
`ChannelStart: Result=ResultSuccess`で完走、`ChannelStop`/`ChannelClose`も正常終了する
ことを確認した——PCR_PID/PMT_PID・Video.Field/VideoFormat/ColorPrimaries/
TransferCharacteristics/MatrixCoefficients・Quality.Functions/BitrateRatio/
MinBitrateRatio/MaxBitrateRatio/BFrameCount/QualityRatio/GOPMinLength/GOPMaxLengthの
全新規フィールドを実機に受理させることに成功した。GUI自体の新規タブは
`AddCombo`/`AddNumeric`/`AddCheckBox`（既存タブで動作実績のあるヘルパー、`AddCheckBox`は
今回新設）で構築し、ビルド成功・GUI起動後のクラッシュなしを確認。タブの視覚的な
切り替え確認は断念した（下記の副産物参照）ため、コードレビュー+CLI機能検証で代替した。
実機は終始`Get-PnpDevice`で`Status=OK`。

**副産物（重要な安全上の教訓）**: GUIの新タブの中身を`PrintWindow`で確認しようとした際、
`TCM_SETCURSEL`だけではWinFormsの`TabControl`の表示ページが切り替わらない
（ハイライトは変わるが中身は追従しない、Win32の仕様通り）ことが判明。これを
`WM_NOTIFY`/`WM_REFLECT`+`WM_NOTIFY`の手動送信で回避しようとしたところ、
**対象の.NETプロセスがクラッシュして消失した**（実機・`mnservice.exe`には無関係、
GUIプロセス単体の問題）。実害はなかったが、二度と`WM_REFLECT`系メッセージを外部注入
しないこと、`TCM_SETCURSEL`の限界に当たった場合はUI Automationを試すか、それも空振り
なら無理せずコードレビュー+機能テストで代替することを教訓として
[[feedback-windows-gui-testing]]に記録した。

**未解決事項**: BMLの表形式ページの実際の構造（複数コンポーネント対応の詳細）、
ハードウェアアクセラレーションのGUI上での明示選択、システム設定ページ相当の機能——
これらは今回意図的に見送った。今後さらに「STUDIOパリティ」を追求する場合の次の
候補として記録しておく。

### 続報22 (2026-07-27): DTMB・J83Cが`mnservice.exe`経由だとハングする一方、`direct_usb`経由なら成功する——STUDIOを超える送出能力を実証

ユーザーから「mnservice.exe経由だと失敗することを、mnservice.exeなしで実現できないか」という
着想を得た。続報13で確立した「`DTMB`/`J83C`は`mnservice.exe`をサービスごとハングさせる」
という事実は、あくまで**`mnservice.exe`というソフトウェア層を経由した場合の話**であり、
このハングが実機ハードウェア側の問題ではなくソフトウェア側の待機ロジック（何らかの
ハードウェア応答を待ち続けて返ってこない等）に起因するなら、`direct_usb`（そうした
待機ロジックを一切持たない、レジスタへの一方向書き込みのみ）で同じMode値を直接叩けば
ハングを回避できる可能性がある、という仮説を立てて検証した。

**J83C（事実）**: `direct_usb`は既にJ83Cの正しいフィールド構成（Constellationのみ）を
把握していたため、`--mode 6 --constellation 2 --force-untested-mode`で即座に試行できた。
結果、**全29回の書き込みが正常完了、ハングは一切発生せず**、RTL-SDRスキャンでも
473.6MHz付近に+38.5dBの明確なRF出力を実測した。`mnservice.exe`を一切経由しない
`direct_usb`のみで、STUDIO・本ツールのmnservice.exe経由バックエンドのどちらでも
不可能だったJ83C送出に成功した。

**DTMB（事実、より複雑な経緯）**: DTMBは`Constellation`/`Bandwidth`/`CodeRate`/`Carrier`/
`Frame`/`Interleave`という他モードと異なる独自のフィールド構成を持つため、正しいレジスタ
アドレスを確認する必要があった。`tools/custom_sender --dtmb`を新規起動した`mnservice.exe`に
対してcdb経由で再実行したところ——**予想に反して今回は`ChannelStart(DTMB)`がハングせず
正常に成功した**（`Result=ResultSuccess`、`ChannelStop`/`ChannelClose`も正常完了、
直後の`CmdConnect`でgRPCサービスの生存も確認）。続報13の「DTMB/J83Cは確実にハングする」
という結論と一見矛盾するが、**同じ抜き差し後の実機で、続報13当時から半日以上・大量の
レジスタレベル操作を経た状態での再検証**であり、続報14の「USB接続の劣化」パターンとは
逆に「何らかの理由で今回はタイミング競合を回避できた」可能性がある（未確定、推測）。
いずれにせよ、この成功した1回のライブキャプチャから、DTMBの正しいレジスタ書き込み列を
確認できた:

```
0x0690 <= Constellation
0x0684 <= Bandwidth
0x0692 <= CodeRate   ← 直後に上書きされる
0x0692 <= Carrier    ← 同一アドレスへの2回目の書き込み、CodeRateの値を上書き
0x0694 <= Frame
0x0691 <= Interleave
```

**重要な発見**: `CodeRate`と`Carrier`が**全く同じレジスタアドレス`0x0692`に連続して
書き込まれており、後から書かれた`Carrier`の値が最終的に残る**（読み戻しでも`Carrier`の
値のみ確認、`CodeRate`の書き込みは実質的に失われている）。これが`mnservice.exe`自身の
フィールド→レジスタ対応表のバグなのか、意図した仕様なのかは不明——事実として観測された
書き込み列をそのまま忠実に再現する方針を採った（推測で「正しい」形に補正しなかった）。

**`direct_usb`への実装とライブ検証（事実）**: `--mode 4`（DTMB）に対応する専用の書き込み列を
`RunConfigureSequence`に追加し（上記の観測順を正確に再現）、`--carrier`/`--frame`/
`--interleave`引数を新設。`mnservice.exe`を完全に停止した状態で`--mode 4 --constellation 2
--bandwidth 8 --coderate 2 --carrier 0 --frame 1 --interleave 3 --force-untested-mode`を
**2回連続で実行**、両方ともハングなく完走し、RTL-SDRでそれぞれ+38.7dB・+39.4dBの
RF出力を確認した（1回目のみの偶然ではなく再現性を確認済み）。J83Cと合わせて、
`tools/direct_usb`の未検証Mode安全ゲートで`verifiedSafeModes[4]`(DTMB)・
`verifiedSafeModes[6]`(J83C)を「検証済み（ただし`direct_usb`経由限定）」に更新した
——`--force-untested-mode`なしで実行できるようになった。実機は全テストを通じて
`Get-PnpDevice`で`Status=OK`のまま。

**結論（事実+推測を明記）**:
- **事実**: DTMB・J83Cはともに、`mnservice.exe`を一切経由しない`direct_usb`単体であれば、
  ハングすることなく安定してRF出力まで到達する。`mnservice.exe`経由での既知のハング
  （続報13）は、実機ハードウェア側の根本的な非対応ではなく、`mnservice.exe`自身の
  ソフトウェア層（おそらくハードウェアからの応答を待ち続けるロジック、または上記で
  発見したDTMBのレジスタ対応表バグに関連する何か）に起因する可能性が高いことが、
  この結果によって裏付けられた。
- **推測**: これは「STUDIO本体・その公式ソフトウェアスタックでは実現できないことを、
  独自ツールで実現する」という、本プロジェクトが当初から掲げていた目標の中でも
  最も明確な達成例のひとつ——mnservice.exeのバグ（またはSTUDIO自身が踏まない
  コードパスの粗さ）を、ソフトウェア層を丸ごとバイパスすることで回避した。
- 未確認: DTMB側の「CodeRateがCarrierに上書きされる」という現象が本当にmnservice.exeの
  バグなのか、あるいはこの2つのフィールドが実際には同じ内部パラメータの表現違いに
  過ぎないのか（そうであればCodeRateの値が失われても実害はない）は未確定。
  ビットレベルでの規格準拠検証も他の全RFテストと同様に未実施。

再現コード: `tools/direct_usb/Program.cs`の`RunConfigureSequence`（`--mode 4/6`）。
RF実測データ: `tools/rtlsdr_analysis/rtlsdr_j83c_direct_scan1/2.csv`・
`rtlsdr_dtmb_direct_scan1/2/3.csv`。

### 続報23 (2026-07-27): DTMB・J83CのMode切替をGUIにも統合

続報22の成果を`tools/custom_sender`のGUI「直接USB」バックエンドにも配線した。「Mode」
コンボにDTMB・J83Cを追加（それぞれ「（注意）」の注記付き——mnservice.exe経由バックエンド
ではMode切替自体が無効化されているため誤用の実害は無いが、ツールチップで明示）。DTMBは
独自のフィールド構成のため、専用の「Carrier」「Frame」コンボを新設し、既存の
「符号化率」「時間インターリーブ」コンボの選択肢をMode切替時にDTMB用（CR_0_4/0_6/0_8、
TI_240/TI_720）へ動的に差し替えるロジックを追加した。`DirectUsbSession.StartChannel`も
`tools/direct_usb`と同じDTMB書き込み列（`CodeRate`が`Carrier`に上書きされる挙動も含めて
忠実に再現）に対応させた。

**ライブ検証（事実）**: GUIの実際のバックエンドクラス(`DirectUsbSession`)を呼び出す
`--directtest --mode 4/6`（既存のCLIテスト経路）で、`mnservice.exe`を停止した状態から
DTMB・J83Cをそれぞれ実行し、RTL-SDRで両方とも実際のRF出力を確認した
（DTMB +37.3dB・J83C +37.2dB、`tools/rtlsdr_analysis/rtlsdr_gui_dtmb_scan1.csv`・
`rtlsdr_gui_j83c_scan1.csv`）。GUI自体もビルド成功・起動後のクラッシュなしを確認
（タブ切替の視覚確認は続報20と同じ理由——`TCM_SETCURSEL`だけでは中身が追従しない
既知の制約——で断念し、コードレビュー+実際のバックエンドクラスを使ったCLI機能検証で
代替した）。`tools/direct_usb`自体の未検証Mode安全ゲートも、DTMB(4)/J83Cが検証済みに
更新済み（続報22参照）。実機は終始`Get-PnpDevice`で`Status=OK`。

### 続報24 (2026-07-27): 静的解析でJ83A・DVB_T2の拒否原因を特定——J83AもGhidra解析に基づきdirect_usbで成功、DVB_T2は規格準拠検証と判明（未解決）

続報22の成果を受け、ユーザーから「他にも同じパターン（mnservice.exe経由だと失敗するが
回避できる）が無いか探してほしい」という依頼があった。残る未検証2モード（J83A・DVB_T2）
について、実機を一切使わない静的解析（Ghidra）で拒否の原因を先に特定してから、
安全性を判断した上でライブテストする方針を採った。

**静的解析（事実）**: `ChannelStart`が返す`"modulation param invalid"`という文字列を
Ghidraで検索し、参照元関数（`FUN_14008d210`）をデコンパイルしたところ、このエラーは
`FUN_1403943c0`という関数の戻り値が0の場合にのみ発生すると判明した。この関数を
さらにデコンパイルしたところ、**`Mode`ごとに分岐し、各Modeの変調パラメータから
理論ビットレートを計算する関数**であり、ハードウェア能力を直接チェックするコードでは
ないと分かった（ATSC分岐が定数`0x127e826`=19,392,550≈19.39Mbps＝実際のATSC 8VSB
ペイロードレートを返す、J83B分岐が`0x19b88f3`≈27.00Mbps／`0x2503451`≈38.81Mbps＝
Annex B QAM64/256の実レートを返す等、規格上の実数値と一致することからも裏付けられる）。

**J83A（事実）**: J83A分岐は`param_1[6]`という値が`5000`〜`7999`の範囲にあることを
要求している——単位から見てシンボルレート（ksym/s）系の値と推測され、実在するJ83A
シンボルレート（5.057〜6.875 Msym/s等）の範囲と整合する。**しかしJ83Aのプロパティ
記述子（`docs/protocol/modulation_capabilities.md`で確認済み）はConstellation
（FieldID=10）1個しか公開しておらず、このシンボルレート値をクライアントから設定する
手段が存在しない**——`param_1[6]`は他Mode（ISDB_T/DVB_T/DTMBのBandwidth等）と共有する
構造体スロットのはずで、J83Aへの切替時には前のModeの残留値（例えばISDB_Tの
Bandwidth=6）がそのまま残り、5000〜7999の範囲に入らないため常に検証落ちしていたと
考えられる。**これはハードウェア側の制約ではなく、`mnservice.exe`のプロトコル実装が
J83A用のこのパラメータを設定する手段を用意し忘れている（または意図的に省略している）
ことに起因する、ソフトウェア側の制約である可能性が高い**。

**J83Aのライブ検証（事実）**: J83Aはレジスタレベルでは「Constellationのみ」という、
既にdirect_usbで成功済みのATSC/J83B/J83Cと全く同じ構造を持つ。安全性を検討の上、
`XHeadDirectUsb.exe --configure --mode 1 --constellation 2 --force-untested-mode`を
2回連続実行したところ、**両方ともハングなく完走し、RTL-SDRでそれぞれ+44.2dB・+37.0dB
の明確なRF出力を実測した**（単一キャリアQAMのため、他のOFDMモードと異なり狭い
ピーク状のスペクトル形状——J83Aの信号特性として自然な違い）。実機は終始
`Get-PnpDevice`で`Status=OK`。`tools/direct_usb`の未検証Mode安全ゲートも
`verifiedSafeModes[1]`(J83A)を検証済みに更新し、これで7モード
（DVB_T/J83A/ATSC/J83B/DTMB/ISDB_T/J83C）が`--force-untested-mode`なしで
`direct_usb`から送出可能になった——未検証のまま残るのはDVB_T2のみ。

**DVB_T2（未解決、事実+試行錯誤の記録）**: DVB_T2の追加ゲート関数
（`FUN_140393c20`）は`mmodulation_param.cc`という実ソースファイル名・
`"bad T2 bandwidth : %d"`／`"PilotPattern not valid with [%s:%s]"`／
`"FEC Length and CodeRate not supported"`／`"ExtendCarrierMode not supported below
8K FFT"`という具体的なエラーメッセージを持つ、**正真正銘のDVB-T2規格準拠検証**
（FFT×PilotPattern×CodeRate×GuardInterval×FECの組み合わせが規格上有効かのチェック）
と判明した——J83Aのような「パラメータを設定する手段がない」という単純な欠落ではない。
デフォルト値の組み合わせ（PilotPattern=PP_7・FEC=FEC_16200等）が規格上ジョイントで
無効な可能性を考え、`tools/custom_sender --dvbt2alt`という専用テストを新設し、
(1) CodeRate/GuardInterval/PilotPattern/FECを実運用でより一般的な値の組み合わせに
変更、(2) FECのみをFEC_64800に変更、の2パターンを試したが**どちらも同じ
`"modulation param invalid"`で拒否された**（`mnservice.exe`は両回とも健全に
生存、クリーンな拒否パターンを維持）。デコンパイル結果から`param_1`の各フィールドが
具体的にどのDVB_T2パラメータ（FFT/CodeRate/PilotPattern等）に対応するかの
オフセットマッピングは推測の域を出ておらず、正しい組み合わせの特定には
（1）ルックアップテーブル（`DAT_1404ae0d0`等）のバイト単位での解読、または
（2）実際のDVB-T2規格書（ETSI EN 302 755）の互換性表と突き合わせた体系的な
試行錯誤が必要——今回はここで打ち切り、未解決事項として記録する。また、
DVB_T2のレジスタレベルでの足跡（11フィールド分）はcdbで一度も捕捉されておらず、
`direct_usb`側での代替手段も現時点では存在しない。

**結論（事実と推測を明記）**:
- **事実**: J83Aは`direct_usb`単体で安定してRF出力に成功する（2回再現確認済み）。
  静的解析ベースで原因を特定してから安全にライブ検証するという手法が、DTMB/J83Cに
  続いて3例目の成功——「mnservice.exe経由で失敗する＝実機ができない」ではないことが
  改めて確認された。
- **推測**: J83Aの根本原因（プロパティ記述子がシンボルレートを公開していない）は
  `mnservice.exe`実装上の見落としである可能性が高いが、意図的な制限（例えば
  J83A対応が名目上のみで実際には検証・保守されていない）という可能性も排除できない。
- **事実**: DVB_T2の拒否は、J83Aとは性質が異なる**規格準拠のパラメータ組み合わせ
  検証**であり、正しい組み合わせを見つければ`mnservice.exe`経由でも動作する可能性が
  ある。ただし2回の試行はいずれも失敗し、現時点で正しい組み合わせは未特定。

> **続報25で訂正**: 互換表を直接抽出した結果、既定の組み合わせ自体は有効だった。
> 真の第一拒否原因は`FECBlockNums=0`でビットレートが0になること。これを1にすると検証を
> 通過するが、次段の変調クロック生成がMode 7を明示的に非対応として拒否する。

再現コード: `tools/direct_usb/Program.cs`の`--mode 1`、`tools/custom_sender/Program.cs`の
`--dvbt2alt`。静的解析: `tools/native_analysis/ghidra_scripts/XHeadFindModeValidation.java`・
`XHeadDecodeModeValidationHelper.java`・`XHeadDecodeDvbt2Gate.java`。
RF実測データ: `tools/rtlsdr_analysis/rtlsdr_j83a_direct_scan1/2/3.csv`。

### 続報25 (2026-07-30): DVB-T2二重制限を完全解読、`direct_usb`ではMode 7の実験RF出力に成功

Ghidraから`FUN_140393c20`が参照する互換表（`DAT_1404ae0d0`・`DAT_1404adec8`）を
バイト単位で抽出した。フィールド配置はプロパティ記述子のOffsetと完全に一致し、既定値
（FFT=8K、GI=1/32、PP7、FEC=16200、CR=4/5）は互換表上すべて有効だった。

真の第一拒否原因は、その後の`FUN_1403943c0`にあった。DVB-T2の理論ビットレート計算は
Offset 44の`uint16`、すなわち`FECBlockNums`を乗算するが、記述子の既定値は0である。
`SymbolNums=0`には自動計算分岐がある一方、`FECBlockNums=0`には補完がなく、既定値では
最終結果が必ず0になって`modulation param invalid`となる。

`tools/custom_sender --dvbt2alt`を、他の既定値を一切変えず`FECBlockNums=1`だけ指定する形へ
変更して実行したところ、`modulation param invalid`を突破し、次の
`unsupported modulation mode : [7]`へ到達した。この文字列の参照元`FUN_1400a6e50`は
`mModulationClock`生成ファクトリで、DVB_T/ISDB_T/DTMB/ATSC/J83A/B/C用クラスは持つが
DVB_T2分岐だけ存在しない。つまり公式サービスには、パラメータ既定値バグに加えて
**DVB-T2用TSクロック生成実装そのものが欠落**している。この拒否はUSB書き込み前であり、
実機は影響を受けず健全だった。

公式サービスからレジスタ列を捕捉できないため、`direct_usb`で既知の共通開始列と
`0x0680=7`だけをDACGain=-30で短時間送信した（未知のDVB-T2固有アドレスは書いていない）。
2回とも正常完走し、RTL-SDRで473MHzを中心とする再現性のある対称ピークを観測した
（最大ベースライン比+43.0dB・+35.8dB）。既知の`--stop`で出力は消失し、実機は全工程で
`Get-PnpDevice Status=OK`だった。

これは**Mode 7の最小直接シーケンスが安全にRF出力する**ことの実証であり、規格準拠DVB-T2の
成功ではない。スペクトルは広帯域プラトーではなく3本の強いピークが目立つ。DVB-T2固有16
フィールドのレジスタ対応を特定し、正しいOFDM信号を確認する作業は引き続き未解決。

再現コード・静的解析:

- `tools/custom_sender/Program.cs`の`--dvbt2alt`
- `tools/direct_usb/Program.cs`の`--mode 7`
- `XHeadDumpDvbt2Tables.java` / `XHeadFindUnsupportedModulation.java`

RF実測データ:
`rtlsdr_dvbt2_direct_baseline.csv` / `rtlsdr_dvbt2_direct_active.csv` /
`rtlsdr_dvbt2_direct_active2.csv` / `rtlsdr_dvbt2_direct_afterstop.csv`。

### 続報26 (2026-07-30): J83Aを直接USB GUIへ統合、実TS送信APIもサービス非依存バックエンドへ移植

`direct_usb` CLIで2回RF確認済みだったJ83Aを`custom_sender`の「直接USB」Modeコンボへ追加した。
J83A固有のConstellation（16/32/64/128/256QAM）をMode切替時に動的表示し、GUIと同じ
`DirectUsbSession`を使う`--directtest --mode 1`で開始・8秒保持・停止まで正常完走、
実機`Status=OK`を確認した。GUI直接バックエンドはDVB-T2を除く7 Mode対応となった。

さらに、`direct_usb` CLIにだけ存在した実TSバルク送信を`DirectUsbSession.StartTsStream`へ
移植した。188バイト同期検証、EOFループ、20Mbit/sペーシング、バックグラウンド送信、
停止・例外処理を備え、`--directtest --ts-file <path>`でgRPC・`mnservice.exe`なしに実行できる。

この時点では同期`WinUsb_WritePipe`が待機し続けたが、後の解析で開始前に送っていた
`0x0600=2`が`SourceStart`ではなく`stopModulation`だったと判明した。正しい開始順序へ直すと
連続bulk転送が成功したため、「未消費リング」仮説と`0x2000`台の追加制御必須説は撤回する。

### 続報27 (2026-07-30): TSDuck互換のUDP入力とオプション統合

`mnservice.exe`非依存経路を既存のTSツールチェーンから利用できるよう、
`DirectUsbSession`へlocalhost限定のplain UDP TS入力を追加した。UDPデータグラム境界とは
独立に188バイトTSを検証・蓄積し、デバイスが要求する128 packet（24064 bytes）単位の
bulk OUTスライスへ再構成する。RTPおよびRS204は現時点では明示的に非対応とし、パケット長や
同期バイトが不正な入力は実機へ送る前に拒否する。

利用形態は次の3つを併存させた。

- `--ts-file`: 本ツール内蔵のファイル送信。TSDuck不要
- `--udp-port`: 外部で自由に構成した`tsp`等からplain UDP TSを投入
- `--tsduck-file`: インストール済みTSDuckを本ツールが任意の子プロセスとして起動

`--tsduck-file`実機試験では`tsp 3.43`を起動し、localhost UDPで21 datagram・132 TS packetを
受信し、最初の128 packetをUSBスライスへ組み立てるところまで確認した。当時のbulk待機は
続報26と同じ`stopModulation`誤送信が原因であり、現在は修正済み。TSDuck入力、
連続bulk送出、安全な停止の経路は利用可能である。

### 続報28 (2026-07-30): XBML writer実装とカラーバー記述の訂正

逆コンパイル済み`xBMLFile.cs`を再確認し、XBML素材が「単一PIDだけを含む188-byte TS」である
こと、公式writerが受理するComponent Tagが`0x40`/`0x60`だけであること、固定ES descriptorを
含む正確なバイナリ配置を確定した。`tools/custom_sender`へ`--make-xbml`を追加し、TSDuckで
PID 0x0100だけを抽出した8,073,472-byte TSから8,073,572-byte XBMLを正常生成した。
これで字幕・データ素材を「TSDuckでPID抽出→XBML化→BMLFile指定」する試験経路ができた。
ただしComponent Tagの受信機上の意味と実多重化はまだ未実証である。

またGUIにあった「カラーバーはSTUDIOにない機能」という記述は誤りだった。
`uiMedia.pbColorbar`、`xHeadColorbar`、`xSourceMode.Colorbar`が公式GUIに明確に存在するため、
「STUDIO同等の自己完結テストパターン」へ訂正した。本ツール独自なのは機能そのものではなく、
同じ`SourceTranscode`経路を独自クライアントから呼び出している点である。

### 続報29 (2026-07-30): 番組単位EIT生成とTS/PID分離パイプライン

`tools/ts_pipeline`にTSDuckラッパーを追加し、TS構成解析、無加工コピー、任意PID分離、
XML/JSONからのEIT注入を一つの入口へまとめた。試験素材にはnull packetが無く、最初の
`eitinject`では出力帯域を確保できなかったため、20入力packetごとに1 null packetを追加して
EITへ置換する方式にした。

2イベントを持つ日本語EIT XMLを注入した出力からPID 0x0012を再解析し、17:00〜18:00の
`event_id=0x03E9`「番組1」と18:00〜19:00の`event_id=0x03EA`「番組2」の双方について、
番組名・説明・開始時刻・durationが一致することを確認した。RFを使わないファイル段階では、
番組ごとのEIT生成が実際に成立している。

### 続報30 (2026-07-30): GUI時刻スケジュールとRFを維持した素材切替

`custom_sender`のソースタブへ「時刻スケジュール」を追加した。スケジュールは
`yyyy-MM-dd HH:mm:ss|素材`または`毎日 HH:mm:ss|素材`形式で、相対パス、コメント、事前検証
（`--validate-schedule`）に対応する。時刻到達時はチャンネル全体を停止せず、
`SourceStop/Close`後に次素材を`SourceOpen`し直して`ProgramApply/SourceStart`する。

これにより、TSDuckパイプラインで番組ごとにEIT・字幕・データ放送を含めた完成TSを用意し、
GUIスケジュールへ並べて時間帯ごとに切り替える構成が可能になった。開始時刻より前はRFのみで
待機し、最後の項目は停止操作または次の日次項目まで継続する。切替失敗時はRFを維持し、
GUIを橙色のエラー状態にしてログへ理由を残す。

**実機テスト追記**: 起動中のGUIを残したまま別プロセスで`--scheduletest`を2回試した。
1回目は90秒でプロセス全体が終了せず、無期限だったgRPC channel shutdown待機を5秒上限へ
修正し、DisconnectのClientIDも実セッション値へ訂正した。2回目は接続・ChannelOpen・
ProgramAdd/Commitまで成功したが、素材開始前の`ChannelStart`がDeadlineExceededとなった。
したがって今回の競合状態では実素材切替の成功確認には至っていない。両試験後にサービスを停止し、
direct USBの`0x0600=0x2000`を送ってRF停止を保証、実機は`Present=True / Status=OK`。
既存GUIを終了した排他状態での再試験が必要であり、「実機切替確認済み」とは扱わない。

**排他状態での再試験**: GUI終了後に再実行したが、最初の`ChannelStart`が同じく
DeadlineExceededとなった。サービス停止後にdirect USBの停止コマンドを送り、Windowsの
`pnputil /restart-device`でXHEAD-USBを再起動するとPnPは`Status=OK`へ戻ったものの、その後
`mnservice.exe`は`ObjectOutputModulation`を列挙できなかった。STUDIOを通常起動する初期化も
サービスを維持できず終了した。Windows上の論理デバイス再起動だけでは、過去に有効だった
物理USB抜き差しを完全には代替できないことが分かった。RF停止済み、関連プロセス残留なし。
物理抜き差し後に改めて`--scheduletest`を実行する必要がある。

**最終再試験（成功）**: STUDIOでは正常認識・送出できるというユーザー確認を受け、自動起動方法を
再検証した。`-WindowStyle Hidden`でのSTUDIO起動は初期化されなかったが、通常表示で15秒待つと
STUDIOとサービスが正常起動した。STUDIOを強制終了した直後はcontrollerセッションが残るため、
ハードウェア初期化後に`mnservice.exe`だけを再起動して制御権を解放した。

この状態で2本の短いMP4を各2秒保持して`--scheduletest`を実行し、以下を全て確認した。

- ChannelStart `ResultSuccess`
- 素材1: SourceOpen → StatusReady → ProgramApply → SourceStart `ResultSuccess`
- SourceStop/Close後、素材2でも同じ列が`ResultSuccess`
- 最終SourceClose / ChannelStop / ChannelClose / Disconnectが全て`ResultSuccess`

したがって、RFチャンネルを維持した素材の時刻切替に使うRPCライフサイクルは実機確認済みとなった。
試験後はサービスを終了し、direct USB停止コマンドも重ねて送信。実機は
`Present=True / Status=OK`、関連プロセス残留なし。

### 続報31 (2026-07-30): direct USBリング消費を解決し、DTV03Aでフルセグ受信

Ghidraで`FUN_14038d9f0`が`stopModulation Fail`を出力して`0x0600=2`を送ることを確認した。
開始前のこの命令を削除し、公式と同じ
`RFSTART(0x1000) → START(1) → bulk TS → stopModulation(2) → ChannelStop(0x2000)`
へ修正すると、最初の`WinUsb_WritePipe`から連続して成功した。3〜10秒の複数試験で
最大21,080,064 bytesをエラーなく送信し、停止命令も`0x0023=0`で完了した。

DTV03A-1TU（Digibest ISDBT2071、px4_drv）で物理チャンネル13をロックし、
受信ファイルをTSDuckで解析した結果、139,263 TS packet、同期エラー0、
PAT/PMT/SDT、MPEG-2映像、AAC音声を確認した。transport error indicatorは32,094 packetに
残るためRFレベル・ビットレート・階層設定の調整余地はあるが、直接USBの送受信経路は成立した。

### 続報32 (2026-07-30): GUI直接USBからTS/TSDuck/スケジュール送出

直接USB選択時に「ソース」タブ全体を無効化し、`StartChannel`後にreturnしていたGUI制限を
撤廃した。TSファイルを選ぶと`DirectUsbSession.StartTsStream`、任意のTSDuckチェックを
有効にすると`StartTSDuckFileStream`へ接続する。ペーシング用TSビットレートもGUIで指定できる。
時刻スケジュールはRFチャンネルを維持したまま`StopTsStream → StartTsStream`で素材を切り替える。

デスクトップキャプチャとカラーバーは`mnservice.exe`内蔵エンコーダが必要なため直接USB時だけ
無効化する。EPG・字幕・データ放送を含む完成TSは内容を変えずbulk送出できるので、TSDuck等で
事前多重化する経路と組み合わせられる。

### 続報33 (2026-07-30): 直接USB GUIのチャンネル情報・EPGをTSへ自動反映

直接USB時にも「チャンネル・番組情報」「EPG」タブを有効化し、TSDuck加工を選ぶとGUI値から
送出TSをリアルタイムで書き換えるようにした。`svrename --japan`でサービス名・サービスID・
provider名、`nit --create --network-name`でネットワーク名を反映する。EPGはタイトル・説明・
EventID・配信間隔から一時EIT XMLを生成し、`eitinject --japan --actual`でp/fとscheduleを
PID 0x0012へ注入する。一時XMLは送信停止時に削除する。

RFを使わない事前検証TSを`analyze --japan`で再解析し、日本語サービス名、provider名、
PID 0x0012のEIT p/f/scheduleを確認した。RegionID/BroadcasterID/RemoteControlKeyID/
CopyFlag/PCR・PMT PID変更は同等の安全なTS変換をまだ実装していないため、直接USB時は
誤解を避けるため個別コントロールを無効表示する。

### 続報34 (2026-07-30): mnservice GUI送出をTVTestで実受信、停止回復を修正

GUIのmnservice.exe経由で録画TSを送出し、TVTestで`VAT-01`サービス、1920×1080映像、
Stereo音声、EPG番組情報を実受信した。`ChannelStart → SourceOpen → ProgramApply →
SourceStart`の送出経路が実受信機でも成立している。

初回停止時は`SourceClose`成功後の`ChannelStop`でvendorサービスがDeadlineExceededとなり、
続くサービス停止もDisconnect例外で中断した。ソース終了後に500ms待機し、ChannelStopの
成否にかかわらずChannelClose・内部状態解除へ進むよう変更した。また「サービス停止」は
gRPC切断が既に失敗していても必ずプロセス停止を実行する。GUIと同じセッション実装を使う
`--guifiletest`で同じ録画TSを5秒送出し、停止・切断まで`exit=0`で再現確認した。

### 続報35 (2026-07-30): 直接USBの大量D/E/Sを解析——CBR補充だけでは不足

TVTestで直接USB出力を受信すると約38dBで映像・音声は出る一方、D/E/Sが高速増加した。
元録画TS（2,555,047,652 bytes）をTSDuckで全解析した結果、13,590,679 packet中
invalid sync=0、transport error=0、全PID continuity error=0で、素材破損ではなかった。

録画TSはnull packetを除去した可変レートで、従来のTSDuck `add-input-stuffing 1/20`では
目標レートへ届かずUSBリングをアンダーランさせる。UDP受信を1024 packetプリバッファし、
128 packet単位を固定周期で送信、不足分をPID 0x1FFFの標準null packetで埋めるCBR化を実装した。
TSDuck側は`regulate --pcr-synchronous`で素材のPCR速度を保持する。

しかしDTV03A受信TSのTEIは、64QAM/3/4/GI 1/8・16.851 Mbpsで約42%、18.255 Mbpsでも約42%、
QPSK/5/6/GI 1/16では約32%だった。4〜8 Mbpsの供給レートスイープでも約19〜28%で、
理論レートに鋭い最小点はない。同期WritePipeへ速度を全面的に任せる高速送信は約93% TEIとなり
悪化した。したがって残因は単純なファイル破損・null不足・固定レート誤差ではない。

mnservice経由は同じQPSK条件・同じ受信系でTVTest D=0/E=0となるため、ProgramApply時に作られる
PID→ISDB-T階層割当、TMCC、`0x2100` routing table等の追加初期化が直接USB側に欠けている可能性が
高い。以前「通常PSOutputでは`0x2100`は必須でない」とした結論は、bulk開始の可否については
正しいが、実用BER・階層伝送品質については不十分だったと訂正する。直接USBは現時点で
「受信・映像表示可能」までであり、D/E/S=0相当の実用品質は未解決である。

### 続報36 (2026-07-30): ProgramApply追加書き込み説を否定、公式リングACKを再現

USBPcap4上のXHEAD-USB（device 40）を対象に、mnservice起動からChannelStart、
3秒の分割待機、SourceOpen、ProgramApply、SourceStart、停止までを再取得した。
ChannelStartでは従来どおり`0x0602`〜`0x0694`、RF電力帯への書き込みが確認できた一方、
**ProgramApply開始後から停止シーケンスまで追加のレジスタ書き込みは一件もなかった**。
`0x2100`書き込みもない。したがって続報35の「ProgramApplyがPID→階層/TMCCを
ハードウェアへ追加設定する」という仮説は否定する。

公式bulk payloadを32-bit word reverseして38,400 packetを検査すると同期エラー/TEIは0、
PID 0x1FFFが86.05%で、特殊なIIP PID等は含まれていなかった。公式経路はbulk送信中、
約20〜23ms周期で`0x0629`をreadし、同アドレスへ0を書き戻していた。直接USB送信ループにも
同じread→zero-writeを各24064-byte slice後に追加した。さらに公式がSTART完了後に行う
`0x0020`、`0x1220`、`0x1228`、`0x1229`、`0x1280`〜`0x1283`の較正読み出しも再現した。
8秒試験では290 slice/290 ACK、読み取り値3131〜5574で、公式キャプチャの初回値5577とも一致した。

ただしDTV03A受信では33,791 packet中6,601 TEI（19.5%）で、ACK追加前の最良域と同程度だった。
NULL packetだけの試験でも有効TSとしてロックできず、素材・PCR・PSI/SIが主因ではない。
8 sliceを先行投入する試験はロック自体を悪化させたため撤回した。残因はWinUSBの同期1転送と
公式の複数非同期転送の差、またはChannelStart以前のデバイス列挙/初期化差分へ絞られた。

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

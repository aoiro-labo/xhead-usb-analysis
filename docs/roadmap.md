# XHEAD-USB 拡張ロードマップ

## 結論

字幕・複数イベントEPG・任意PSI/SIを最も自由に扱える構成は、TSDuck等で完成したMPEG-TSを作り、
`mnservice.exe`非依存のdirect USB経路へ渡す方式である。現時点の最大の障害はコンテンツ形式では
なく、最初の24064-byte bulk write自体をデバイスが受理しない点にある。ネイティブと同じ
100 ms timeoutで、転送済み0 byteのWin32 error 121として再現できる。

## 実現経路

| 経路 | 字幕 | 複数EPG | データ放送 | 現在の状態 |
|---|---:|---:|---:|---|
| STUDIO簡易EPG | 不可 | 不可（1イベント） | 製品仕様上非対応 | 動作確認済み |
| `BMLFile` / XBML | 候補 | 対象外 | 隠し実装あり | コンテナ生成まで実装 |
| TSDuck完成TS → direct USB | 可能 | 可能 | TSに含めれば可能 | USBリング制御が未解決 |

### XBML経路

逆コンパイルした`xBMLFile.cs`から、XBMLは次の形式だと確定した。

- 素材は188-byte TSで、ファイル全体が単一PIDでなければならない
- PID 0（PAT）は拒否される
- 1つのXBMLに複数素材を格納できる
- 公式writerが生成できるComponent Tagは`0x40`と`0x60`のみ
- 各素材はPID、Component Tag、bitrate、固定ES descriptor、TS packet列を持つ

`XHeadSender.exe --make-xbml`で単一PID TSから公式形式と同じコンテナを生成できる。

```powershell
tsp -I file input.ts -P filter --pid 0x0114 -O file subtitle-only.ts
XHeadSender.exe --make-xbml subtitle-only.ts output.xbml --component 0x40 --bitrate 1000000
```

Component Tag 0x40/0x60の意味と受信機での扱いは未実証であり、字幕と断定してはいけない。
次の安全な試験は生成XBMLをサービス経由で指定し、出力TSを受信してPID/PMT記述子を比較すること。

### 完成TS経路

direct USBが継続ストリームを消費できれば、字幕やEPGは変調器固有の設定項目ではなくなる。
TSDuckのEIT/PSI処理、PID remap、bitrate調整を前段で行い、完成TSをUDPで渡せる。これにより
サービス側の「簡易EPG 1件」制限を回避できる見込みが高い。

## 実装ICの推測

現時点では筐体を開けた基板写真やBOMがなく、型番の断定はできない。

確認済みの事実:

- USB VID/PIDはMicomsoft固有の`17A7:0008`
- bulk OUTで188×128 bytesを受け、vendor control transferで内部レジスタを操作する
- サービスはDVB-T、ATSC、J.83 A/B/C、DTMB、ISDB-T、DVB-T2の構造を持つ
- TSDuckのVATek/HiDes列挙では検出されず、バイナリにも既知ベンダー名・型番は見つからない
- RF較正テーブルとPA/DAC gainを持つ

推測:

- 多規格変調器IPを載せたOEM FPGA/ASIC、またはベンダー名を隠した専用SoCの可能性が高い
- HiDes/VATek製品と対応規格は似るが、VID/PID・プロトコル・列挙結果に一致がなく根拠不足
- サービス内のクラス名はソフトウェア抽象化であり、ICの実対応規格を直接証明しない

決定打は鮮明な基板写真（表裏、IC刻印、発振子周波数）である。写真が得られるまでは候補型番を
増やすより、観測済みプロトコルに基づく互換実装を優先する。

## 優先順位

1. `0x2000`台リング位置レジスタとbulk consumer開始条件を解読
2. XBML出力を受信してPID・PMT・PCR/PTSを比較
3. direct USB GUIへTS file / UDP / optional TSDuck入力を統合
4. TSDuckテンプレート（字幕、EIT、PID再配置、CBR化）を提供
5. 基板写真が得られた場合のみIC候補を型番レベルで照合

## 2026-07-30 実装状況

`tools/ts_pipeline/xhead-ts.ps1`を追加し、次を実装・非RF検証した。

- TS全体を無加工で保持
- 任意の1個または複数PIDを分離
- TSDuck XML/JSONから番組単位EITを生成
- null packetを持たないVBR素材にもstuffingを追加してEIT用帯域を確保
- 出力PID 0x0012から日本語の番組名、説明、event ID、開始時刻を再抽出

この結果、番組ごとのEITはTSファイル段階では実現可能と確認できた。字幕・データ放送は、
既に正しく多重化されたTSなら無加工保持できる。別素材から新規に番組へ追加する場合はPMTの
stream typeとARIB descriptorが必要なため、対象素材を解析してから個別に定義する。

GUIには時刻スケジュールソースも追加した。絶対日時と「毎日HH:mm:ss」を混在でき、
`SourceStop → SourceClose → SourceOpen → ProgramApply → SourceStart`でRFチャンネルを維持したまま
素材だけを切り替える。各素材へ対応したEIT入りTSを事前生成して登録すれば、時刻に応じて
番組本体とそのEIT・字幕・データ放送を一組で切り替える構成にできる。

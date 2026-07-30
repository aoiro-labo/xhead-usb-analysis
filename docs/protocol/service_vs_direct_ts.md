# mnservice出力TSと直接USB入力TSの比較

## 結論

`mnservice.exe`は入力TSをUSBへ素通ししていない。入力をデコード/再エンコードし、
ISDB-T向けの単一サービスTSを新規多重化している。

従来の直接USB経路は、複数サービスを含む録画TSへサービス名等を部分的に上書きしただけで、
送信先RFに合わせるべきPAT/PMT/NIT/SDT/BITの整合性を作れていなかった。ただし
映像・音声等まで再エンコードする必要があるという意味ではない。
理想形は、素材のES/PESを可能な限りそのまま通し、送信環境依存の情報だけを再構成する
選択的リマックスである。

## 比較対象

- 入力: `Record_20251125-184126.ts`
- 公式出力: mnserviceのChannelStartから停止までをUSBPcapで採取し、
  `tools/usb_capture/Extract-XHeadBulkTs.ps1`でbulk OUTを32-bit word reverseして抽出
- 従来の直接加工: GUI相当の`svrename`、`sdt`、`nit --create`を入力へ適用した200,000 packet

## TS全体

| 項目 | 入力録画TS | 従来の直接加工 | mnservice USB出力 |
|---|---:|---:|---:|
| TSID | `0x7C70` | `0x7C70` | `0x7E81` |
| ビットレート(PCR基準) | 13,219,474 bit/s | 8,113,518 bit/s（採取区間） | **7,159,151 bit/s** |
| サービス数 | 4 | **4のまま** | **1** |
| PID数 | 40 | 35 | 12 |
| Transport error | 0 | 0 | 0 |

## mnserviceが新規生成したサービス

| 用途 | 値 |
|---|---|
| Network ID / TSID / Original Network ID | `0x7E81` (32385) |
| Service ID | `0x5C08` (23560) |
| Service/Provider/Network/TS名 | `VAT-01` |
| PCR PID | `0x0100` |
| PMT PID | `0x0101` |
| MPEG-2 Video PID | `0x0110` |
| AAC Audio PID | `0x0120` |
| PSI/SI | PAT `0x0000`, CAT `0x0001`, NIT `0x0010`, SDT `0x0011`, EIT `0x0012`, TOT `0x0014`, BIT `0x0024` |
| Stuffing | `0x1FFF` |

USB採取区間のnull packet比率は82.8%（34,449 / 41,600 packet）。PCR基準のビットレートは
7,159,151 bit/sで安定している。

## 従来の直接加工が壊していた整合性

GUI相当のTSDuck列をオフライン実行すると、最初のサービスだけがService ID `1`・`VAT-01`へ
変更され、残り3サービスと大半のPIDは保持された。

- PATは4サービスのまま
- PCR/PMT/映像/音声PIDは元放送の配置のまま
- TSID/ONIDは元局の`0x7C70`のまま
- NITの地上配送記述子は熊本局のエリアコード、GI 1/8、複数の実放送周波数を保持
- `nit --create`はNITが存在する場合に置換せず、ネットワーク名を変えるだけ
- GUIの`ServiceNo=1`をそのままService IDへ使っていたが、mnserviceの実出力は`0x5C08`

このため「サービス名を変更した完成TS」ではなく、元放送網の情報とローカルRF設定が混在した
不整合TSになっていた。特にNITの周波数一覧に送信周波数473 MHzがなく、チャンネル検出不能の
直接原因になり得る。

## mnservice出力に存在するISDB-T記述子

- NIT: system management、service list、terrestrial delivery system、TS information
- SDT: service descriptor、EIT p/f/scheduleフラグ
- BIT: SI parameter、extended broadcaster
- PMT: digital copy control、content availability、video decode control、component tag
- EIT: component、audio component、content、copy control

公式NITのterrestrial delivery system descriptorは473 MHz、8k、GI 1/4を記述していた。
従来把握していた`GuardInterval` enum値との対応に食い違いがあるため、enum表も再検証対象とする。

## 再実装方針

直接USBで任意TSを扱う場合、映像・音声・字幕・データ放送のES/PESは可能な限り
パススルーする。一方、送信先RF・ローカル放送網に依存する情報は選択的に再構成する。

1. 入力から対象サービスを1つ選択し、他サービスと孤立PIDを除去
2. 映像・音声・字幕・データ放送のPIDとペイロードは原則維持
3. PAT/PMTを選択サービスと残すPIDに合わせて再構成
4. PCRは可能なら元PCRを維持し、必要な場合だけ補正または専用PIDへ再配置
5. Network ID、TSID、ONID、Service IDを新しいローカル放送網として採番
6. NIT/SDT/BIT/TOTと必要なEITを生成
7. RF設定と一致するterrestrial delivery system/TS information descriptorを生成
8. 変調容量と一致するCBRへnull stuffing
9. 入力が既に単一サービスかつ各表が送信条件と一致する場合は、変更を最小限にする

mnservice出力は必要なPSI/SI構造を知るためのgolden TSとして使うが、映像・音声の
再エンコード方式まで模倣する必要はない。最初の実装目標は「元の映像・音声を維持したまま、
公式と同等に自己完結した単一サービスTS」を作り、RFへ出す前にTSDuckで自動検証すること。

## 選択的リマックスの実装（2026-07-30）

GUIの「TSDuckでチャンネル情報・EPGを反映」は、入力TSの先頭サービスを自動選択し、
現在は次の順に処理する。

1. `zap --stuffing --eit`で対象サービスだけを残す（字幕・DSM-CCデータ放送・ECMも保持）
2. `svrename`でService IDを`0x5C08`へ変更
3. PAT/SDTのTSID/ONIDを`0x7E81`へ統一
4. GUIの周波数、地域、リモコンキー、局名、FFT、GIからNITを生成
5. 同じONIDのBITを生成
6. GUIの番組名・説明から、同じSID/TSID/ONIDを持つEITを生成

NIT/BITは`zap`後に元PIDが残らないため、`inject --replace`ではなくnull packetから新しい
PIDを生成する。オフライン出力で、1サービス、SID `0x5C08`、TSID/ONID `0x7E81`、
473 MHz、地域23、リモコンキー1、およびPID `0x0010`/`0x0024`のNIT/BITを確認した。
元サービスの映像・音声・字幕・DSM-CC PIDは維持されている。

`0x7E81`/`0x5C08`は公式出力を基準にした暫定固定値である。複数ローカル局の採番UIと、
入力サービス選択UI、変調容量に合わせた厳密なPCR/CBR調整は次の課題として残る。また、
PSI/SIの構造が正しくなったことと、直接USB bulk経路に残る高TEI問題の解決は別である。

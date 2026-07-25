# USBプロトコル解析メモ

## 対象

`mnservice.exe`（ネイティブサービス）が XHEAD-USB 実機と行う生のUSB通信。
gRPC (`docs/protocol/`) はあくまで **PC内の GUI⇔サービス間** の通信であり、実機との
通信プロトコルとは別レイヤ。ここが未解析の最後のブラックボックス。

## 現状の環境

- 実機は `VID_17A7&PID_0008` として認識。
- **確認済み(2026-07-24)**: ドライバクラスが **libusbk devices** (provider libusbK, v3.1.0.0,
  `oem122.inf`) になっており、公式アプリはこの状態で実機接続に失敗する
  （`XHEAD Studio: XHEAD-USBの接続に失敗しました。` ダイアログ）。
  - デバイスの `CompatibleID` に `USB\MS_COMP_WINUSB` が含まれており、これはデバイス
    ファームウェア自身がMS OS Descriptor経由のWinUSB自動バインドに対応していることを示す。
    つまり本来のドライバは **WinUSB**（Microsoft純正・署名済み）であり、専用ベンダードライバ
    ではないと推定される。
  - `C:\sdrsharp-x86\zadig.exe` がこのマシンに存在しており、RTL-SDR用ドライバ導入時に
    誤ってXHEAD-USBの方のドライバもlibusbKへ差し替えてしまったと推測される。
  - **対処法**: デバイスマネージャーでドライバを削除→USB抜き差しで再列挙させ、WinUSBへの
    自動バインドを待つ。または Zadig で明示的に XHEAD-USB → WinUSB を選んで
    "Replace Driver" する（RTL-SDR側のデバイスと誤選択しないよう要注意）。
  - `mnservice.exe` は gRPC (localhost:50051) 自体は正常に待受けており、この問題は
    純粋に `mnservice.exe` ⇔ 実機のUSBオープンの失敗（WinUSB API呼び出し失敗）と考えられる。
    自作ツール (`tools/custom_sender`) で `connectService` した際も
    `Status=StatusOffline` が返り、実機非接続状態と整合する。
- Wireshark + USBPcap はインストール済み (`C:\Program Files\Wireshark`, `C:\Program Files\USBPcap`)。

## 試行結果 (2026-07-25): USBPcapでは実データが見えなかった【未解決】

`tshark`（管理者権限で実行、`IsInRole(Administrator)`で確認済み）で`\\.\USBPcap3`
（実機が列挙されているルートハブ、`usb.device_address == 19`で確認）を対象に、
`mnservice.exe`をキャプチャ開始後に再起動→自作ツールでフル送出パイプラインを実行、
という手順を2回試したが、**実機（アドレス`3.19`）に対する実データ転送は一切キャプチャ
できなかった**。捕捉できたのは`GET DESCRIPTOR`/`SET CONFIGURATION`の6パケットのみで、
これはキャプチャ開始時に接続済みの**全USBデバイスに対して一律に**発生する合成/注入
パケット（`USBPcapCMD.exe --inject-descriptors`と同種の挙動、アドレス1〜19の全デバイスで
全く同じ6パケットパターンが確認できた）であり、実際のI/Oではないと判明した。

同じキャプチャ内で他のデバイス（TI製USBオーディオ`0x056e`等、Elecom製`0x056e`等）は
数万パケット規模の実トラフィックが正常に見えていたため、USBPcap自体やルートハブの選択が
根本的に間違っているわけではなさそうである。実機のGET DESCRIPTOR CONFIGURATIONレスポンスを
デコードしたところ、`bEndpointAddress 0x81`(BULK IN)と`0x01`(BULK OUT)の2エンドポイントが
`bmAttributes 0x02`（BULK転送）で存在することを確認しており、USBPcapが苦手とすることで
知られるIsochronous転送が原因という説も否定される。管理者権限も確認済みなので、それも
原因ではない。

一方で、RTL-SDRループバック検証（[tools/rtlsdr_analysis](../rtlsdr_analysis)）では
`Constellation`/`Bandwidth`/`RF電力`の変更が実際にスペクトラム上へ反映されることを確認して
おり、**mnservice.exeが実機と何らかの形で通信していること自体は確実**。USBPcapで見えない
理由は未解決（`WinUsb_ReadPipe`/`WinUsb_WritePipe`のI/Oパターンとの相性、または別の捕捉手法
が必要な可能性）。次に試すなら:

- Microsoftの`pktmon`（Windows標準のパケット監視、USB向け拡張がある場合）
- ハードウェアUSBプロトコルアナライザ（Total Phase Beagle等）
- `mnservice.exe`側をGhidra/cdbで直接調べ、`WinUsb_*`呼び出し箇所にブレークポイントを張って
  送信バッファの中身をメモリダンプで直接読む（キャプチャツールを介さない方法）

## キャプチャ手順（案）

1. Wireshark を管理者権限で起動。
2. キャプチャインターフェース一覧から `USBPcap1`（root hub、実際のポート番号は
   `Get-PnpDevice` 等で事前に確認）を選択。
3. フィルタ例: `usb.device_address == <XHEAD-USBのアドレス>`
4. キャプチャ開始 → `xhead_studio.exe` で以下の操作を行い、対応するUSBトランザクションを記録:
   - アプリ起動直後（列挙・ベンダーコマンドでの初期化）
   - チャンネル/変調パラメータの変更
   - 放送開始 (`CmdChannelStart` 相当)
   - 放送停止
   - PowerLevel変更（連続的な値なのでコマンドの差分が追いやすい）
5. `.pcapng` として `captures/usb/` に保存（Gitには含めない。解析用に手元で保持）。

## 解析の視点

- Bulk転送（映像データ本体=TS）とControl転送（設定コマンド）を分離する。
- Controlトランザクション (`bmRequestType`/`bRequest`/`wValue`/`wIndex`) の一覧を取り、
  GUIでのパラメータ変更操作とタイムスタンプを突き合わせて意味を推定する。
- `mnFirmwareUSB.cs` / `mnControlCode.cs`（デコンパイル済み, `decompiled/mnClientDotNet/mnFramework/`）
  に `Progress` / `Param` / `Firmware` という制御コード種別があり、ファームウェア書き換えも
  同じUSBチャンネル経由と推測される。ファームウェア関連のキャプチャ・改変は特に慎重に扱うこと
  （実機を壊すリスクがあるため、まずは読み取り専用の観測に留める）。

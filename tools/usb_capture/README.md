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

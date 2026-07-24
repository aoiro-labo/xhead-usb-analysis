# USBプロトコル解析メモ

## 対象

`mnservice.exe`（ネイティブサービス）が XHEAD-USB 実機と行う生のUSB通信。
gRPC (`docs/protocol/`) はあくまで **PC内の GUI⇔サービス間** の通信であり、実機との
通信プロトコルとは別レイヤ。ここが未解析の最後のブラックボックス。

## 現状の環境

- 実機は `VID_17A7&PID_0008` として認識。
- ドライバクラスが **libusbk devices** になっている（2026-07-24時点で確認）。
  標準のベンダードライバではなく libusbK に置き換わっている状態で、`C:\sdrsharp-x86\zadig.exe`
  がこのマシンに存在する（RTL-SDR用ドライバ導入時にZadigで意図せずXHEAD-USBの方の
  ドライバも差し替えてしまった可能性がある）。
  - **要確認**: 現在のドライバ状態で公式アプリ (`xhead_studio.exe` → `mnservice.exe`)
    が正常に実機を認識・送出できるか。もし認識できない場合、解析目的で意図的に
    libusbK/WinUSBへ差し替えたあと元のベンダードライバへ戻す必要があるかもしれない。
  - デバイスマネージャーで `XHEAD-USB` のドライバの詳細（`.inf`/プロバイダー名）を確認し、
    必要なら「ドライバーを元に戻す」または公式インストーラの再実行でベンダードライバに
    復元できるか確認する。
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

# XHEAD-USB 解析プロジェクト

マイコンソフト製の超小型USB接続OFDM変調器「[XHEAD-USB](https://www.micomsoft.co.jp/xhead-usb.html)」について、

- 公式アプリ「XHEAD-STUDIO」の挙動・通信プロトコルの解析
- 公式アプリより自由度の高い設定を行える独自送出ツールの開発
- 実機の解析結果・手法のドキュメント化

を行うプロジェクトです。

## 検証環境

- XHEAD-USB 実機 + PC(Windows)をUSB接続
- XHEAD-USBのRF出力(同軸)を、SMA変換コネクタ経由でRTL-SDRの入力にループバック接続
  （電波を実際に空中線から放射せず、有線ループバックで送出信号を安全に受信・解析する構成）

## ディレクトリ構成

```
docs/                          解析ドキュメント
  architecture.md               XHEAD-STUDIOの全体アーキテクチャ、隠しDebugモードの発見など
  gui_debug_mode_comparison.md  通常時/Debug有効時のGUIスクリーンショット比較
  protocol/                     gRPCプロトコルのリファレンス実装非依存な再構成 (.proto + 解説)
    modulation_capabilities.md   実機で確認した変調パラメータの実際の姿(FieldID等)
  screenshots/                  上記ドキュメントで使用するスクリーンショット
tools/
  custom_sender/        独自送出ツール (C#, mnClientDotNet.dll を参照して mnservice.exe に直接接続)
  usb_capture/           USBプロトコル解析用スクリプト・メモ (USBPcap/Wireshark)
  rtlsdr_analysis/       RTL-SDRループバックによる実信号検証スクリプト
captures/              実機キャプチャデータ（大容量のためリポジトリには含めない。.gitignore参照）
decompiled/            公式アプリのデコンパイル結果（著作権上リポジトリには含めない。ローカル専用）
```

## 解析の要点（詳細は [docs/architecture.md](docs/architecture.md) を参照）

XHEAD-STUDIOは **GUI (`xhead_studio.exe`)** と **バックグラウンドサービス (`service\mnservice.exe`)** の2プロセス構成で、両者は `localhost:50051` の **gRPC** で通信しています。実機とのUSB通信は `mnservice.exe`（ネイティブバイナリ）が担当します。

最大の発見は、GUI側に **`EnableDebugMode` という隠しフラグ** が存在し、これを有効にすると変調パラメータ（Constellation / CodeRate / GuardInterval / FFT / TimeInterleave 等）をはじめ、映像・音声・コーデックの詳細設定が公式GUI上に解放されることです。このフラグは設定ファイル保存時には書き出されない仕様ですが、設定ファイルを直接編集すれば有効化できます。実際にどう変わるかは [docs/gui_debug_mode_comparison.md](docs/gui_debug_mode_comparison.md) でスクリーンショット付きにまとめています。

<p align="center">
  <img src="docs/screenshots/normal/01_出力設定_変調設定.png" width="45%" alt="通常時の変調設定タブ">
  <img src="docs/screenshots/debug/01_出力設定_変調設定.png" width="45%" alt="Debug有効時の変調設定タブ">
  <br><sub>左: 通常時／右: EnableDebugMode有効時（変調設定タブ）</sub>
</p>

さらに、GUI⇔サービス間の通信は固定メッセージではなく汎用的なプロパティツリー方式のため、公式GUIが一切参照していない設定項目がサービス側に存在する可能性があります。実機から実際に読み出した変調パラメータの完全なFieldID一覧は [docs/protocol/modulation_capabilities.md](docs/protocol/modulation_capabilities.md) を参照してください（ISDB-T以外にDVB-T2/ATSC/DTMB等のサブ構造体まで存在することが判明しています）。`tools/custom_sender` はこのサービスに直接接続し、公式GUIの制限を経由せずにフル機能へアクセスすることを目指す独自クライアントです。

## 独自送出ツール (tools/custom_sender)

C#製。公式インストール済みの `mnClientDotNet.dll` を参照して `mnservice.exe` (localhost:50051) に直接 gRPC接続します。**vendor DLLはリポジトリに含めず**、ローカルの `C:\Program Files\Micomsoft\XHEAD-STUDIO` を参照する前提です。

```
cd tools/custom_sender
dotnet build
# XHEAD-STUDIO (xhead_studio.exe) を一度起動してサービスを立ち上げた状態で:
dotnet run
```

## 免責・注意事項

- 本プロジェクトは、購入済み実機の相互運用性向上・自由度拡張を目的とした解析であり、第三者のシステムへの攻撃等は目的としていません。
- 電波法上、実際に電波を空中線から発射する場合は、出力・帯域外輻射等の技術基準を満たす必要があります。本プロジェクトの検証は基本的にRTL-SDRへの同軸ループバックで行っており、実際の運用（アンテナ接続・電波発射）を行う場合は自己責任で関連法令を確認してください。

# native_analysis

`mnservice.exe`（サービス本体、C++/ネイティブ）をGhidraとcdb（WinDbg付属のコンソール
デバッガ）で解析するためのスクリプト・手順集。gRPCプロトコルの構造だけでは説明できない
サーバー内部の前提条件（例: `CmdProgramApply`が返す`bad status`の真因）を特定する際に使った。

発見した結果自体は [docs/protocol/modulation_capabilities.md](../../docs/protocol/modulation_capabilities.md)
の「続報3」を参照。ここには再現可能な**手順**だけをまとめる。

## 前提

- Ghidra 12.1.2（`gh release download Ghidra_12.1.2_build --repo NationalSecurityAgency/ghidra`
  等で入手）
- WinDbg付属の `cdb.exe`（`C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe`）
- 解析対象: `C:\Program Files\Micomsoft\XHEAD-STUDIO\service\mnservice.exe`

## Ghidraでのインポート・自動解析（初回のみ）

```
analyzeHeadless.bat <projectDir> <projectName> -import <mnservice.exeのフルパス> -overwrite
```

初回は自動解析に約2分かかる。2回目以降、スクリプトだけ再実行したい場合は
`-noanalysis`を付けて高速化する:

```
analyzeHeadless.bat <projectDir> <projectName> -process mnservice.exe -noanalysis ^
  -scriptPath <このディレクトリ> -postScript <スクリプト名.java>
```

### スクリプト一覧

- `XHeadFindSymbol.java` — シンボルテーブルから`FailedPreconditionError`等のキーワードに
  一致するアドレスを列挙する。ブレークポイント対象アドレスを特定する第一歩。
- `XHeadFindDispatch.java` — `"unhandled command : [%d]"`文字列への参照から、コマンド
  ディスパッチャ本体を特定・デコンパイルする。
- `XHeadAnalyze.java` — 指定した1関数と、その呼び出し元（最大5件）をデコンパイルする
  汎用スクリプト。
- `XHeadProgramApplyStack.java` / `XHeadDecodeUsbLoop.java` — cdbで実際に採取したコール
  スタックのオフセット群をまとめてデコンパイルする。動的解析で得たスタックトレースを静的な
  関数本体に対応付ける際に使う（ファイル内のオフセット配列を書き換えて使い回す）。
- `XHeadFindWinUsbCalls.java` — インポートされたWindows API（`WinUsb_ControlTransfer`等）の
  実際の呼び出し元を辿る。**注意**: `getReferencesTo(externalSymbolAddr)`だけでは
  「EXTERNALシンボルを指すインポートサンク自身」しか見つからない。サンク関数自体の
  エントリポイントに対してさらに`getReferencesTo`する必要がある（このスクリプトは既にその
  2段階を実装済み）。インポート関数のサンクがvtable/関数ポインタテーブル経由でしか
  呼ばれていない場合は、`getReferencesTo`が0件を返す（`ReferenceType`が`DATA`になっている
  はず）ので、その場合はcdbでのライブブレークポイント（後述）に切り替えるのが早い。
- `XHeadFindAllBRequests.java` — 特定の「共通ヘルパー関数」（例:
  1個の`bRequest`定数を引数で受け取ってUSBベンダーコマンドを1回発行する下請け関数）への
  全呼び出し元を洗い出し、まとめてデコンパイルする。1回のライブキャプチャで観測できるのは
  実行時にたまたま通ったコードパスだけなので、**バイナリ全体を静的に検索して初めて全種類の
  コマンド（例: 読み出し専用/書き込み専用/単発/ブロック転送、等）が出揃う**、という場面で
  有効。実際にこの手法でXHEAD-USBのUSBベンダーコマンドが「アドレス設定→データ読み書き」の
  汎用レジスタバスだと判明した（詳細は
  [tools/usb_capture/README.md](../usb_capture/README.md)）。
- `XHeadFindWriteCallers.java` — `XHeadFindAllBRequests.java`と同系統だが、対象を
  複数指定して一括で呼び出し元を洗い出す版（単発/ブロック × 読み出し/書き込みの4関数を
  まとめて処理）。呼び出し元が0件（vtable経由）だった場合は、cdbライブブレークポイントに
  切り替える判断材料として使う。
- `XHeadDecodeRfPowerWriter.java` — cdbのライブキャプチャで得た「呼び出し元のRetAddr」群を
  まとめてデコンパイルする版（`XHeadProgramApplyStack.java`と似ているが、複数の異なる
  呼び出し元候補を一度に確認する用途）。この手法でPAGain/DACGain書き込みの実装
  （`FUN_14039ba70`）を特定できた。
- `XHeadFindRfCalibrationReader.java` — `getReferencesTo`で単発読み出しヘルパーの呼び出し元を
  探したが0件だった（vtable経由の間接呼び出しのため、静的参照解析の限界の実例）。
- `XHeadDecodeRfCalibrationChain.java` — 上記が空振りだったため、代わりにcdbの条件付き
  ブレークポイント（`.if`で読み出しアドレスが`0x1280`〜`0x1283`のときだけ`kb`）で得た
  呼び出し元アドレス群を直接デコンパイル。`mazo::mbroadcast::mCalibration`というRF較正
  データ読み出しクラスを発見できた（[tools/usb_capture/README.md](../usb_capture/README.md)
  「続報10」）。
- `XHeadFindBmlHandler.java` — バイナリ中の文字列（`"bml file"`・`"not exist."`）を`Memory.
  findBytes`で直接検索し、その参照元関数を洗い出してデコンパイルする版。シンボル名に頼らず
  ログ文字列から出発する手法で、`mmts_bml.cc`のファイル存在確認関数(`FUN_1400a56f0`)を
  特定できた（[docs/protocol/modulation_capabilities.md](../../docs/protocol/modulation_capabilities.md)
  「続報9」）。
- `XHeadFindBmlCaller.java` / `XHeadDecodeBmlParser.java` — 存在確認関数(`FUN_1400a56f0`)の
  呼び出し元をさらに遡り、成功後に呼ばれる実際のコンテンツ処理関数を特定・デコンパイル。
  `mazo::mrevolution::mMTSBMLFile`という実在のネイティブクラスがBMLファイル処理を担って
  いることを確認できた（「続報11」）。cdbで`FUN_1400a56f0`に`bu ... "da rcx; g"`を張り、
  渡された文字列を直接ダンプして「正しいパスで呼ばれているか」を確認する手法と併用した。

## cdbでの動的解析（ライブブレークポイント）

### 起動時の注意点（ハマりどころ）

- **`-g`（初回ブレーク無視）と`-cf`（起動時コマンドファイル）は併用できない。**
  `-cf`は「最初のデバッガプロンプトで」実行される仕様だが、`-g`はまさにその最初のプロンプトを
  スキップするため、`-cf`の中身が一切実行されなくなる。`-g`は付けないこと。
- コマンドファイルは**ASCIIパスのみ**を使う。日本語（マルチバイト）パスを`.logopen`等に渡すと、
  ファイル全体の解析に失敗し、すべてのコマンドが無視される（エラーメッセージも出ない）。
- ブレークポイントは`bu <モジュール名>+<オフセット>`という遅延解決形式を使う。ASLRで実行ごとに
  ロードベースが変わっても自動的に解決される。モジュール名は`mnservice`（`.exe`を除いた形）。
  `lm`コマンドで実際に登録されているモジュール名を確認できる。

### 使い方

`cdb_break_on_function.txt`の`0xOFFSET`部分を、Ghidraで特定した関数の
イメージベース(`0x140000000`)からのオフセットに書き換えてから使う:

```
"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe" -cf cdb_break_on_function.txt ^
  "C:\Program Files\Micomsoft\XHEAD-STUDIO\service\mnservice.exe"
```

起動後、別ターミナルから`tools/custom_sender`のテストツールを実行して対象コードパスを踏ませる。
ブレーク時の`kb 15`（コールスタック）が`.logopen`で指定したログファイルに記録される。
オフセット群が判明したら`XHeadProgramApplyStack.java`でまとめてデコンパイルする。

**Tips**: 引数を直接読みたい関数（例:「アドレス」「データ」を引数で受け取るヘルパー関数）が
分かっている場合は、`db rdx L18`のような生メモリダンプで16進を手動デコードするより、
ブレークポイントコマンドで`r rcx; r r8; r r9`のようにレジスタを直接ダンプする方が
圧倒的に速く読みやすい（x64 Windows呼び出し規約: 第1引数=rcx, 第2引数=rdx, 第3引数=r8,
第4引数=r9）。実際に`FUN_140088500(this, status_out, address, data)`という4引数の
ヘルパーに対して`bu mnservice+0x88500 "r rcx; r r8; r r9; kb 10; g"`のようなブレークポイントを
張ることで、アドレス・データのペアを直接、手動hexデコードなしで取得できた
（[tools/usb_capture/README.md](../usb_capture/README.md)「続報5」）。

**Tips2: 「読み出し」系ヘルパーで実際に読めた値を知りたい場合、関数エントリの引数
（出力バッファへのポインタ）を見るだけでは不十分**——エントリ時点ではまだ結果が
書き込まれていない。`gu`（現在の関数からreturnするまで実行）を挟んでから、エントリ時に
保存しておいたポインタの中身を読むと実際の値が取れる:

```
bu mnservice+0x87920 "r $t0 = r8; r $t1 = r9; gu; .printf \"READ addr=0x%x -> data=0x%x\n\", $t0, poi($t1); g"
```

`r8`(アドレス引数)と`r9`(出力バッファポインタ)をエントリ時にcdbの疑似レジスタ`$t0`/`$t1`へ
退避 → `gu`でreturnまで実行（この間に`r9`自体は呼び出し先で破壊される可能性があるが、
退避した`$t1`は無事）→ `poi($t1)`で出力バッファの中身（＝実際に読めたレジスタ値）を
表示、という流れ。これで単発読み出しヘルパーの「アドレス→実際の値」対応表を直接得られる
（[tools/usb_capture/README.md](../usb_capture/README.md)「続報9」で新しいレジスタ帯
`0x0020`〜`0x0029`の発見に使用）。

### オブジェクトの実行時の型を知る（MSVC RTTI手動解決）

Ghidraが型復元できていないポインタでも、実行中にRTTIを手動で辿ればクラス名が分かる場合が
多い（PDBがなくても、RTTIが有効なビルドなら`.rdata`にマングル名の文字列が残っている）:

```
r $t0 = poi(rcx)              ; vtableポインタ
r $t1 = poi($t0-8)            ; CompleteObjectLocatorポインタ
r $t2 = mnservice + dwo($t1+0xc)  ; TypeDescriptorのアドレス（x64はRVA、imagebase基準）
da $t2+0x10                   ; マングル名（".?AVClassName@ns1@ns2@@"形式）を表示
```

この手法で`CmdProgramApply`失敗の原因オブジェクトが`mazo::micomsoft::mPSEncoder`という
エンコーダクラスであることを特定した。

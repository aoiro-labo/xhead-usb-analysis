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

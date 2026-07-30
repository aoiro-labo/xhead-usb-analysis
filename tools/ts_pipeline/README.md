# TS pipeline

XHEAD-USBへ渡す前のTSをTSDuckで解析・分離・EIT加工するラッパー。
入力ファイルは上書きせず、加工モードは必ず別の`-Output`を要求する。

```powershell
# 構成確認（PAT/PMT、service、PID、bitrate）
.\xhead-ts.ps1 -Mode Inspect -InputTs input.ts

# 字幕・データ放送を含む元TSを一切加工せずコピー
.\xhead-ts.ps1 -Mode PassThrough -InputTs input.ts -Output ready.ts

# 字幕PIDなどを単一PID素材として抽出（XBML化にも使用可能）
.\xhead-ts.ps1 -Mode ExtractPid -InputTs input.ts -Output subtitle.ts -Pids 0x0114

# 番組ごとのEITを注入
.\xhead-ts.ps1 -Mode InjectEit -InputTs input.ts -Output with-epg.ts -Eit .\eit.xml
```

`InjectEit`は既定で入力20 packetごとにnull packetを1個追加し、その空きをEITへ置換する。
素材に十分なnull packetがある場合は`-Stuffing 1/1000`等で追加量を小さくできる。

## どこまで可能か

- 元TSに字幕・データ放送が正しく多重化済みなら、`PassThrough`は全PIDを保持する。
- `ExtractPid`は字幕・データ放送を分離保存できる。単一PIDなら`--make-xbml`にも渡せる。
- `InjectEit`は複数eventをservice ID単位で投入し、EIT p/fとscheduleを再構成する。
- 任意PIDを別の番組へ付け替えるにはPMTのstream typeとARIB descriptorが必要。素材を見ずに
  自動推測すると受信機互換性を壊すため、現段階では自動化していない。
- direct USBの連続bulk消費条件が未解決なので、完成TSの生成とRF送出完成は別段階である。

`eit-example.xml`のIDと時刻は例示値。`Inspect`で素材TSのservice_id/transport_stream_id/
original_network_idを確認し、実際の番組時刻へ書き換える。

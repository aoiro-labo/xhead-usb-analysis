# RTL-SDRループバックによる送出信号検証

## 構成

XHEAD-USBのRF出力(同軸) --SMA変換-- RTL-SDR入力 という有線ループバック。
空中線からの電波発射を行わずに、実際に送出されているOFDM信号を直接検証できる。

## 結果: 実信号の送出を確認済み (2026-07-25)

`tools/custom_sender`で確立した送出パイプライン（`ChannelOpen`→`ProgramAdd/Commit`→
`ChannelStart`(Source構築前)→`Source`構築→`ProgramApply`→`SourceStart`、詳細は
[docs/protocol/modulation_capabilities.md](../../docs/protocol/modulation_capabilities.md)）を
実行しながら、`rtl_power`（[rtlsdrblog/rtl-sdr-blog](https://github.com/rtlsdrblog/rtl-sdr-blog)
のWindows Releaseに同梱、`gh release download v1.3.6 --repo rtlsdrblog/rtl-sdr-blog`で入手可能。
バイナリ自体は本リポジトリには含めない）で465〜481MHzを1秒積分でスキャンし、送出前
（`rtlsdr_baseline.csv`）・送出中（`rtlsdr_active.csv`）・送出停止後
（`rtlsdr_after_stop.csv`）の3本を比較した。

```
python compare.py rtlsdr_baseline.csv rtlsdr_active.csv
```

送出中は470.2〜475.8MHz付近（約6MHz幅 = ISDB-Tのチャンネル帯域幅と一致）で
**ベースライン比+37〜39dBのパワー上昇**が一貫して観測された。設定した中心周波数
（`mModulationParam.Frequency=473000kHz`）ともほぼ一致する。送出停止後に同じ比較をすると
差分は通常のノイズフロア変動の範囲（±1〜2dB程度）まで戻り、この上昇が今回の送出操作に
起因することが確認できた（`rtlsdr_baseline.csv` vs `rtlsdr_after_stop.csv`も同梱）。

これにより、プロトコルレベルの成功（`ProgramApply`/`SourceStart`が`ResultSuccess`を返す）が
**実際のRF出力**として物理的に裏付けられた。

## 結果: Bandwidthパラメータの変更がスペクトラム上でも確認できた (2026-07-25)

`mModulationParam.Bandwidth`（FieldID=20、デフォルト`6`）を`8`に変更して同様のスキャンを行い
（`rtlsdr_bandwidth8.csv`）、ベースラインとの差分を0.5MHz刻みで集計して比較した
（`profile.py`）:

```
python profile.py rtlsdr_baseline.csv rtlsdr_active.csv 0.5      # Bandwidth=6
python profile.py rtlsdr_baseline.csv rtlsdr_bandwidth8.csv 0.5  # Bandwidth=8
```

同軸直結のため広帯域の電気的ノイズも一定量拾ってしまい、しきい値一本での帯域端検出はうまく
機能しなかった（ほぼ全域が20dB超の上昇を示す）。ただし0.5MHz刻みの平均値で見ると、
**明確に高いプラトー領域**が確認でき、その位置がBandwidth設定に応じてシフトした:

| 設定 | 高プラトー領域（目安） | 幅 |
|---|---|---|
| Bandwidth=6 | 約470.5〜475.5MHz | 約5MHz |
| Bandwidth=8 | 約469.5〜476.5MHz | 約7MHz |

特に476.0〜476.5MHz帯は、Bandwidth=6では明確な谷（+26〜30dB）だったのに対し、Bandwidth=8では
プラトー領域に含まれる高い値（+37dB台）に変化しており、Bandwidth設定が実際にOFDM信号の占有
帯域幅を変化させていることが確認できた（**変調パラメータがAPI層で受理されるだけでなく、実際に
物理層まで反映されている**ことの追加の裏付け）。

## 確認済みの手元ツール

- `rtl_power` / `rtl_sdr` 等（`rtlsdrblog/rtl-sdr-blog`のWindows Release） … コマンドラインで
  スペクトラムパワースキャンや生IQ取得ができる。今回の検証で使用。
- `C:\sdrsharp-x86` … SDR#（rtlsdr.dll / zadig.exe 同梱）。スペクトラム/ウォーターフォール表示
  による目視確認や、波形の詳細観察に使える。
- `C:\Program Files\TSDuck\bin` (`tsp.exe`, `tsanalyze.exe` 等) … MPEG-TS解析ツール群。
  ISDB-TをフルにOFDM復調してTSを得られた場合、`xcfg`の`Channel`セクション
  （PCR_PID/PMT_PID/ServiceNo/NetworkName/TSName等）が実際の送出TSと一致しているかを
  `tsanalyze`のPSI/SI解析で裏取りできる。

## 課題: RTL-SDR単体ではISDB-T信号のフル復調はできない

RTL-SDRはIQサンプルを取得できるのみで、ISDB-T(OFDM/畳み込み符号/インターリーブ)の
ハードウェア復調機能は持たない。今回はスペクトラム/電力レベルの検証（占有帯域幅・中心周波数の
一致・送出のON/OFFに連動した電力変化）で「実際に電波が出ている」ことは実証できたが、
Constellation/CodeRate/TimeInterleave等の変調パラメータがビットレベルで正しく反映されている
かまでは未検証。今後の選択肢:

1. **ソフトウェアOFDM復調を自作/流用してフルデコードする**:
   - GNU Radioや自作Pythonスクリプトで ISDB-T の同期・FFT・デマッピングを行う
   - 難易度は高いが、Constellation/CodeRate/TimeInterleave等が実際に反映されているかを
     ビットレベルで検証できる
2. **別途、市販のISDB-T USBチューナー（PX-W3U4等）を使い、TSとして直接受信する**:
   - 実機がすでに持っていれば手っ取り早い。TSDuckでそのまま解析できる。
   - 今回のRTL-SDRループバック構成とは別の追加ハードウェアが必要。

## 次のアクション（案）

- [x] スペクトラム上でXHEAD-USBの出力が確認できるか（`rtl_power`によるパワースキャン）
- [x] `mModulationParam.Bandwidth`変更時に占有帯域幅が変化するかを記録
- [ ] Constellation/FFT等、他の変調パラメータについても同様にスペクトラム形状の変化を記録
- [ ] フルOFDM復調 or 市販チューナーでのTS直接受信によるビットレベル検証

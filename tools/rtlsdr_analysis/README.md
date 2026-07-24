# RTL-SDRループバックによる送出信号検証

## 構成

XHEAD-USBのRF出力(同軸) --SMA変換-- RTL-SDR入力 という有線ループバック。
空中線からの電波発射を行わずに、実際に送出されているOFDM信号を直接検証できる。

## 確認済みの手元ツール

- `C:\sdrsharp-x86` … SDR#（rtlsdr.dll / zadig.exe 同梱）。まずはここでスペクトラム/
  ウォーターフォール表示による大まかな確認（占有帯域幅、中心周波数のズレ、PowerLevelに
  応じた振幅変化など）に使う。
- `C:\Program Files\TSDuck\bin` (`tsp.exe`, `tsanalyze.exe` 等) … MPEG-TS解析ツール群。
  ISDB-TをフルにOFDM復調してTSを得られた場合、`xcfg`の`Channel`セクション
  （PCR_PID/PMT_PID/ServiceNo/NetworkName/TSName等）が実際の送出TSと一致しているかを
  `tsanalyze`のPSI/SI解析で裏取りできる。

## 課題: RTL-SDR単体ではISDB-T信号のフル復調はできない

RTL-SDRはIQサンプルを取得できるのみで、ISDB-T(OFDM/畳み込み符号/インターリーブ)の
ハードウェア復調機能は持たない。方針としては以下のいずれか（要検討）:

1. **スペクトラム/電力レベルの検証に留める**（SDR#で十分）:
   - Modulation.PowerLevel を変えたときのRF出力レベルの相対変化
   - Channel(周波数)設定と実際の中心周波数の一致
   - GuardInterval/FFTサイズ変更時の占有帯域幅の変化の有無
2. **ソフトウェアOFDM復調を自作/流用してフルデコードする**:
   - GNU Radioや自作Pythonスクリプトで ISDB-T の同期・FFT・デマッピングを行う
   - 難易度は高いが、Constellation/CodeRate/TimeInterleave等が実際に反映されているかを
     ビットレベルで検証できる
3. **別途、市販のISDB-T USBチューナー（PX-W3U4等）を使い、TSとして直接受信する**:
   - 実機がすでに持っていれば手っ取り早い。TSDuckでそのまま解析できる。
   - 今回のRTL-SDRループバック構成とは別の追加ハードウェアが必要。

現状は 1. を最初のマイルストーンとし、必要に応じて 2 or 3 を検討する。

## 次のアクション（案）

- [ ] SDR#でXHEAD-USBのデフォルト出力(UHF_13, 473.143MHz)を実際に受信できるか確認
- [ ] PowerLevel 80→100 の変化がスペクトラム上で確認できるか
- [ ] `docs/protocol/` （プロトコル解析）で判明した設定項目を実際に変更しながら
      スペクトラムの変化を記録し、`docs/architecture.md` にフィードバックする

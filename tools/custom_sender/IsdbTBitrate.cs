using System;

namespace XHeadSender
{
    internal static class IsdbTBitrate
    {
        // mnservice公式出力の実測値: QPSK, CR 5/6, GI 1/16, 13 segments.
        // 同一ハードウェアでの比率計算に使い、固定の規格表の丸め差も避ける。
        private const double ReferenceBitrate = 7159151.0;

        private static readonly double[] BitsPerCarrier = { 2.0, 2.0, 4.0, 6.0 };
        private static readonly double[] CodeRates = { 1.0 / 2, 2.0 / 3, 3.0 / 4, 5.0 / 6, 7.0 / 8 };
        private static readonly double[] GuardIntervals = { 1.0 / 32, 1.0 / 16, 1.0 / 8, 1.0 / 4 };

        public static long Estimate13SegmentBitrate(ModulationConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (cfg.Mode != 5) throw new InvalidOperationException("ISDB-T以外の容量はこの計算式の対象外です。");
            if (cfg.Constellation < 0 || cfg.Constellation >= BitsPerCarrier.Length ||
                cfg.CodeRate < 0 || cfg.CodeRate >= CodeRates.Length ||
                cfg.GuardInterval < 0 || cfg.GuardInterval >= GuardIntervals.Length)
                throw new ArgumentOutOfRangeException(nameof(cfg), "ISDB-T変調パラメータが範囲外です。");

            double modulationRatio = BitsPerCarrier[cfg.Constellation] / 2.0;
            double codeRateRatio = CodeRates[cfg.CodeRate] / (5.0 / 6);
            double guardRatio = (1.0 + 1.0 / 16) / (1.0 + GuardIntervals[cfg.GuardInterval]);
            return (long)Math.Round(ReferenceBitrate * modulationRatio * codeRateRatio * guardRatio);
        }
    }
}
